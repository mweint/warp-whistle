namespace Smb3Editor.Core;

/// <summary>Faithful, read-only composition of a PRG1 world-map tile layout.</summary>
public sealed record OverworldRenderSnapshot(int WidthInTiles, int HeightInTiles, int PixelWidth, int PixelHeight, IReadOnlyList<uint> ArgbPixels);

public static class Smb3OverworldRenderer
{
    private const int PrgBankSize = 0x2000;
    private const int WorldMapTsaBank = 12;
    private const int PaletteBank = 27;
    private const int PalettePointerTable = 0x17D2;
    // Foundry's world-map graphics set starts its first animation frame with
    // CHR segments $14/$15, followed by static map art at $16/$17. Map TSA
    // pattern values address that composed 256-tile view directly.
    private static readonly int[] AnimatedChrPages = [0x14, 0x70, 0x72, 0x74];
    private const int StaticChrPage = 0x16;
    // PRG11 MapObject_Pat1/Pat2, first animated frame. Each value is the
    // first tile of an 8x16 NES sprite; the pair composes a 16x16 map object.
    private static readonly (byte Left, byte Right, byte LeftAttr, byte RightAttr)[] MapSpriteFrames =
    [
        (0x00, 0x00, 0, 0), (0x49, 0x49, 3, 3), (0xC9, 0xCB, 1, 1), (0xC5, 0xC7, 2, 2),
        (0xC5, 0xC7, 2, 2), (0xC5, 0xC7, 2, 2), (0xC5, 0xC7, 2, 2), (0xE1, 0xE1, 1, 1),
        (0x21, 0x23, 2, 2), (0x11, 0x21, 3, 3), (0x13, 0x15, 3, 3), (0x17, 0x19, 3, 3),
        (0xFD, 0xFF, 3, 3), (0xF5, 0xF7, 2, 2), (0xE5, 0xE7, 2, 2), (0xFD, 0xFF, 1, 1),
        (0x71, 0x73, 2, 2)
    ];

    public static OperationResult<OverworldRenderSnapshot> Render(
        RomImage rom,
        OverworldDocument world,
        int animationFrame = 0,
        IReadOnlyList<OverworldPaletteOverride>? paletteOverrides = null)
    {
        try
        {
            if (!string.Equals(rom.Profile.Id, "us-prg1", StringComparison.Ordinal))
                return OperationResult<OverworldRenderSnapshot>.Failure(Diagnostics.Error("OVERWORLD_PROFILE", "The overworld view currently supports verified US PRG1 ROMs only."));
            if (world.Tiles.Count != world.Width * OverworldDocument.ScreenHeight)
                return OperationResult<OverworldRenderSnapshot>.Failure(Diagnostics.Error("OVERWORLD_LAYOUT", "The world-map tile layout has invalid dimensions."));

            var tsa = rom.Prg.Slice(WorldMapTsaBank * PrgBankSize, 0x400);
            var paletteBank = rom.Prg.Slice(PaletteBank * PrgBankSize, PrgBankSize);
            var pointer = paletteBank[PalettePointerTable] | (paletteBank[PalettePointerTable + 1] << 8);
            var paletteOffset = pointer - 0xA000 + (world.TilePalette * 16);
            if (paletteOffset < 0 || paletteOffset > paletteBank.Length - 16)
                return OperationResult<OverworldRenderSnapshot>.Failure(Diagnostics.Error("OVERWORLD_PALETTE", "The world-map palette points outside the verified palette bank."));
            var palette = paletteOverrides?.LastOrDefault(item => !item.Sprites && item.Palette == world.TilePalette)?.Colors is { Count: 16 } overrideColors
                ? overrideColors.ToArray()
                : paletteBank.Slice(paletteOffset, 16).ToArray();
            var pixelWidth = world.Width * 16;
            var pixelHeight = OverworldDocument.ScreenHeight * 16;
            var pixels = new uint[pixelWidth * pixelHeight];
            var decoded = new Dictionary<(byte Pattern, int Frame), byte[]>();

            for (var y = 0; y < OverworldDocument.ScreenHeight; y++)
            for (var x = 0; x < world.Width; x++)
            {
                var tile = world.TileAt(x, y);
                var paletteIndex = tile >> 6;
                var screen = x / OverworldDocument.ScreenWidth;
                // Stock map exceptions observed in Foundry's map drawer.
                var frame = world.World == 4 || (world.World == 7 && screen == 3) ? 0 : animationFrame & 3;
                Draw(0, 0, tsa[tile], frame);
                Draw(0, 8, tsa[0x100 + tile], frame);
                Draw(8, 0, tsa[0x200 + tile], frame);
                Draw(8, 8, tsa[0x300 + tile], frame);

                void Draw(int localX, int localY, byte pattern, int frame)
                {
                    var key = (Pattern: pattern, Frame: frame);
                    if (!decoded.TryGetValue(key, out var bits))
                    {
                        var offset = pattern < 0x80
                            ? (AnimatedChrPages[frame] * 0x400) + (pattern * ChrTileDecoder.BytesPerTile)
                            : (StaticChrPage * 0x400) + ((pattern - 0x80) * ChrTileDecoder.BytesPerTile);
                        if (offset > rom.Chr.Length - ChrTileDecoder.BytesPerTile)
                            throw new InvalidOperationException("A world-map tile points outside CHR ROM.");
                        bits = ChrTileDecoder.DecodeTile(rom.Chr.Slice(offset, ChrTileDecoder.BytesPerTile), 0).Value!;
                        decoded[key] = bits;
                    }
                    for (var py = 0; py < 8; py++)
                    for (var px = 0; px < 8; px++)
                    {
                        var color = bits[(py * 8) + px];
                        var paletteColor = palette[color == 0 ? 0 : (paletteIndex * 4) + color] & 0x3F;
                        pixels[((y * 16 + localY + py) * pixelWidth) + (x * 16 + localX + px)] = NesPalette.Argb[paletteColor];
                    }
                }
            }
            return OperationResult<OverworldRenderSnapshot>.Success(new OverworldRenderSnapshot(world.Width, OverworldDocument.ScreenHeight, pixelWidth, pixelHeight, pixels));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return OperationResult<OverworldRenderSnapshot>.Failure(Diagnostics.Error("OVERWORLD_RENDER", ex.Message));
        }
    }

