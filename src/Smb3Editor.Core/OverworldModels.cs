namespace Smb3Editor.Core;

/// <summary>Vanilla SMB3 overworld data. Tile layouts retain their verified, fixed-size source slots.</summary>
public sealed record OverworldDocument(
    int World,
    int LayoutOffset,
    int TilePalette,
    int SpritePalette,
    int AnimationSpeed,
    int StartRow,
    bool ScrollEnabled,
    IReadOnlyList<byte> Tiles,
    IReadOnlyList<OverworldLevelPointer> LevelPointers,
    IReadOnlyList<OverworldLockBridge> LocksAndBridges)
{
    public const int ScreenWidth = 16;
    public const int ScreenHeight = 9;
    public const int FirstMapRow = 2;
    public int ScreenCount => Tiles.Count / (ScreenWidth * ScreenHeight);
    public int Width => ScreenCount * ScreenWidth;
    public override string ToString() => $"World {World + 1}";

    /// <summary>
    /// SMB3 stores each 16×9 map screen as one contiguous block, rather than
    /// storing every row across the full map width.
    /// </summary>
    public int TileIndex(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= ScreenHeight)
            throw new ArgumentOutOfRangeException();
        return ((x / ScreenWidth) * ScreenWidth * ScreenHeight) + (y * ScreenWidth) + (x % ScreenWidth);
    }

    public byte TileAt(int x, int y) => Tiles[TileIndex(x, y)];

    public OverworldDocument WithTile(int x, int y, byte tile)
    {
        if (x < 0 || x >= Width || y < 0 || y >= ScreenHeight) return this;
        var tiles = Tiles.ToArray();
        tiles[TileIndex(x, y)] = tile;
        return this with { Tiles = tiles };
    }

    public OverworldDocument WithLevelPointer(OverworldLevelPointer pointer)
    {
        if (pointer.Index < 0 || pointer.Index >= LevelPointers.Count) return this;
        var pointers = LevelPointers.ToArray();
        pointers[pointer.Index] = pointer;
        return this with { LevelPointers = pointers };
    }

    public OverworldDocument WithLockBridge(OverworldLockBridge item)
    {
        var locks = LocksAndBridges.ToArray();
        var index = Array.FindIndex(locks, existing => existing.Slot == item.Slot);
        if (index < 0) return this;
        locks[index] = item;
        return this with { LocksAndBridges = locks };
    }
}

public sealed record OverworldLevelPointer(
    int Index,
    int Screen,
    int Column,
    int Row,
    int ObjectSet,
    ushort LevelOffset,
    ushort EnemyOffset,
    int PositionXOffset,
    int PositionYOffset,
    int LevelPointerOffset,
    int EnemyPointerOffset);

/// <summary>One pre-existing post-fortress lock or bridge event slot.</summary>
public sealed record OverworldLockBridge(
    int World,
    int Slot,
    int Screen,
    int Column,
    int Row,
    byte ReplacementTile,
    int PositionOffset,
    int RowOffset,
    int ReplacementTileOffset);

public static class Smb3OverworldParser
{
    private const int BaseOffset = 0x10;
    private const int WorldMapBase = BaseOffset + 0xE000;
    private const int WorldCount = 9;
    private const int MaxScreens = 4;
    private const int FirstValidRow = 2;

    // PRG1 constants from the refreshed Foundry/Scribe reference and the PRG1 disassembly.
    private const int LayoutList = 0x185A8;
    private const int TilePalettes = 0x1842D;
    private const int SpritePalettes = 0x18436;
    private const int MapYStarts = 0x3C39A;
    private const int MapScroll = 0x14F44;
    private const int StructureOffsets = 0x193DA;
    private const int YPositionLists = 0x193EC;
    private const int XPositionLists = 0x193FE;
    private const int EnemyOffsetLists = 0x19410;
    private const int LevelOffsetLists = 0x19422;
    private const int FortressFxRows = 0x14855;
    private const int FortressFxPositions = 0x14866;
    private const int FortressFxReplacementTiles = 0x14877;
    private const int FortressFxWorldSlots = 0x14888;
    private static readonly int[] FortressFxCounts = [1, 1, 2, 2, 2, 3, 2, 4, 0];

