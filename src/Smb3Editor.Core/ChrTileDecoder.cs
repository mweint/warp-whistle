namespace Smb3Editor.Core;

public static class ChrTileDecoder
{
    public const int BytesPerTile = 16;
    public const int TilePixels = 8;

    public static OperationResult<byte[]> DecodeTile(ReadOnlySpan<byte> chr, int tileIndex)
    {
        if (tileIndex < 0 || tileIndex > (chr.Length / BytesPerTile) - 1)
        {
            return OperationResult<byte[]>.Failure(
                Diagnostics.Error("CHR_RANGE", $"CHR tile {tileIndex} is outside the supplied pattern data."));
        }

        var offset = tileIndex * BytesPerTile;
        var pixels = new byte[TilePixels * TilePixels];
        for (var row = 0; row < TilePixels; row++)
        {
            var low = chr[offset + row];
            var high = chr[offset + row + 8];
            for (var column = 0; column < TilePixels; column++)
            {
                var shift = 7 - column;
                pixels[(row * TilePixels) + column] = (byte)(((low >> shift) & 1) | (((high >> shift) & 1) << 1));
            }
        }

        return OperationResult<byte[]>.Success(pixels);
    }
}

public static class NesPalette
{
    // A deterministic NTSC-like palette for previews. Indices remain the ROM's source of truth.
    public static IReadOnlyList<uint> Argb { get; } =
    [
        0xFF666666, 0xFF002A88, 0xFF1412A7, 0xFF3B00A4, 0xFF5C007E, 0xFF6E0040, 0xFF6C0700, 0xFF561D00,
        0xFF333500, 0xFF0B4800, 0xFF005200, 0xFF004F08, 0xFF00404D, 0xFF000000, 0xFF000000, 0xFF000000,
        0xFFADADAD, 0xFF155FD9, 0xFF4240FF, 0xFF7527FE, 0xFFA01ACC, 0xFFB71E7B, 0xFFB53120, 0xFF994E00,
        0xFF6B6D00, 0xFF388700, 0xFF0C9300, 0xFF008F32, 0xFF007C8D, 0xFF000000, 0xFF000000, 0xFF000000,
        0xFFFFFFFF, 0xFF64B0FF, 0xFF9290FF, 0xFFC676FF, 0xFFF36AFF, 0xFFFF6ECC, 0xFFFF8170, 0xFFEA9E22,
        0xFFBCBE00, 0xFF88D800, 0xFF5CE430, 0xFF45E082, 0xFF48CDDE, 0xFF4F4F4F, 0xFF000000, 0xFF000000,
        0xFFFFFFFF, 0xFFC0DFFF, 0xFFD3D2FF, 0xFFE8C8FF, 0xFFFBC2FF, 0xFFFFC4EA, 0xFFFFCCC5, 0xFFF7D8A5,
        0xFFE4E594, 0xFFCFEE96, 0xFFBDF4AB, 0xFFB3F3CC, 0xFFB5EBF2, 0xFFB8B8B8, 0xFF000000, 0xFF000000
    ];
}

