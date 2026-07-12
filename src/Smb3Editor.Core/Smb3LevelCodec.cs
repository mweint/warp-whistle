namespace Smb3Editor.Core;

public static class Smb3LevelCodec
{
    public const int HeaderLength = 9;

    public static OperationResult<LevelDocument> Decode(RomImage rom, LevelLocation location)
    {
        var diagnostics = new List<Diagnostic>();
        var layoutRange = rom.ReadRange(location.LayoutOffset, location.LayoutCapacity, $"{location.DisplayName} layout");
        var enemyRange = rom.ReadRange(location.EnemyOffset, location.EnemyCapacity, $"{location.DisplayName} enemies");

        if (!layoutRange.IsSuccess || !enemyRange.IsSuccess)
        {
            return OperationResult<LevelDocument>.Failure(
                layoutRange.Diagnostics.Concat(enemyRange.Diagnostics).ToArray());
        }

        var layout = layoutRange.Value.Span;
        var enemies = enemyRange.Value.Span;

        if (location.LayoutDataSignature.Count > 0 &&
            (layout.Length < HeaderLength + location.LayoutDataSignature.Count ||
             !layout.Slice(HeaderLength, location.LayoutDataSignature.Count).SequenceEqual(location.LayoutDataSignature.ToArray())))
        {
            return OperationResult<LevelDocument>.Failure(
                Diagnostics.Error("LEVEL_SIGNATURE", $"{location.DisplayName} does not match the verified layout signature for this ROM profile."));
        }

        if (location.EnemySignature.Count > 0 &&
            (enemies.Length < location.EnemySignature.Count ||
             !enemies[..location.EnemySignature.Count].SequenceEqual(location.EnemySignature.ToArray())))
        {
            return OperationResult<LevelDocument>.Failure(
                Diagnostics.Error("ENEMY_SIGNATURE", $"{location.DisplayName} does not match the verified enemy signature for this ROM profile."));
        }

        if (layout.Length < HeaderLength)
        {
            return OperationResult<LevelDocument>.Failure(
                Diagnostics.Error("LEVEL_HEADER", "The level header is truncated."));
        }

        var header = new LevelHeader(
            ReadUInt16(layout, 0),
            ReadUInt16(layout, 2),
            layout[4],
            layout[5],
            layout[6],
            layout[7],
            layout[8]);

        var elements = new List<LevelElement>();
        var cursor = HeaderLength;
        var terminated = false;

        while (cursor < layout.Length)
        {
            if (layout[cursor] == 0xFF)
            {
                cursor++;
                terminated = true;
                break;
            }

            if (cursor > layout.Length - 3)
            {
                diagnostics.Add(Diagnostics.Error("LEVEL_TRUNCATED", "A level command is truncated before its terminator."));
                break;
            }

            var first = layout[cursor];
            var second = layout[cursor + 1];
            var shape = layout[cursor + 2];
            var kind = (first & 0xE0) == 0xE0
                ? LevelElementKind.Junction
                : (shape & 0xF0) == 0
                    ? LevelElementKind.FixedGenerator
                    : LevelElementKind.VariableGenerator;

            var generatorId = kind switch
            {
                LevelElementKind.Junction => -1,
                LevelElementKind.FixedGenerator => ((first & 0xE0) >> 1) | (shape & 0x0F),
                _ => ((first >> 5) * 15) + (shape >> 4) - 1
            };

            byte? extra = null;
            var commandLength = 3;
            if (kind == LevelElementKind.VariableGenerator && location.FourByteGeneratorIds.Contains(generatorId))
            {
                if (cursor + 3 >= layout.Length)
                {
                    diagnostics.Add(Diagnostics.Error("LEVEL_TRUNCATED", "A four-byte generator is missing its final parameter."));
                    break;
                }

                extra = layout[cursor + 3];
                commandLength = 4;
            }

            var x = header.IsVertical ? second & 0x0F : second;
            var y = header.IsVertical
                ? ((second >> 4) * 15) + (first & 0x0F)
                : first & 0x1F;
            elements.Add(new LevelElement(elements.Count, kind, generatorId, x, y, shape, extra, first, second, x, y));
            cursor += commandLength;
        }

        if (!terminated)
        {
            diagnostics.Add(Diagnostics.Error("LEVEL_TERMINATOR", "The level has no $FF terminator inside its verified allocation."));
        }

        if (enemies.Length < 2)
        {
            diagnostics.Add(Diagnostics.Error("ENEMY_TRUNCATED", "The enemy stream is truncated."));
        }

        var enemyHeader = enemies.Length > 0 ? enemies[0] : (byte)0;
        var enemyElements = new List<EnemyElement>();
        var enemyCursor = 1;
        var enemiesTerminated = false;
        while (enemyCursor < enemies.Length)
        {
            if (enemies[enemyCursor] == 0xFF)
            {
                enemyCursor++;
                enemiesTerminated = true;
                break;
            }

            if (enemyCursor > enemies.Length - 3)
            {
                diagnostics.Add(Diagnostics.Error("ENEMY_TRUNCATED", "An enemy command is truncated before its terminator."));
                break;
            }

            var second = enemies[enemyCursor + 1];
            var third = enemies[enemyCursor + 2];
            var enemyX = header.IsVertical ? second & 0x0F : second;
            var enemyY = header.IsVertical
                ? ((third >> 4) * 15) + (third & 0x0F)
                : third & 0x1F;
            var flags = header.IsVertical ? (byte)0 : (byte)(third & 0xE0);
            enemyElements.Add(new EnemyElement(
                enemyElements.Count,
                enemies[enemyCursor],
                enemyX,
                enemyY,
                flags,
                second,
                third,
                enemyX,
                enemyY));
            enemyCursor += 3;
        }

        if (!enemiesTerminated)
        {
            diagnostics.Add(Diagnostics.Error("ENEMY_TERMINATOR", "The enemy stream has no $FF terminator inside its verified allocation."));
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return OperationResult<LevelDocument>.Failure(diagnostics.ToArray());
        }

        diagnostics.Add(Diagnostics.Info(
            "LEVEL_LOADED",
            $"Loaded {elements.Count} generators/junctions and {enemyElements.Count} enemies."));

        return OperationResult<LevelDocument>.Success(
            new LevelDocument(
                location.AreaId,
                location.DisplayName,
                location.Tileset,
                header,
                elements,
                enemyHeader,
                enemyElements,
                cursor,
                enemyCursor,
                rom.Profile.Levels.Values
                    .Where(candidate => string.Equals(candidate.AreaId, location.AreaId, StringComparison.Ordinal))
                    .Select(static candidate => candidate.DisplayName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static name => name, StringComparer.Ordinal)
                    .ToArray()),
            diagnostics);
    }