    public static OperationResult<MetatilePreview> RenderTilePreview(
        RomImage rom,
        OverworldDocument paletteSource,
        byte tile,
        IReadOnlyList<OverworldPaletteOverride>? paletteOverrides = null)
    {
        // Rendering a full synthetic screen deliberately reuses the verified map renderer;
        // callers only retain the first 16×16 metatile preview.
        var synthetic = paletteSource with { Tiles = Enumerable.Repeat(tile, OverworldDocument.ScreenWidth * OverworldDocument.ScreenHeight).ToArray() };
        var rendered = Render(rom, synthetic, paletteOverrides: paletteOverrides);
        if (!rendered.IsSuccess) return OperationResult<MetatilePreview>.Failure(rendered.Diagnostics.ToArray());
        var pixels = new uint[16 * 16];
        for (var y = 0; y < 16; y++) Array.Copy(rendered.Value!.ArgbPixels.ToArray(), y * rendered.Value.PixelWidth, pixels, y * 16, 16);
        return OperationResult<MetatilePreview>.Success(new MetatilePreview(16, 16, pixels));
    }

    /// <summary>Composes the stock 16x16 overworld map-object sprite from PRG11's pattern tables and the user ROM's CHR.</summary>
    public static OperationResult<MetatilePreview> RenderMapSpritePreview(
        RomImage rom,
        OverworldDocument paletteSource,
        byte type,
        IReadOnlyList<OverworldPaletteOverride>? paletteOverrides = null)
    {
        try
        {
            if (type == 0 || type >= MapSpriteFrames.Length)
                return OperationResult<MetatilePreview>.Success(new MetatilePreview(16, 16, new uint[16 * 16]));
            var paletteBank = rom.Prg.Slice(PaletteBank * PrgBankSize, PrgBankSize);
            var pointer = paletteBank[PalettePointerTable] | (paletteBank[PalettePointerTable + 1] << 8);
            var offset = pointer - 0xA000 + (paletteSource.SpritePalette * 16);
            if (offset < 0 || offset > paletteBank.Length - 16)
                return OperationResult<MetatilePreview>.Failure(Diagnostics.Error("OVERWORLD_SPRITE_PALETTE", "The map-sprite palette points outside the verified palette bank."));
            var palette = paletteOverrides?.LastOrDefault(item => item.Sprites && item.Palette == paletteSource.SpritePalette)?.Colors is { Count: 16 } changed
                ? changed.ToArray() : paletteBank.Slice(offset, 16).ToArray();
            var pixels = new uint[16 * 16];
            var frame = MapSpriteFrames[type];
            Draw(frame.Left, frame.LeftAttr, 0);
            Draw(frame.Right, frame.RightAttr, 8);
            return OperationResult<MetatilePreview>.Success(new MetatilePreview(16, 16, pixels));

            void Draw(byte pattern, byte attribute, int xBase)
            {
                // World-map sprites are 8x16 PPU sprites, not map TSA tiles.  PRG030
                // maps CHR banks $20-$23 into PPU $1000-$1FFF before these PRG011
                // pattern tables are used.  The low bit selects that PPU pattern table.
                var ppuOffset = ((pattern & 0xFE) * ChrTileDecoder.BytesPerTile) + 0x1000;
                for (var tile = 0; tile < 2; tile++)
                {
                    var tilePpuOffset = ppuOffset + (tile * ChrTileDecoder.BytesPerTile);
                    var slot = (tilePpuOffset - 0x1000) / 0x400;
                    var chrOffset = ((0x20 + slot) * 0x400) + (tilePpuOffset & 0x3FF);
                    if (slot is < 0 or > 3 || chrOffset < 0 || chrOffset > rom.Chr.Length - ChrTileDecoder.BytesPerTile)
                        throw new InvalidOperationException("A map-object sprite points outside CHR ROM.");
                    var decoded = ChrTileDecoder.DecodeTile(rom.Chr.Slice(chrOffset, ChrTileDecoder.BytesPerTile), 0).Value!;
                    for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++)
                    {
                        var sourceX = (attribute & 0x40) != 0 ? 7 - x : x;
                        var sourceY = (attribute & 0x80) != 0 ? 7 - y : y;
                        var color = decoded[(sourceY * 8) + sourceX];
                        if (color == 0) continue;
                        pixels[((tile * 8 + y) * 16) + xBase + x] = NesPalette.Argb[palette[(attribute * 4) + color] & 0x3F];
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return OperationResult<MetatilePreview>.Failure(Diagnostics.Error("OVERWORLD_SPRITE_RENDER", ex.Message));
        }
    }
}
