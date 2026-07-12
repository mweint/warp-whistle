namespace Smb3Editor.Core.Tests;

public sealed class BpsCodecTests
{
    [Fact]
    public void CreatedPatchRoundTripsAndChecksumsTarget()
    {
        var source = Enumerable.Range(0, 512).Select(static value => (byte)value).ToArray();
        var target = source.ToArray();
        target[17] ^= 0x55;
        target[400] ^= 0xAA;

        var codec = new BpsCodec();
        var patch = codec.Create(source, target);
        var applied = codec.Apply(source, patch);

        Assert.True(applied.IsSuccess);
        Assert.Equal(target, applied.Value);
    }

    [Fact]
    public void CorruptPatchIsRejected()
    {
        var codec = new BpsCodec();
        var patch = codec.Create([1, 2, 3], [1, 9, 3]);
        patch[8] ^= 0x40;

        var applied = codec.Apply([1, 2, 3], patch);

        Assert.False(applied.IsSuccess);
        Assert.Contains(applied.Diagnostics, static diagnostic => diagnostic.Code == "BPS_PATCH_CRC");
    }
}