    public static OperationResult<byte[]> EncodeLayout(LevelDocument document)
    {
        var diagnostics = Validate(document);
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return OperationResult<byte[]>.Failure(diagnostics.ToArray());
        }

        using var stream = new MemoryStream();
        WriteUInt16(stream, document.Header.AlternateLayoutAddress);
        WriteUInt16(stream, document.Header.AlternateEnemyAddress);
        stream.WriteByte(document.Header.SizeAndStartY);
        stream.WriteByte(document.Header.PalettesAndStartX);
        stream.WriteByte(document.Header.TilesetAndScroll);
        stream.WriteByte(document.Header.BackgroundAndAction);
        stream.WriteByte(document.Header.MusicAndTime);

        foreach (var element in document.Elements)
        {
            if (element.Kind == LevelElementKind.Junction)
            {
                stream.WriteByte(element.OriginalFirstByte);
                stream.WriteByte(element.OriginalSecondByte);
                stream.WriteByte(element.Shape);
                continue;
            }

            byte first;
            byte second;
            if (document.Header.IsVertical)
            {
                first = element.Y == element.OriginalY
                    ? element.OriginalFirstByte
                    : (byte)((element.OriginalFirstByte & 0xF0) | (element.Y % 15));
                second = element.X == element.OriginalX && element.Y == element.OriginalY
                    ? element.OriginalSecondByte
                    : (byte)(((element.Y / 15) << 4) | (element.X & 0x0F));
            }
            else
            {
                first = element.Y == element.OriginalY
                    ? element.OriginalFirstByte
                    : (byte)((element.OriginalFirstByte & 0xE0) | (element.Y & 0x1F));
                second = element.X == element.OriginalX
                    ? element.OriginalSecondByte
                    : (byte)element.X;
            }

            stream.WriteByte(first);
            stream.WriteByte(second);
            stream.WriteByte(element.Shape);
            if (element.ExtraParameter is byte extra)
            {
                stream.WriteByte(extra);
            }
        }