    public static OperationResult<IReadOnlyList<OverworldDocument>> Parse(RomImage source)
    {
        if (!string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal))
        {
            return OperationResult<IReadOnlyList<OverworldDocument>>.Failure(
                Diagnostics.Error("OVERWORLD_PROFILE", "The overworld editor currently supports verified US PRG1 ROMs only."));
        }

        var diagnostics = new List<Diagnostic>();
        var maps = new List<OverworldDocument>(WorldCount);
        for (var world = 0; world < WorldCount; world++)
        {
            if (!TryParseWorld(source.Bytes, world, out var map, out var error))
            {
                diagnostics.Add(Diagnostics.Error("OVERWORLD_PARSE", $"World {world + 1} could not be parsed: {error}"));
                continue;
            }

            maps.Add(map!);
        }

        return diagnostics.Any(static d => d.Severity == DiagnosticSeverity.Error)
            ? OperationResult<IReadOnlyList<OverworldDocument>>.Failure(diagnostics.ToArray())
            : OperationResult<IReadOnlyList<OverworldDocument>>.Success(maps, diagnostics);
    }

    private static bool TryParseWorld(byte[] bytes, int world, out OverworldDocument? map, out string error)
    {
        map = null;
        error = string.Empty;
        var layoutPointerAddress = LayoutList + (world * 2);
        var layoutOffset = WorldMapBase + ReadU16(bytes, layoutPointerAddress);
        if (!InRange(bytes, layoutOffset, 1))
        {
            error = $"tile layout pointer ${layoutOffset:X} is outside the ROM";
            return false;
        }

        var delimiter = Array.IndexOf(bytes, (byte)0xFF, layoutOffset);
        if (delimiter < 0 || delimiter - layoutOffset == 0 || (delimiter - layoutOffset) % (OverworldDocument.ScreenWidth * OverworldDocument.ScreenHeight) != 0)
        {
            error = "tile layout has no valid screen-aligned terminator";
            return false;
        }

        var tiles = bytes.AsSpan(layoutOffset, delimiter - layoutOffset).ToArray();
        if (tiles.Length / (OverworldDocument.ScreenWidth * OverworldDocument.ScreenHeight) > MaxScreens)
        {
            error = "tile layout exceeds the vanilla four-screen limit";
            return false;
        }

        var structureOffset = WorldMapBase + ReadU16(bytes, StructureOffsets + (world * 2));
        if (!InRange(bytes, structureOffset, 4))
        {
            error = "level-pointer structure is outside the ROM";
            return false;
        }

        var yStart = WorldMapBase + ReadU16(bytes, YPositionLists + (world * 2));
        var xStart = WorldMapBase + ReadU16(bytes, XPositionLists + (world * 2));
        var count = xStart - yStart;
        if (count < 0 || count > 0xFF || !InRange(bytes, yStart, count) || !InRange(bytes, xStart, count))
        {
            error = "level-pointer position lists are invalid";
            return false;
        }

        var enemyList = WorldMapBase + ReadU16(bytes, EnemyOffsetLists + (world * 2));
        var levelList = WorldMapBase + ReadU16(bytes, LevelOffsetLists + (world * 2));
        if (!InRange(bytes, enemyList, count * 2) || !InRange(bytes, levelList, count * 2))
        {
            error = "level-pointer address lists are invalid";
            return false;
        }

        var pointers = new List<OverworldLevelPointer>(count);
        for (var index = 0; index < count; index++)
        {
            var positionX = bytes[xStart + index];
            var positionY = bytes[yStart + index];
            var screen = positionX >> 4;
            var column = positionX & 0x0F;
            var row = positionY >> 4;
            var objectSet = positionY & 0x0F;
            if (screen >= tiles.Length / (OverworldDocument.ScreenWidth * OverworldDocument.ScreenHeight) || row < FirstValidRow || row >= FirstValidRow + OverworldDocument.ScreenHeight)
            {
                error = $"level pointer {index} has an invalid position";
                return false;
            }

            pointers.Add(new OverworldLevelPointer(
                index,
                screen,
                column,
                row,
                objectSet,
                ReadU16(bytes, levelList + (index * 2)),
                ReadU16(bytes, enemyList + (index * 2)),
                xStart + index,
                yStart + index,
                levelList + (index * 2),
                enemyList + (index * 2)));
        }

        var locks = new List<OverworldLockBridge>();
        var lockCount = FortressFxCounts[world];
        for (var index = 0; index < lockCount; index++)
        {
            var slot = bytes[FortressFxWorldSlots + (world * 4) + index];
            var packedPosition = bytes[FortressFxPositions + slot];
            locks.Add(new OverworldLockBridge(world, slot, packedPosition & 0x0F, packedPosition >> 4,
                bytes[FortressFxRows + slot] >> 4, bytes[FortressFxReplacementTiles + slot],
                FortressFxPositions + slot, FortressFxRows + slot, FortressFxReplacementTiles + slot));
        }

        map = new OverworldDocument(
            world,
            layoutOffset,
            bytes[TilePalettes + world],
            bytes[SpritePalettes + world],
            bytes[0x17C11 + (world * 4)],
            bytes[MapYStarts + world],
            (bytes[MapScroll + world] & 0x10) == 0,
            tiles,
            pointers,
            locks);
        return true;
    }

    private static ushort ReadU16(byte[] bytes, int offset) =>
        (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static bool InRange(byte[] bytes, int offset, int length) =>
        offset >= 0 && length >= 0 && offset <= bytes.Length - length;
}

