namespace Smb3Editor.Core.Tests;

public sealed class ChrTileDecoderTests
{
    [Fact]
    public void DecodesTwoBitPlanesWithoutChangingSource()
    {
        var tile = new byte[16];
        tile[0] = 0b1000_0000;
        tile[8] = 0b0100_0000;

        var decoded = ChrTileDecoder.DecodeTile(tile, 0);

        Assert.True(decoded.IsSuccess);
        Assert.Equal((byte)1, decoded.Value![0]);
        Assert.Equal((byte)2, decoded.Value[1]);
        Assert.All(decoded.Value.Skip(2), static pixel => Assert.Equal((byte)0, pixel));
    }

    [Fact]
    public void RejectsOutOfRangeTile()
    {
        var decoded = ChrTileDecoder.DecodeTile(new byte[16], 1);

        Assert.False(decoded.IsSuccess);
        Assert.Contains(decoded.Diagnostics, static diagnostic => diagnostic.Code == "CHR_RANGE");
    }
}