        stream.WriteByte(0xFF);
        return OperationResult<byte[]>.Success(stream.ToArray(), diagnostics);
    }

    public static OperationResult<byte[]> EncodeEnemies(LevelDocument document)
    {
        var diagnostics = Validate(document);
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return OperationResult<byte[]>.Failure(diagnostics.ToArray());
        }

        using var stream = new MemoryStream();
        stream.WriteByte(document.EnemyHeader);
        foreach (var enemy in document.Enemies)
        {
            stream.WriteByte(enemy.Id);
            if (document.Header.IsVertical)
            {
                var second = enemy.OriginalSecondByte is byte originalSecond && enemy.X == enemy.OriginalX
                    ? originalSecond
                    : (byte)(((enemy.OriginalSecondByte ?? 0) & 0xF0) | (enemy.X & 0x0F));
                var third = enemy.OriginalThirdByte is byte originalThird && enemy.Y == enemy.OriginalY
                    ? originalThird
                    : (byte)(((enemy.Y / 15) << 4) | (enemy.Y % 15));
                stream.WriteByte(second);
                stream.WriteByte(third);
            }
            else
            {
                stream.WriteByte((byte)enemy.X);
                stream.WriteByte((byte)(enemy.Flags | (enemy.Y & 0x1F)));
            }
        }

        stream.WriteByte(0xFF);
        return OperationResult<byte[]>.Success(stream.ToArray(), diagnostics);
    }

    public static IReadOnlyList<Diagnostic> Validate(LevelDocument document)
    {
        var diagnostics = new List<Diagnostic>();
        var maxX = document.Header.IsVertical ? 15 : 255;
        var maxY = document.Header.IsVertical ? (document.Header.ScreenCount * 15) - 1 : 26;

        foreach (var element in document.Elements)
        {
            if (element.X is < 0 or > 255 || element.Y is < 0 or > 239)
            {
                diagnostics.Add(Diagnostics.Error("LEVEL_POSITION", $"Generator {element.Index + 1} is outside the encodable position range."));
            }

            if (element.X > maxX || element.Y > maxY)
            {
                diagnostics.Add(Diagnostics.Warning("LEVEL_BOUNDS", $"Generator {element.Index + 1} lies outside the visible level bounds."));
            }
        }

        foreach (var enemy in document.Enemies)
        {
            var enemyMaxX = document.Header.IsVertical ? 15 : 255;
            var enemyMaxY = document.Header.IsVertical ? (document.Header.ScreenCount * 15) - 1 : 31;
            if (enemy.X is < 0 || enemy.X > enemyMaxX || enemy.Y is < 0 || enemy.Y > enemyMaxY)
            {
                diagnostics.Add(Diagnostics.Error("ENEMY_POSITION", $"Enemy {enemy.Index + 1} is outside the encodable position range."));
            }
        }

        if (document.Enemies.Count > 48)
        {
            diagnostics.Add(Diagnostics.Warning("ENEMY_COUNT", "This area has more than 48 enemies and may overload the original engine."));
        }

        return diagnostics;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, int offset) =>
        (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)value);
        stream.WriteByte((byte)(value >> 8));
    }
}