/// <summary>
/// Serializes only fixed-size PRG1 overworld tile layouts. It never relocates a
/// map, changes a terminator, or changes any pointer table.
/// </summary>
public static class Smb3OverworldSerializer
{
    public static IReadOnlyList<Diagnostic> ApplyTileOverrides(
        ProjectDocumentV2 project,
        RomImage source,
        byte[] output)
    {
        if (project.OverworldTiles is not { Count: > 0 }) return [];

        if (!string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal))
        {
            return [Diagnostics.Error("BUILD_OVERWORLD_PROFILE", "Overworld tile export currently supports verified US PRG1 ROMs only.")];
        }

        var parsed = Smb3OverworldParser.Parse(source);
        if (!parsed.IsSuccess) return parsed.Diagnostics;

        var maps = parsed.Value!.ToDictionary(static map => map.World);
        var diagnostics = new List<Diagnostic>();
        foreach (var overrideTiles in project.OverworldTiles.OrderBy(static item => item.World))
        {
            if (!maps.TryGetValue(overrideTiles.World, out var map))
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_WORLD", $"Overworld {overrideTiles.World + 1} is not present in the verified PRG1 map set."));
                continue;
            }

            if (overrideTiles.Tiles.Count != map.Tiles.Count)
            {
                diagnostics.Add(Diagnostics.Error(
                    "BUILD_OVERWORLD_SIZE",
                    $"World {map.World + 1} has {overrideTiles.Tiles.Count} tiles, but its fixed vanilla slot requires {map.Tiles.Count}."));
                continue;
            }

