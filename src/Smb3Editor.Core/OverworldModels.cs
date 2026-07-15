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
    IReadOnlyList<OverworldLockBridge> LocksAndBridges,
    IReadOnlyList<OverworldMapSprite> MapSprites = null!)
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

    public OverworldDocument WithMapSprite(OverworldMapSprite item)
    {
        var sprites = MapSprites.ToArray();
        if (item.Index < 0 || item.Index >= sprites.Length) return this;
        sprites[item.Index] = item;
        return this with { MapSprites = sprites };
    }

    public OverworldDocument WithTiles(IReadOnlyList<byte> tiles) =>
        tiles.Count > 0 && tiles.Count % (ScreenWidth * ScreenHeight) == 0
            ? this with { Tiles = tiles.ToArray() }
            : this;
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
    int ReplacementTileOffset,
    int TriggerIndex = -1);

/// <summary>One of the nine vanilla map-object slots for a normal world. Slot 2 is reserved for the airship.</summary>
public sealed record OverworldMapSprite(
    int World,
    int Index,
    int Screen,
    int Column,
    int Row,
    byte Type,
    byte Item,
    int ScreenOffset,
    int XOffset,
    int YOffset,
    int TypeOffset,
    int ItemOffset)
{
    public bool IsAirshipSlot => Index == 1;
    public bool IsEmpty => Type == 0;
}

public static class Smb3OverworldParser
{
    private const int BaseOffset = 0x10;
    private const int WorldMapBase = BaseOffset + 0xE000;
    // Map-object tables and their pointed-to lists live in PRG11's $C000 bank,
    // not in the PRG12 map-layout bank.
    private const int MapObjectBase = BaseOffset + 0xC000;
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
    private const int MapObjectYLists = 0x16020;
    private const int MapObjectScreenLists = MapObjectYLists + (8 * 2);
    private const int MapObjectXLists = MapObjectScreenLists + (8 * 2);
    private const int MapObjectTypeLists = MapObjectXLists + (8 * 2);
    private const int MapObjectItemLists = MapObjectTypeLists + (8 * 2);
    private const int MapSpriteCount = 9;
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
                FortressFxPositions + slot, FortressFxRows + slot, FortressFxReplacementTiles + slot, index));
        }

        var sprites = new List<OverworldMapSprite>(MapSpriteCount);
        if (world < 8)
        {
            if (!TryGetMapSpriteList(bytes, MapObjectYLists, world, out var yList) ||
                !TryGetMapSpriteList(bytes, MapObjectScreenLists, world, out var screenList) ||
                !TryGetMapSpriteList(bytes, MapObjectXLists, world, out var xList) ||
                !TryGetMapSpriteList(bytes, MapObjectTypeLists, world, out var typeList) ||
                !TryGetMapSpriteList(bytes, MapObjectItemLists, world, out var itemList))
            {
                error = "map-sprite table is outside the ROM";
                return false;
            }
            for (var index = 0; index < MapSpriteCount; index++)
            {
                var screen = bytes[screenList + index];
                var column = bytes[xList + index] >> 4;
                var row = bytes[yList + index] >> 4;
                sprites.Add(new OverworldMapSprite(world, index, screen, column, row, bytes[typeList + index], bytes[itemList + index],
                    screenList + index, xList + index, yList + index, typeList + index, itemList + index));
            }
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
            locks,
            sprites);
        return true;
    }

    private static ushort ReadU16(byte[] bytes, int offset) =>
        (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static bool InRange(byte[] bytes, int offset, int length) =>
        offset >= 0 && length >= 0 && offset <= bytes.Length - length;

    private static bool TryGetMapSpriteList(byte[] bytes, int tableOffset, int world, out int listOffset)
    {
        listOffset = 0;
        var pointerOffset = tableOffset + (world * 2);
        if (!InRange(bytes, pointerOffset, 2)) return false;
        listOffset = MapObjectBase + ReadU16(bytes, pointerOffset);
        return InRange(bytes, listOffset, MapSpriteCount);
    }
}

