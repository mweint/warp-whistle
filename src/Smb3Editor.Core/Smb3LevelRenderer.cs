using System.Text.Json;

namespace Smb3Editor.Core;

public sealed record LevelRenderSnapshot(
    int WidthInTiles,
    int HeightInTiles,
    IReadOnlyList<byte> Metatiles,
    int PixelWidth,
    int PixelHeight,
    IReadOnlyList<uint> ArgbPixels,
    IReadOnlyDictionary<byte, EnemySpritePreview> EnemySprites,
    IReadOnlyDictionary<int, LevelElementRenderBounds> ElementBounds,
    IReadOnlyDictionary<int, LevelElementRenderAnchor> ElementAnchors);

public sealed record LevelElementRenderBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public sealed record LevelElementRenderAnchor(int X, int Y);

public sealed record EnemySpritePreview(
    int OffsetX,
    int OffsetY,
    int PixelWidth,
    int PixelHeight,
    IReadOnlyList<uint> ArgbPixels);

public interface ISmb3LevelRenderer
{
    OperationResult<LevelRenderSnapshot> Render(RomImage rom, LevelDocument document, int? excludedElementIndex = null);
}

/// <summary>
/// Produces the level exactly as SMB3 does: the verified ROM's bounded 6502
/// level loader creates the metatile map, then ROM-resident metatile, CHR and
/// palette tables are composed into a display bitmap.
/// </summary>
public sealed class Smb3LevelRenderer : ISmb3LevelRenderer
{
    private const int PrgBankSize = 0x2000;
    private const int TileMemoryAddress = 0x6000;
    private const int HorizontalScreenBytes = 0x1B0;
    private const int VerticalScreenBytes = 0x00F0;
    private const ushort LoaderEntryPoint = 0x9A1D;
    private const ushort HostReturnAddress = 0x0001;
    private const int InstructionLimit = 5_000_000;

    private static readonly int[] LayoutBankByTileset =
    [
        11, 15, 21, 16, 17, 19, 18, 18, 18, 20, 23, 19, 17, 19, 13, 26, 26, 26, 14
    ];

    private static readonly IReadOnlyList<DocumentedEnemy> DocumentedEnemies = LoadDocumentedEnemies();