            if (map.LayoutOffset < 0 || map.LayoutOffset > output.Length - map.Tiles.Count - 1 || output[map.LayoutOffset + map.Tiles.Count] != 0xFF)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_RANGE", $"World {map.World + 1}'s verified tile slot is no longer safe to write."));
                continue;
            }

            overrideTiles.Tiles.ToArray().CopyTo(output, map.LayoutOffset);
            diagnostics.Add(Diagnostics.Info("BUILD_OVERWORLD_OK", $"Compiled World {map.World + 1}: {map.Tiles.Count} fixed-size overworld tiles."));
        }

        if (diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error)) return diagnostics;

        // Parse the exact compiled bytes before they reach the normal verifier.
        // This catches a stale pointer or terminator rather than exporting a map
        // which only looked valid against the original source image.
        var verified = Smb3OverworldParser.Parse(RomImage.CreateForTesting(source.SourcePath, output, source.Profile));
        if (!verified.IsSuccess)
        {
            diagnostics.AddRange(verified.Diagnostics.Select(static item =>
                Diagnostics.Error("BUILD_OVERWORLD_VERIFY", $"Compiled overworld verification failed: {item.Message}")));
        }

        return diagnostics;
    }

    public static IReadOnlyList<Diagnostic> ApplyLevelPointerOverrides(
        ProjectDocumentV2 project,
        RomImage source,
        byte[] output)
    {
        if (project.OverworldLevelPointers is not { Count: > 0 }) return [];
        if (!string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal))
        {
            return [Diagnostics.Error("BUILD_OVERWORLD_PROFILE", "Overworld node export currently supports verified US PRG1 ROMs only.")];
        }

        var parsed = Smb3OverworldParser.Parse(source);
        if (!parsed.IsSuccess) return parsed.Diagnostics;
        var maps = parsed.Value!.ToDictionary(static map => map.World);
        var diagnostics = new List<Diagnostic>();
        foreach (var change in project.OverworldLevelPointers.OrderBy(static item => item.World).ThenBy(static item => item.Index))
        {
            if (!maps.TryGetValue(change.World, out var world) || change.Index < 0 || change.Index >= world.LevelPointers.Count)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_NODE", $"World {change.World + 1} map node {change.Index + 1} is not present in the verified PRG1 map data."));
                continue;
            }

            if (change.Screen < 0 || change.Screen >= world.ScreenCount || change.Column is < 0 or >= OverworldDocument.ScreenWidth ||
                change.Row is < OverworldDocument.FirstMapRow or >= OverworldDocument.FirstMapRow + OverworldDocument.ScreenHeight || change.ObjectSet is < 0 or > 0x0F ||
                change.LevelOffset is < 0xA000 or > 0xBFFF || change.EnemyOffset is < 0xC000 or > 0xDFFF)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_NODE_RANGE", $"World {world.World + 1} map node {change.Index + 1} has an invalid vanilla position or destination."));
                continue;
            }

            var sourceNode = world.LevelPointers[change.Index];
            output[sourceNode.PositionXOffset] = (byte)((change.Screen << 4) | change.Column);
            output[sourceNode.PositionYOffset] = (byte)((change.Row << 4) | change.ObjectSet);
            WriteU16(output, sourceNode.LevelPointerOffset, change.LevelOffset);
            WriteU16(output, sourceNode.EnemyPointerOffset, change.EnemyOffset);
            diagnostics.Add(Diagnostics.Info("BUILD_OVERWORLD_NODE_OK", $"Compiled World {world.World + 1} map node {change.Index + 1}."));
        }

        if (diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error)) return diagnostics;
        var verified = Smb3OverworldParser.Parse(RomImage.CreateForTesting(source.SourcePath, output, source.Profile));
        if (!verified.IsSuccess)
        {
            diagnostics.AddRange(verified.Diagnostics.Select(static item =>
                Diagnostics.Error("BUILD_OVERWORLD_NODE_VERIFY", $"Compiled overworld-node verification failed: {item.Message}")));
        }

        return diagnostics;
    }

    public static IReadOnlyList<Diagnostic> ApplyLockBridgeOverrides(
        ProjectDocumentV2 project,
        RomImage source,
        byte[] output)
    {
        if (project.OverworldLocksAndBridges is not { Count: > 0 }) return [];
        if (!string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal))
            return [Diagnostics.Error("BUILD_OVERWORLD_PROFILE", "Overworld lock and bridge export currently supports verified US PRG1 ROMs only.")];

        var parsed = Smb3OverworldParser.Parse(source);
        if (!parsed.IsSuccess) return parsed.Diagnostics;
        var worlds = parsed.Value!.ToDictionary(static world => world.World);
        var diagnostics = new List<Diagnostic>();
        foreach (var change in project.OverworldLocksAndBridges.OrderBy(static item => item.World).ThenBy(static item => item.Slot))
        {
            if (!worlds.TryGetValue(change.World, out var world) || world.LocksAndBridges.FirstOrDefault(item => item.Slot == change.Slot) is not { } sourceLock)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_LOCK", $"World {change.World + 1} does not have stock lock/bridge slot ${change.Slot:X2}."));
                continue;
            }
            if (change.Screen < 0 || change.Screen >= world.ScreenCount || change.Column is < 0 or >= OverworldDocument.ScreenWidth ||
                change.Row is < OverworldDocument.FirstMapRow or >= OverworldDocument.FirstMapRow + OverworldDocument.ScreenHeight)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_LOCK_RANGE", $"World {world.World + 1} lock/bridge slot ${change.Slot:X2} has an invalid map position."));
                continue;
            }

            output[sourceLock.PositionOffset] = (byte)((change.Column << 4) | change.Screen);
            // The row occupies the high nibble. Preserve the low nibble so a
            // future verified profile cannot lose adjacent format bits.
            output[sourceLock.RowOffset] = (byte)((output[sourceLock.RowOffset] & 0x0F) | (change.Row << 4));
            output[sourceLock.ReplacementTileOffset] = change.ReplacementTile;
            diagnostics.Add(Diagnostics.Info("BUILD_OVERWORLD_LOCK_OK", $"Compiled World {world.World + 1} lock/bridge slot ${change.Slot:X2}."));
        }

        if (diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error)) return diagnostics;
        var verified = Smb3OverworldParser.Parse(RomImage.CreateForTesting(source.SourcePath, output, source.Profile));
        if (!verified.IsSuccess)
            diagnostics.AddRange(verified.Diagnostics.Select(static item => Diagnostics.Error("BUILD_OVERWORLD_LOCK_VERIFY", $"Compiled lock/bridge verification failed: {item.Message}")));
        return diagnostics;
    }

    public static IReadOnlyList<Diagnostic> ApplyPaletteOverrides(ProjectDocumentV2 project, RomImage source, byte[] output)
    {
        if (project.OverworldPalettes is not { Count: > 0 }) return [];
        if (!string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal))
            return [Diagnostics.Error("BUILD_OVERWORLD_PROFILE", "Overworld palette export currently supports verified US PRG1 ROMs only.")];

        const int paletteBank = 27;
        const int pointerTable = 0x17D2;
        var bankOffset = 16 + (paletteBank * 0x2000);
        if (bankOffset < 0 || bankOffset + pointerTable + 2 > output.Length)
            return [Diagnostics.Error("BUILD_OVERWORLD_PALETTE", "The verified PRG1 overworld palette table is outside the ROM image.")];
        var pointer = output[bankOffset + pointerTable] | (output[bankOffset + pointerTable + 1] << 8);
        var baseOffset = bankOffset + pointer - 0xA000;
        var diagnostics = new List<Diagnostic>();
        foreach (var palette in project.OverworldPalettes.OrderBy(static item => item.Sprites).ThenBy(static item => item.Palette))
        {
            // Tile and map-sprite palette IDs share the same verified 16-byte
            // palette table. The type remains explicit in project data so the UI
            // can show the source role without inventing a second storage area.
            var offset = baseOffset + (palette.Palette * 16);
            if (palette.Palette < 0 || palette.Colors.Count != 16 || offset < bankOffset || offset > output.Length - 16)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_PALETTE", $"Overworld {(palette.Sprites ? "sprite" : "tile")} palette {palette.Palette + 1} is outside the verified PRG1 palette table."));
                continue;
            }
            palette.Colors.ToArray().CopyTo(output, offset);
            diagnostics.Add(Diagnostics.Info("BUILD_OVERWORLD_PALETTE_OK", $"Compiled overworld {(palette.Sprites ? "sprite" : "tile")} palette {palette.Palette + 1}."));
        }
        return diagnostics;
    }

    private static void WriteU16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }
}