/// <summary>Serializes verified PRG1 overworld data within the original fixed pools.</summary>
public static class Smb3OverworldSerializer
{
    /// <summary>Returns the concrete vanilla constraint violated by a map node, if any.</summary>
    public static string? GetNodeValidationError(OverworldDocument world, OverworldLevelPointer node)
    {
        var positionProblems = new List<string>();
        var destinationProblems = new List<string>();
        if (node.Screen < 0 || node.Screen >= world.ScreenCount)
            positionProblems.Add($"screen {node.Screen + 1} (World {world.World + 1} has screens 1-{world.ScreenCount})");
        if (node.Column is < 0 or >= OverworldDocument.ScreenWidth)
            positionProblems.Add($"column {node.Column + 1} (valid columns are 1-{OverworldDocument.ScreenWidth})");
        var firstRow = OverworldDocument.FirstMapRow;
        var lastRow = firstRow + OverworldDocument.ScreenHeight - 1;
        if (node.Row < firstRow || node.Row > lastRow)
            positionProblems.Add($"row {node.Row + 1} (valid rows are {firstRow + 1}-{lastRow + 1})");
        if (node.ObjectSet is < 0 or > 0x0F)
            destinationProblems.Add($"object set {node.ObjectSet} (valid sets are 0-15)");
        // These are raw overworld-table values, not generic CPU addresses. PRG1
        // legitimately uses values such as $0700 and $0000/$0001 for map entries.
        // Their validity depends on the game's bank-selection path, so rejecting
        // them by a fixed $A000/$C000 range incorrectly rejects an untouched ROM.

        if (positionProblems.Count == 0 && destinationProblems.Count == 0) return null;
        var repairs = new List<string>();
        if (positionProblems.Count > 0)
            repairs.Add($"Map position is outside World {world.World + 1}: {string.Join(", ", positionProblems)}. Drag the red node onto the map.");
        if (destinationProblems.Count > 0)
            repairs.Add($"Destination object set is not usable: {string.Join(", ", destinationProblems)}. Switch to Nodes, click the red node, then choose its destination again.");
        return string.Join(" ", repairs);
    }

    private const int WorldMapBase = 0xE010;
    private const int StructureOffsets = 0x193DA;
    private const int YPositionLists = 0x193EC;
    private const int XPositionLists = 0x193FE;
    private const int EnemyOffsetLists = 0x19410;
    private const int LevelOffsetLists = 0x19422;
    private const int LayoutList = 0x185A8;
    private const int LayoutPoolStart = 0x185BA;
    // PRG12 labels the first byte after World 9's $FF terminator as $B0F3.
    // It is unrelated map code/data and must never be consumed by map layouts.
    private const int LayoutPoolEndExclusive = 0x19103;
    private const int ScreenBytes = OverworldDocument.ScreenWidth * OverworldDocument.ScreenHeight;
    private const int MapObjectYLists = 0x16020;
    private const int MapObjectScreenLists = MapObjectYLists + (8 * 2);
    private const int MapObjectXLists = MapObjectScreenLists + (8 * 2);
    private const int MapObjectTypeLists = MapObjectXLists + (8 * 2);
    private const int MapObjectItemLists = MapObjectTypeLists + (8 * 2);
    private const int MapSpriteCount = 9;

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

        var sourceMaps = parsed.Value!.OrderBy(static map => map.World).ToArray();
        var maps = sourceMaps.ToDictionary(static map => map.World);
        var diagnostics = new List<Diagnostic>();
        if (project.OverworldTiles.GroupBy(static item => item.World).Any(static group => group.Count() != 1))
            return [Diagnostics.Error("BUILD_OVERWORLD_WORLD", "The project contains more than one tile layout for an overworld.")];