    private static readonly IReadOnlyDictionary<byte, SpritePart[]> BuiltInEnemyPreviewParts =
        new Dictionary<byte, SpritePart[]>
        {
            [0x41] = [P(0, 0, 4, 0x10, 1), P(8, 0, 4, 0x10, 1, hFlip: true)],
            [0x42] = [P(0, 0, 79, 0x26, 1), P(8, 0, 79, 0x28, 1)],
            [0x43] = [P(0, 0, 79, 0x26, 1), P(8, 0, 79, 0x28, 1)],
            [0x3F] =
            [
                P(0, -16, 19, 0x00, 2), P(8, -16, 19, 0x02, 2),
                P(0, 0, 19, 0x04, 2), P(8, 0, 19, 0x06, 2)
            ],
            [0x2F] = [P(0, 0, 18, 0x14, 1), P(8, 0, 18, 0x16, 1)],
            [0x2E] =
            [
                P(0, 16, 18, 0x10, 2), P(8, 16, 18, 0x12, 2),
                P(16, 16, 18, 0x10, 2), P(24, 16, 18, 0x12, 2)
            ],
            [0x30] = [P(0, 0, 18, 0x00, 1)], [0x45] = [P(0, 0, 18, 0x00, 1)],
            [0x31] = [P(0, 4, 18, 0x28, 1), P(8, 4, 18, 0x2A, 1)],
            [0x32] = [P(0, -4, 18, 0x28, 1, vFlip: true), P(8, -4, 18, 0x2A, 1, vFlip: true)],
            [0x4B] = BoomBoomParts(),
            [0x4C] = BoomBoomParts(),
            [0x51] = RotoDiscParts(), [0x5A] = RotoDiscParts(), [0x5B] = RotoDiscParts(),
            [0x5E] = RotoDiscParts(), [0x5F] = RotoDiscParts(), [0x60] = RotoDiscParts(),
            [0x6C] = KoopaParts(2),
            [0x6D] = KoopaParts(1),
            [0x6E] = [.. KoopaParts(2), P(8, -10, 79, 0x0C, 1)],
            [0x6F] = [.. KoopaParts(1), P(8, -10, 79, 0x0C, 1)],
            [0x70] = [P(0, 0, 11, 0x10, 3), P(8, 0, 11, 0x12, 3)],
            [0x71] = [P(0, 0, 11, 0x04, 1), P(8, 0, 11, 0x06, 1)],
            [0x72] = [P(0, 0, 79, 0x18, 3), P(8, 0, 79, 0x1A, 3, hFlip: true)],
            [0x73] =
            [
                P(0, 0, 79, 0x18, 1), P(8, 0, 79, 0x1A, 1, hFlip: true),
                P(-2, -10, 79, 0x0C, 1, hFlip: true), P(10, -10, 79, 0x0C, 1)
            ],
            [0x76] = [P(0, 0, 79, 0x26, 1), P(8, 0, 79, 0x28, 1)],
            [0x77] = [P(0, 0, 79, 0x26, 2), P(8, 0, 79, 0x28, 2)],
            [0x78] = [P(0, 0, 79, 0x1C, 3), P(8, 0, 79, 0x1C, 3)],
            [0x79] = [P(0, 0, 79, 0x1C, 3), P(8, 0, 79, 0x1C, 3)],
            [0x8A] = ThwompParts(), [0x8B] = ThwompParts(), [0x8C] = ThwompParts(),
            [0x8D] = ThwompParts(), [0x8E] = ThwompParts(), [0x8F] = ThwompParts(),
            [0x9E] = [P(0, 0, 18, 0x0C, 1), P(8, 0, 18, 0x0C, 1, hFlip: true)],
            [0x9F] = [P(0, 0, 14, 0x30, 1), P(8, 0, 14, 0x32, 1)],
            [0xA0] = PiranhaParts(0x20, 2, -24),
            [0xA1] = PiranhaParts(0x20, 2, 24, flipped: true),
            [0xA2] = PiranhaParts(0x20, 1, -32),
            [0xA3] = PiranhaParts(0x20, 1, 32, flipped: true),
            [0xA4] = PiranhaParts(0x30, 2, -24),
            [0xA5] = PiranhaParts(0x30, 2, 24, flipped: true),
            [0xA6] = PiranhaParts(0x30, 1, -32),
            [0xA7] = PiranhaParts(0x30, 1, 32, flipped: true)
        };

    private static readonly IReadOnlyDictionary<byte, SpritePart[]> EnemyPreviewParts = BuildEnemyPreviewParts();
    public static IReadOnlyDictionary<byte, string> EnemyCatalog { get; } = DocumentedEnemies
        .Where(static item => item.Id is >= 0 and <= byte.MaxValue)
        .GroupBy(static item => (byte)item.Id)
        .ToDictionary(static group => group.Key, static group => group.Last().Name);

    public static bool HasEnemyPreview(byte id) => EnemyPreviewParts.ContainsKey(id);

    public static string? GetEnemyName(byte id) => EnemyCatalog.GetValueOrDefault(id);

    public static string? GetEnemyDescription(byte id) => DocumentedEnemies
        .LastOrDefault(item => item.Id == id)?.Description;

    public static OperationResult<IReadOnlyList<uint>> ReadPalettePreview(
        RomImage rom, LevelDocument document, bool objects, int selection)
    {
        var header = document.Header.WithEditableSettings(
            document.Header.ScreenCount,
            objects ? document.Header.BackgroundPalette : Math.Clamp(selection, 0, 7),
            objects ? Math.Clamp(selection, 0, 3) : document.Header.ObjectPalette,
            document.Header.Music,
            document.Header.TimeSetting);
        var candidate = document with { Header = header };
        var bytes = objects
            ? ReadObjectPalette(rom, candidate)
            : ReadBackgroundPalette(rom, candidate).Value;
        if (bytes is null || bytes.Length < 16)
        {
            return OperationResult<IReadOnlyList<uint>>.Failure(
                Diagnostics.Error("PALETTE_PREVIEW", "The selected ROM palette could not be previewed."));
        }
        return OperationResult<IReadOnlyList<uint>>.Success(
            bytes.Take(16).Select(value => NesPalette.Argb[value & 0x3F]).ToArray());
    }

