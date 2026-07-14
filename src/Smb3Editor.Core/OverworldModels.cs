namespace Smb3Editor.Core;

/// <summary>Vanilla SMB3 overworld data. This model is read-only until its round-trip rules are verified.</summary>
public sealed record OverworldDocument(
    int World,
    int TilePalette,
    int SpritePalette,
    int StartRow,
    bool ScrollEnabled,
    IReadOnlyList<byte> Tiles,
    IReadOnlyList<OverworldLevelPointer> LevelPointers)
{
    public const int ScreenWidth = 16;
    public const int ScreenHeight = 9;
    public int ScreenCount => Tiles.Count / (ScreenWidth * ScreenHeight);
    public int Width => ScreenCount * ScreenWidth;
}

public sealed record OverworldLevelPointer(
    int Index,
    int Screen,
    int Column,
    int Row,
    int ObjectSet,
    ushort LevelOffset,
    ushort EnemyOffset);

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
                ReadU16(bytes, enemyList + (index * 2))));
        }

        map = new OverworldDocument(
            world,
            bytes[TilePalettes + world],
            bytes[SpritePalettes + world],
            bytes[MapYStarts + world],
            (bytes[MapScroll + world] & 0x10) == 0,
            tiles,
            pointers);
        return true;
    }

    private static ushort ReadU16(byte[] bytes, int offset) =>
        (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static bool InRange(byte[] bytes, int offset, int length) =>
        offset >= 0 && length >= 0 && offset <= bytes.Length - length;
}
