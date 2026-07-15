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
}