        var requested = project.OverworldTiles.ToDictionary(static item => item.World);
        var effective = new IReadOnlyList<byte>[sourceMaps.Length];
        for (var world = 0; world < sourceMaps.Length; world++)
        {
            var map = sourceMaps[world];
            var tiles = requested.TryGetValue(world, out var overrideTiles) ? overrideTiles.Tiles : map.Tiles;
            if (tiles.Count == 0 || tiles.Count % ScreenBytes != 0 || tiles.Count / ScreenBytes > 4)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_SIZE", $"World {world + 1} must contain 1-4 complete 16x9 map screens."));
                continue;
            }
            effective[world] = tiles;
        }
        if (diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error)) return diagnostics;

        var required = effective.Sum(static tiles => tiles.Count + 1);
        var capacity = LayoutPoolEndExclusive - LayoutPoolStart;
        if (required > capacity)
            return [Diagnostics.Error("BUILD_OVERWORLD_CAPACITY", $"Overworld layouts need {required} bytes, but the verified vanilla layout pool holds {capacity}. Remove a map screen from another world before adding one.")];

        // All nine layout streams are rebuilt together inside their verified
        // contiguous PRG12 pool. This permits screen transfer between worlds,
        // but never grows the ROM or overwrites the following $B0F3 table.
        var cursor = LayoutPoolStart;
        for (var world = 0; world < effective.Length; world++)
        {
            var tiles = effective[world];
            if (cursor + tiles.Count + 1 > LayoutPoolEndExclusive)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_CAPACITY", "The rebuilt overworld layouts would exceed their verified stock pool."));
                continue;
            }
            WriteU16(output, LayoutList + (world * 2), (ushort)(cursor - WorldMapBase));
            tiles.ToArray().CopyTo(output, cursor);
            output[cursor + tiles.Count] = 0xFF;
            cursor += tiles.Count + 1;
            if (requested.ContainsKey(world))
                diagnostics.Add(Diagnostics.Info("BUILD_OVERWORLD_OK", $"Compiled World {world + 1}: {tiles.Count / ScreenBytes} vanilla map screen(s)."));
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

        var parsed = Smb3OverworldParser.Parse(RomImage.CreateForTesting(source.SourcePath, output, source.Profile));
        if (!parsed.IsSuccess) return parsed.Diagnostics;
        var maps = parsed.Value!.ToDictionary(static map => map.World);
        var rebuiltWorlds = (project.OverworldNodeSets ?? []).Select(static item => item.World).ToHashSet();
        var diagnostics = new List<Diagnostic>();
        foreach (var change in project.OverworldLevelPointers.OrderBy(static item => item.World).ThenBy(static item => item.Index))
        {
            if (rebuiltWorlds.Contains(change.World)) continue;
            if (!maps.TryGetValue(change.World, out var world) || change.Index < 0 || change.Index >= world.LevelPointers.Count)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_NODE", $"World {change.World + 1} map node {change.Index + 1} is not present in the verified PRG1 map data."));
                continue;
            }

            var candidate = new OverworldLevelPointer(change.Index, change.Screen, change.Column, change.Row,
                change.ObjectSet, change.LevelOffset, change.EnemyOffset, 0, 0, 0, 0);
            if (GetNodeValidationError(world, candidate) is { } problem)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_NODE_RANGE", $"World {world.World + 1} map node {change.Index + 1} is invalid: {problem}."));
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

    /// <summary>
    /// Rebuilds the stock map-entry structure blocks for Worlds 1–8. Their
    /// original blocks form one verified contiguous pool; no ROM space grows
    /// and World 9's special structure remains untouched.
    /// </summary>
    public static IReadOnlyList<Diagnostic> ApplyNodeSetOverrides(
        ProjectDocumentV2 project,
        RomImage source,
        byte[] output)
    {
        if (project.OverworldNodeSets is not { Count: > 0 }) return [];
        if (!string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal))
            return [Diagnostics.Error("BUILD_OVERWORLD_PROFILE", "Overworld node editing currently supports verified US PRG1 ROMs only.")];

        var parsed = Smb3OverworldParser.Parse(RomImage.CreateForTesting(source.SourcePath, output, source.Profile));
        if (!parsed.IsSuccess) return parsed.Diagnostics;
        var maps = parsed.Value!.OrderBy(static map => map.World).ToArray();
        var requested = project.OverworldNodeSets.GroupBy(static item => item.World).ToArray();
        if (requested.Any(static group => group.Count() != 1))
            return [Diagnostics.Error("BUILD_OVERWORLD_NODE_SET", "The project contains more than one node set for an overworld.")];
        if (requested.Any(static group => group.Key is < 0 or > 7))
            return [Diagnostics.Error("BUILD_OVERWORLD_NODE_SET", "Vanilla node add/remove is currently verified for Worlds 1–8 only; Warp World remains read-only.")];

        var sets = requested.ToDictionary(static group => group.Key, static group => group.Single().Nodes);
        var effective = new List<OverworldNodeOverride>[8];
        for (var world = 0; world < 8; world++)
        {
            var map = maps[world];
            effective[world] = (sets.TryGetValue(world, out var replacement) ? replacement : map.LevelPointers
                    .Select(static node => new OverworldNodeOverride(node.Screen, node.Column, node.Row, node.ObjectSet, node.LevelOffset, node.EnemyOffset)))
                .OrderBy(static node => node.Screen).ThenBy(static node => node.Row).ThenBy(static node => node.Column).ToList();
            var seen = new HashSet<(int Screen, int Column, int Row)>();
            foreach (var node in effective[world])
            {
                if (node.Screen < 0 || node.Screen >= map.ScreenCount || node.Column is < 0 or >= OverworldDocument.ScreenWidth ||
                    node.Row is < OverworldDocument.FirstMapRow or >= OverworldDocument.FirstMapRow + OverworldDocument.ScreenHeight ||
                    node.ObjectSet is < 0 or > 0x0F ||
                    !seen.Add((node.Screen, node.Column, node.Row)))
                {
                    return [Diagnostics.Error("BUILD_OVERWORLD_NODE_RANGE", $"World {world + 1} has an invalid or duplicate vanilla map node.")];
                }
            }
        }

        var poolStart = maps[0].LevelPointers[0].PositionYOffset - 4;
        var poolEnd = maps[8].LevelPointers[0].PositionYOffset - 4;
        if (poolStart < 0 || poolEnd <= poolStart || poolEnd > output.Length)
            return [Diagnostics.Error("BUILD_OVERWORLD_NODE_POOL", "The verified vanilla node-table pool is unavailable.")];
        var required = effective.Sum(static nodes => 4 + (nodes.Count * 6));
        if (required > poolEnd - poolStart)
            return [Diagnostics.Error("BUILD_OVERWORLD_NODE_CAPACITY", $"Vanilla map nodes need {required} bytes, but the verified stock pool holds {poolEnd - poolStart}. Remove a node from another overworld or use Enhanced mode when available.")];

        var cursor = poolStart;
        for (var world = 0; world < 8; world++)
        {
            var nodes = effective[world];
            var starts = new byte[4];
            var index = 0;
            for (var screen = 0; screen < starts.Length; screen++)
            {
                starts[screen] = (byte)index;
                while (index < nodes.Count && nodes[index].Screen == screen) index++;
            }
            starts.CopyTo(output.AsSpan(cursor, 4));
            var yOffset = cursor + 4;
            var xOffset = yOffset + nodes.Count;
            var enemyOffset = xOffset + nodes.Count;
            var layoutOffset = enemyOffset + (nodes.Count * 2);
            for (var node = 0; node < nodes.Count; node++)
            {
                output[yOffset + node] = (byte)((nodes[node].Row << 4) | nodes[node].ObjectSet);
                output[xOffset + node] = (byte)((nodes[node].Screen << 4) | nodes[node].Column);
                WriteU16(output, enemyOffset + (node * 2), nodes[node].EnemyOffset);
                WriteU16(output, layoutOffset + (node * 2), nodes[node].LevelOffset);
            }
            WriteU16(output, StructureOffsets + (world * 2), (ushort)(cursor - WorldMapBase));
            WriteU16(output, YPositionLists + (world * 2), (ushort)(yOffset - WorldMapBase));
            WriteU16(output, XPositionLists + (world * 2), (ushort)(xOffset - WorldMapBase));
            WriteU16(output, EnemyOffsetLists + (world * 2), (ushort)(enemyOffset - WorldMapBase));
            WriteU16(output, LevelOffsetLists + (world * 2), (ushort)(layoutOffset - WorldMapBase));
            cursor = layoutOffset + (nodes.Count * 2);
        }

        var verified = Smb3OverworldParser.Parse(RomImage.CreateForTesting(source.SourcePath, output, source.Profile));
        if (!verified.IsSuccess)
            return verified.Diagnostics.Select(static item => Diagnostics.Error("BUILD_OVERWORLD_NODE_VERIFY", $"Compiled node verification failed: {item.Message}")).ToArray();
        return [Diagnostics.Info("BUILD_OVERWORLD_NODE_OK", $"Compiled {effective.Sum(static nodes => nodes.Count)} vanilla map nodes in the stock {poolEnd - poolStart}-byte pool.")];
    }

    public static IReadOnlyList<Diagnostic> ApplyLockBridgeOverrides(
        ProjectDocumentV2 project,
        RomImage source,
        byte[] output)
    {
        if (project.OverworldLocksAndBridges is not { Count: > 0 }) return [];
        if (!string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal))
            return [Diagnostics.Error("BUILD_OVERWORLD_PROFILE", "Overworld lock and bridge export currently supports verified US PRG1 ROMs only.")];

        var parsed = Smb3OverworldParser.Parse(RomImage.CreateForTesting(source.SourcePath, output, source.Profile));
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

    public static IReadOnlyList<Diagnostic> ApplyMapSpriteOverrides(ProjectDocumentV2 project, RomImage source, byte[] output)
    {
        if (project.OverworldMapSprites is not { Count: > 0 }) return [];
        if (!string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal))
            return [Diagnostics.Error("BUILD_OVERWORLD_PROFILE", "Overworld map sprites currently support verified US PRG1 ROMs only.")];

        var parsed = Smb3OverworldParser.Parse(RomImage.CreateForTesting(source.SourcePath, output, source.Profile));
        if (!parsed.IsSuccess) return parsed.Diagnostics;
        var worlds = parsed.Value!.ToDictionary(static world => world.World);
        var diagnostics = new List<Diagnostic>();
        foreach (var change in project.OverworldMapSprites.OrderBy(static item => item.World).ThenBy(static item => item.Index))
        {
            if (!worlds.TryGetValue(change.World, out var world) || change.World is < 0 or > 7 || change.Index is < 0 or >= MapSpriteCount)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_SPRITE", "The requested map sprite slot is not present in the verified PRG1 data."));
                continue;
            }
            if (change.Index == 1)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_SPRITE_AIRSHIP", "Map sprite slot 2 is reserved for the stock airship and cannot be edited."));
                continue;
            }
            if (change.Screen < 0 || change.Screen >= world.ScreenCount || change.Column is < 0 or >= OverworldDocument.ScreenWidth ||
                change.Row is < OverworldDocument.FirstMapRow or >= OverworldDocument.FirstMapRow + OverworldDocument.ScreenHeight)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_SPRITE_RANGE", $"World {change.World + 1} map sprite {change.Index + 1} has an invalid map position."));
                continue;
            }
            var sprites = world.MapSprites;
            if (sprites.Count != MapSpriteCount)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_OVERWORLD_SPRITE", "The verified map-sprite table is unavailable."));
                continue;
            }
            var original = sprites[change.Index];
            output[original.ScreenOffset] = (byte)change.Screen;
            output[original.XOffset] = (byte)((change.Column << 4) | (output[original.XOffset] & 0x0F));
            output[original.YOffset] = (byte)((change.Row << 4) | (output[original.YOffset] & 0x0F));
            output[original.TypeOffset] = change.Type;
            output[original.ItemOffset] = change.Item;
            diagnostics.Add(Diagnostics.Info("BUILD_OVERWORLD_SPRITE_OK", $"Compiled World {change.World + 1} map sprite {change.Index + 1}."));
        }
        if (diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error)) return diagnostics;
        var verified = Smb3OverworldParser.Parse(RomImage.CreateForTesting(source.SourcePath, output, source.Profile));
        if (!verified.IsSuccess)
            diagnostics.AddRange(verified.Diagnostics.Select(static item => Diagnostics.Error("BUILD_OVERWORLD_SPRITE_VERIFY", $"Compiled map-sprite verification failed: {item.Message}")));
        return diagnostics;
    }

    private static void WriteU16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

}