    public OperationResult<LevelRenderSnapshot> Render(RomImage rom, LevelDocument document, int? excludedElementIndex = null)
    {
        try
        {
            if (!string.Equals(rom.Profile.Id, "us-prg1", StringComparison.Ordinal))
            {
                return OperationResult<LevelRenderSnapshot>.Failure(
                    Diagnostics.Error("RENDER_PROFILE", "Faithful graphics rendering is currently enabled for the verified US PRG1 revision."));
            }

            if (document.Tileset <= 0 || document.Tileset >= LayoutBankByTileset.Length)
            {
                return OperationResult<LevelRenderSnapshot>.Failure(
                    Diagnostics.Error("RENDER_TILESET", $"Tileset {document.Tileset} is not a supported gameplay tileset."));
            }

            var encoded = Smb3LevelCodec.EncodeLayout(document, excludedElementIndex);
            if (!encoded.IsSuccess)
            {
                return OperationResult<LevelRenderSnapshot>.Failure(encoded.Diagnostics.ToArray());
            }

            var cpu = new Cpu6502Sandbox { StackPointer = 0xFB };
            LoadPrgBank(cpu, rom, 30, 0x8000);
            LoadPrgBank(cpu, rom, LayoutBankByTileset[document.Tileset], 0xA000);
            LoadPrgBank(cpu, rom, 14, 0xC000);
            LoadPrgBank(cpu, rom, 31, 0xE000);
            cpu.Load(0x4000, encoded.Value!);

            cpu.Memory[0x0061] = 0x00; // Level_LayPtr_AddrL
            cpu.Memory[0x0062] = 0x40; // Level_LayPtr_AddrH
            cpu.Memory[0x03DE] = 0x00; // Level_JctCtl
            cpu.Memory[0x03DF] = 0x00; // Level_JctFlag
            cpu.Memory[0x070A] = (byte)document.Tileset;
            cpu.Memory[0x01FC] = 0x00; // Synthetic caller return address ($0000 + 1)
            cpu.Memory[0x01FD] = 0x00;

            var elementByLayoutPointer = BuildElementPointerMap(document, excludedElementIndex);
            var writesByElement = new Dictionary<int, HashSet<ushort>>();
            int? activeElementIndex = null;
            int? unsafeElementIndex = null;
            ushort? unsafeTileWriteAddress = null;
            cpu.MemoryWriteObserver = (address, _) =>
            {
                var layoutPointer = cpu.Memory[0x0061] | (cpu.Memory[0x0062] << 8);
                if (elementByLayoutPointer.TryGetValue(layoutPointer, out var mappedElementIndex))
                {
                    activeElementIndex = mappedElementIndex;
                }
                if (address >= TileMemoryAddress + 6480 && activeElementIndex is int activeIndex &&
                    document.Tileset == 1 &&
                    document.Elements.FirstOrDefault(item => item.Index == activeIndex) is
                        { Kind: LevelElementKind.VariableGenerator, GeneratorId: >= 0 and <= 3 })
                {
                    unsafeTileWriteAddress ??= address;
                    unsafeElementIndex ??= activeIndex;
                }
                if (address is < TileMemoryAddress or >= TileMemoryAddress + 6480 || activeElementIndex is not int elementIndex)
                {
                    return;
                }
                if (!writesByElement.TryGetValue(elementIndex, out var addresses))
                {
                    addresses = [];
                    writesByElement[elementIndex] = addresses;
                }

                addresses.Add(address);
            };

            var run = cpu.Run(LoaderEntryPoint, InstructionLimit, HostReturnAddress);
            cpu.MemoryWriteObserver = null;
            if (unsafeTileWriteAddress is not null || !run.Halted || run.Diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error))
            {
                if ((unsafeElementIndex ?? activeElementIndex) is int elementIndex &&
                    document.Elements.FirstOrDefault(item => item.Index == elementIndex) is { } activeElement)
                {
                    var editable = document.Elements.Where(item => item.Kind != LevelElementKind.Junction).ToArray();
                    var layer = Array.FindIndex(editable, item => item.Index == elementIndex) + 1;
                    return OperationResult<LevelRenderSnapshot>.Failure(
                        Diagnostics.Error(
                            $"GENERATOR_UNSAFE:ELEMENT:{elementIndex}",
                        $"{GeneratorDefinition.For(document, activeElement).Name} on layer {layer}/{editable.Length} did not complete inside the bounded ROM generator. Its current position, size, or ordering leaves SMB3 searching or writing outside the safe level-generation path{(unsafeTileWriteAddress is ushort address ? $" at ${address:X4}" : string.Empty)}. Reposition, resize, or reorder this object until the level renders again."));
                }
                return OperationResult<LevelRenderSnapshot>.Failure(run.Diagnostics.ToArray());
            }

            var width = document.Header.IsVertical ? 16 : document.Header.ScreenCount * 16;
            var height = document.Header.IsVertical ? document.Header.ScreenCount * 15 : 27;
            var metatiles = ReadMetatiles(cpu.Memory, document.Header.IsVertical, width, height);
            var pixels = ComposePixels(rom, document, metatiles, width, height);
            if (!pixels.IsSuccess)
            {
                return OperationResult<LevelRenderSnapshot>.Failure(pixels.Diagnostics.ToArray());
            }

            var enemySprites = ComposeEnemySprites(rom, document);
            var elementBounds = BuildElementBounds(document, writesByElement, width, height);
            var elementAnchors = BuildElementAnchors(document, writesByElement, width, height);
            return OperationResult<LevelRenderSnapshot>.Success(
                new LevelRenderSnapshot(
                    width,
                    height,
                    metatiles,
                    width * 16,
                    height * 16,
                    pixels.Value!,
                    enemySprites,
                    elementBounds,
                    elementAnchors),
                [Diagnostics.Info("RENDER_OK", $"Rendered {width:N0} × {height:N0} metatiles using the ROM's level generator in {run.Instructions:N0} instructions.")]);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return OperationResult<LevelRenderSnapshot>.Failure(
                Diagnostics.Error("RENDER_FAILED", $"The ROM-derived level renderer stopped safely: {ex.Message}"));
        }
    }

    private static void LoadPrgBank(Cpu6502Sandbox cpu, RomImage rom, int bank, ushort address)
    {
        var offset = bank * PrgBankSize;
        if (bank < 0 || offset > rom.Prg.Length - PrgBankSize)
        {
            throw new ArgumentOutOfRangeException(nameof(bank));
        }

        cpu.Load(address, rom.Prg.Slice(offset, PrgBankSize));
    }

    private static byte[] ReadMetatiles(Span<byte> memory, bool vertical, int width, int height)
    {
        var output = new byte[checked(width * height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var source = vertical
                    ? TileMemoryAddress + ((y / 15) * VerticalScreenBytes) + ((y % 15) * 16) + x
                    : TileMemoryAddress + ((x / 16) * HorizontalScreenBytes) + (y * 16) + (x % 16);
                output[(y * width) + x] = memory[source];
            }
        }

        return output;
    }

    private static OperationResult<uint[]> ComposePixels(
        RomImage rom,
        LevelDocument document,
        IReadOnlyList<byte> metatiles,
        int width,
        int height)
    {
        var bank = LayoutBankByTileset[document.Tileset];
        var metatileLayouts = rom.Prg.Slice(bank * PrgBankSize, 0x400);
        var palette = ReadBackgroundPalette(rom, document);
        if (!palette.IsSuccess)
        {
            return OperationResult<uint[]>.Failure(palette.Diagnostics.ToArray());
        }

        var backgroundPage = document.Header.BackgroundAndAction & 0x1F;
        if (backgroundPage > 22)
        {
            return OperationResult<uint[]>.Failure(
                Diagnostics.Error("RENDER_CHR_PAGE", $"Background CHR page {backgroundPage} is outside SMB3's table."));
        }

        var bank30 = rom.Prg.Slice(30 * PrgBankSize, PrgBankSize);
        var page1 = bank30[0x1762 + backgroundPage];
        var page2 = bank30[0x1779 + backgroundPage];
        var pixelWidth = width * 16;
        var pixelHeight = height * 16;
        var output = new uint[checked(pixelWidth * pixelHeight)];
        var decodedPatterns = new Dictionary<(byte Page, byte Pattern), byte[]>();

        for (var tileY = 0; tileY < height; tileY++)
        {
            for (var tileX = 0; tileX < width; tileX++)
            {
                var tile = metatiles[(tileY * width) + tileX];
                var paletteIndex = tile >> 6;
                DrawQuadrant(0, 0, metatileLayouts[tile]);
                DrawQuadrant(0, 8, metatileLayouts[0x100 + tile]);
                DrawQuadrant(8, 0, metatileLayouts[0x200 + tile]);
                DrawQuadrant(8, 8, metatileLayouts[0x300 + tile]);

                void DrawQuadrant(int localX, int localY, byte pattern)
                {
                    var page = pattern < 0x80 ? page1 : page2;
                    var key = (page, pattern);
                    if (!decodedPatterns.TryGetValue(key, out var patternPixels))
                    {
                        var patternWithinPage = pattern & 0x7F;
                        var chrOffset = (page * 0x400) + (patternWithinPage * ChrTileDecoder.BytesPerTile);
                        if (chrOffset > rom.Chr.Length - ChrTileDecoder.BytesPerTile)
                        {
                            throw new ArgumentOutOfRangeException(nameof(pattern), "A metatile pattern points outside CHR ROM.");
                        }

                        var decoded = ChrTileDecoder.DecodeTile(rom.Chr.Slice(chrOffset, ChrTileDecoder.BytesPerTile), 0);
                        if (!decoded.IsSuccess)
                        {
                            throw new InvalidOperationException(decoded.Diagnostics[0].Message);
                        }

                        patternPixels = decoded.Value!;
                        decodedPatterns[key] = patternPixels;
                    }

                    for (var py = 0; py < 8; py++)
                    {
                        var destinationY = (tileY * 16) + localY + py;
                        for (var px = 0; px < 8; px++)
                        {
                            var colorWithinPalette = patternPixels[(py * 8) + px];
                            var colorOffset = colorWithinPalette == 0 ? 0 : (paletteIndex * 4) + colorWithinPalette;
                            var nesColor = palette.Value![colorOffset] & 0x3F;
                            output[(destinationY * pixelWidth) + (tileX * 16) + localX + px] = NesPalette.Argb[nesColor];
                        }
                    }
                }
            }
        }

        return OperationResult<uint[]>.Success(output);
    }

    private static OperationResult<byte[]> ReadBackgroundPalette(RomImage rom, LevelDocument document)
    {
        const int paletteBank = 27;
        const int palettePointerTableOffset = 0x17D2; // Runtime $B7D2 in PRG bank 27.
        var bank = rom.Prg.Slice(paletteBank * PrgBankSize, PrgBankSize);
        var pointerOffset = palettePointerTableOffset + (document.Tileset * 2);
        var pointer = bank[pointerOffset] | (bank[pointerOffset + 1] << 8);
        var paletteOffset = pointer - 0xA000 + (document.Header.BackgroundPalette * 16);
        if (paletteOffset < 0 || paletteOffset > bank.Length - 16)
        {
            return OperationResult<byte[]>.Failure(
                Diagnostics.Error("RENDER_PALETTE", "The selected background palette points outside the verified palette bank."));
        }

        return OperationResult<byte[]>.Success(bank.Slice(paletteOffset, 16).ToArray());
    }

    private static IReadOnlyDictionary<int, int> BuildElementPointerMap(LevelDocument document, int? excludedElementIndex)
    {
        var result = new Dictionary<int, int>();
        var pointer = 0x4000 + Smb3LevelCodec.HeaderLength;
        foreach (var element in document.Elements)
        {
            if (excludedElementIndex == element.Index) continue;
            result[pointer + 3] = element.Index;
            pointer += element.ExtraParameter is null ? 3 : 4;
            result[pointer] = element.Index;
        }

        return result;
    }

    private static IReadOnlyDictionary<int, LevelElementRenderBounds> BuildElementBounds(
        LevelDocument document,
        IReadOnlyDictionary<int, HashSet<ushort>> writesByElement,
        int width,
        int height)
    {
        var result = new Dictionary<int, LevelElementRenderBounds>();
        foreach (var element in document.Elements)
        {
            var points = writesByElement.TryGetValue(element.Index, out var addresses)
                ? addresses.Select(address => TileMemoryToPoint(address, document.Header.IsVertical))
                    .Where(point => point.X >= 0 && point.X < width && point.Y >= 0 && point.Y < height)
                    .ToArray()
                : [];
            result[element.Index] = points.Length == 0
                ? new LevelElementRenderBounds(element.X, element.Y, element.X + 1, element.Y + 1)
                : new LevelElementRenderBounds(
                    points.Min(static point => point.X),
                    points.Min(static point => point.Y),
                    points.Max(static point => point.X) + 1,
                    points.Max(static point => point.Y) + 1);
        }

        return result;
    }

    private static IReadOnlyDictionary<int, LevelElementRenderAnchor> BuildElementAnchors(
        LevelDocument document,
        IReadOnlyDictionary<int, HashSet<ushort>> writesByElement,
        int width,
        int height)
    {
        var result = new Dictionary<int, LevelElementRenderAnchor>();
        foreach (var element in document.Elements)
        {
            var points = writesByElement.TryGetValue(element.Index, out var addresses)
                ? addresses.Select(address => TileMemoryToPoint(address, document.Header.IsVertical))
                    .Where(point => point.X >= 0 && point.X < width && point.Y >= 0 && point.Y < height)
                    .ToArray()
                : [];
            var anchor = points
                .OrderBy(point => Math.Abs(point.X - element.X) + Math.Abs(point.Y - element.Y))
                .ThenBy(static point => point.Y)
                .ThenBy(static point => point.X)
                .FirstOrDefault((element.X, element.Y));
            result[element.Index] = new LevelElementRenderAnchor(anchor.X, anchor.Y);
        }

        return result;
    }

    private static (int X, int Y) TileMemoryToPoint(ushort address, bool vertical)
    {
        var offset = address - TileMemoryAddress;
        if (vertical)
        {
            var screen = offset / VerticalScreenBytes;
            var withinScreen = offset % VerticalScreenBytes;
            return (withinScreen % 16, (screen * 15) + (withinScreen / 16));
        }

        var horizontalScreen = offset / HorizontalScreenBytes;
        var horizontalWithinScreen = offset % HorizontalScreenBytes;
        return ((horizontalScreen * 16) + (horizontalWithinScreen % 16), horizontalWithinScreen / 16);
    }

    private static IReadOnlyDictionary<byte, EnemySpritePreview> ComposeEnemySprites(RomImage rom, LevelDocument document)
    {
        var palette = ReadObjectPalette(rom, document);
        var previews = new Dictionary<byte, EnemySpritePreview>();
        foreach (var id in document.Enemies.Select(static enemy => enemy.Id).Distinct())
        {
            if (!EnemyPreviewParts.TryGetValue(id, out var parts))
            {
                continue;
            }

            var minX = parts.Min(static part => part.X);
            var minY = parts.Min(static part => part.Y);
            var maxX = parts.Max(static part => part.X + 8);
            var maxY = parts.Max(static part => part.Y + 16);
            var width = maxX - minX;
            var height = maxY - minY;
            var pixels = new uint[width * height];
            var patterns = new Dictionary<(int Bank, int Pattern), byte[]>();
            foreach (var part in parts)
            {
                for (var destinationY = 0; destinationY < 16; destinationY++)
                {
                    var sourceY = part.VFlip ? 15 - destinationY : destinationY;
                    var pattern = part.Pattern + (sourceY / 8);
                    if (!patterns.TryGetValue((part.Bank, pattern), out var patternPixels))
                    {
                        var chrOffset = (part.Bank * 0x400) + (pattern * ChrTileDecoder.BytesPerTile);
                        if (chrOffset > rom.Chr.Length - ChrTileDecoder.BytesPerTile)
                        {
                            continue;
                        }

                        var decoded = ChrTileDecoder.DecodeTile(rom.Chr.Slice(chrOffset, ChrTileDecoder.BytesPerTile), 0);
                        if (!decoded.IsSuccess)
                        {
                            continue;
                        }

                        patternPixels = decoded.Value!;
                        patterns[(part.Bank, pattern)] = patternPixels;
                    }

                    var patternY = sourceY & 7;
                    for (var destinationX = 0; destinationX < 8; destinationX++)
                    {
                        var sourceX = part.HFlip ? 7 - destinationX : destinationX;
                        var color = patternPixels[(patternY * 8) + sourceX];
                        if (color == 0)
                        {
                            continue;
                        }

                        var paletteColor = palette[(part.Palette * 4) + color] & 0x3F;
                        var x = part.X - minX + destinationX;
                        var y = part.Y - minY + destinationY;
                        pixels[(y * width) + x] = NesPalette.Argb[paletteColor];
                    }
                }
            }

            previews[id] = new EnemySpritePreview(minX, minY, width, height, pixels);
        }

        return previews;
    }

    private static byte[] ReadObjectPalette(RomImage rom, LevelDocument document)
    {
        const int paletteBank = 27;
        const int palettePointerTableOffset = 0x17D2;
        var bank = rom.Prg.Slice(paletteBank * PrgBankSize, PrgBankSize);
        var pointerOffset = palettePointerTableOffset + (document.Tileset * 2);
        var pointer = bank[pointerOffset] | (bank[pointerOffset + 1] << 8);
        var paletteOffset = pointer - 0xA000 + ((8 + document.Header.ObjectPalette) * 16);
        if (paletteOffset < 0 || paletteOffset > bank.Length - 16)
        {
            return new byte[16];
        }

        return bank.Slice(paletteOffset, 16).ToArray();
    }

    private static SpritePart[] KoopaParts(int shellPalette) =>
    [
        P(0, -20, 79, 0x00, 3), P(0, -4, 79, 0x02, 3), P(8, -4, 79, 0x04, shellPalette),
        P(0, 12, 79, 0x38, 3), P(8, 12, 79, 0x3A, 3)
    ];

    private static SpritePart[] BoomBoomParts() =>
    [
        P(8, 0, 51, 0x18, 3), P(0, 16, 51, 0x1A, 3), P(8, 16, 51, 0x1C, 3),
        P(16, 0, 51, 0x18, 3, hFlip: true), P(24, 16, 51, 0x1A, 3, hFlip: true),
        P(16, 16, 51, 0x1C, 3, hFlip: true)
    ];

    private static SpritePart[] ThwompParts() =>
    [
        P(4, 0, 18, 0x30, 2), P(12, 0, 18, 0x32, 2), P(20, 0, 18, 0x30, 2, hFlip: true),
        P(4, 16, 18, 0x3A, 2), P(12, 16, 18, 0x3C, 2), P(20, 16, 18, 0x3A, 2, hFlip: true)
    ];

    private static SpritePart[] RotoDiscParts() =>
        [P(0, 0, 18, 0x18, 1), P(8, 0, 18, 0x1A, 1)];

    private static SpritePart[] PiranhaParts(int headPattern, int headPalette, int headY, bool flipped = false)
    {
        var stemY = flipped ? headY - 16 : headY + 16;
        var secondHeadPattern = headPattern == 0x20 ? headPattern : headPattern + 2;
        return
        [
            P(8, headY, 79, headPattern, headPalette, vFlip: flipped),
            P(16, headY, 79, secondHeadPattern, headPalette, hFlip: headPattern == 0x20, vFlip: flipped),
            P(8, stemY, 79, 0x22, 2, vFlip: flipped),
            P(16, stemY, 79, 0x22, 2, hFlip: true, vFlip: flipped)
        ];
    }

    private static SpritePart P(
        int x,
        int y,
        int bank,
        int pattern,
        int palette,
        bool hFlip = false,
        bool vFlip = false) => new(x, y, bank, pattern, palette, hFlip, vFlip);

    private static IReadOnlyList<DocumentedEnemy> LoadDocumentedEnemies()
    {
        var assembly = typeof(Smb3LevelRenderer).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith("EnemyMetadata.json", StringComparison.Ordinal));
        if (resourceName is null) return [];

        using var stream = assembly.GetManifestResourceStream(resourceName);
        return stream is null
            ? []
            : JsonSerializer.Deserialize<List<DocumentedEnemy>>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
    }

    private static IReadOnlyDictionary<byte, SpritePart[]> BuildEnemyPreviewParts()
    {
        var result = BuiltInEnemyPreviewParts.ToDictionary(item => item.Key, item => item.Value);
        foreach (var enemy in DocumentedEnemies.Where(item => item.Id is >= 0 and <= byte.MaxValue && item.Sprites.Count > 0))
        {
            result[(byte)enemy.Id] = enemy.Sprites
                .Select(sprite => P(sprite.X, sprite.Y, sprite.Bank, sprite.Pattern, sprite.Palette,
                    sprite.HFlip, sprite.VFlip))
                .ToArray();
        }
        return result;
    }

    private sealed record DocumentedEnemy(int Id, string Name, string Description, IReadOnlyList<DocumentedSprite> Sprites);
    private sealed record DocumentedSprite(int X, int Y, int Bank, int Pattern, int Palette, bool HFlip, bool VFlip);
    private sealed record SpritePart(int X, int Y, int Bank, int Pattern, int Palette, bool HFlip, bool VFlip);
}
