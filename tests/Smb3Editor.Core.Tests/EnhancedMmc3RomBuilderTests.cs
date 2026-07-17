namespace Smb3Editor.Core.Tests;

public sealed class EnhancedMmc3RomBuilderTests
{
    [Fact]
    public void ExpandedFoundationPreservesFixedBanksAndAllocatesInsertedSpace()
    {
        var profile = Smb3Profiles.All.Single(static item => item.Id == "us-prg1");
        var bytes = new byte[16 + 0x40000 + 0x20000];
        bytes[0] = (byte)'N'; bytes[1] = (byte)'E'; bytes[2] = (byte)'S'; bytes[3] = 0x1A;
        bytes[4] = 16; bytes[5] = 16; bytes[6] = 0x40;
        for (var bank = 0; bank < 32; bank++)
        {
            bytes.AsSpan(16 + bank * 0x2000, 0x2000).Fill((byte)bank);
        }

        var source = RomImage.CreateForTesting("test.nes", bytes, profile);
        var project = ProjectDocumentV2.Create(source) with
        {
            OutputMode = RomOutputMode.EnhancedMmc3,
            StorageMode = RomStorageMode.ManagedVanilla
        };

        var result = new EnhancedMmc3RomBuilder().Build(project, source, source.Bytes);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Diagnostics));
        var output = result.Value!.RomBytes;
        Assert.Equal(16 + 0x80000 + 0x20000, output.Length);
        Assert.Equal(32, output[4]);
        Assert.Equal(0x02, output[6] & 0x02);
        Assert.Equal(1, output[8]);
        Assert.Equal(bytes.AsSpan(16, 30 * 0x2000).ToArray(), output.AsSpan(16, 30 * 0x2000).ToArray());
        Assert.Equal(bytes.AsSpan(16 + 30 * 0x2000, 2 * 0x2000).ToArray(), output.AsSpan(16 + 62 * 0x2000, 2 * 0x2000).ToArray());
        Assert.All(output.AsSpan(16 + 30 * 0x2000, 32 * 0x2000).ToArray(), static value => Assert.Equal(0xFF, value));
        Assert.Equal(0x32000, result.Value.Allocation.UsedBytes);
    }

    [Fact]
    public void VanillaModeIsRejectedByEnhancedBuilder()
    {
        var profile = Smb3Profiles.All.Single(static item => item.Id == "us-prg1");
        var bytes = new byte[16 + 0x40000 + 0x20000];
        bytes[0] = (byte)'N'; bytes[1] = (byte)'E'; bytes[2] = (byte)'S'; bytes[3] = 0x1A;
        bytes[4] = 16; bytes[5] = 16; bytes[6] = 0x40;
        var source = RomImage.CreateForTesting("test.nes", bytes, profile);

        var result = new EnhancedMmc3RomBuilder().Build(ProjectDocumentV2.Create(source), source, source.Bytes);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static item => item.Code == "ENHANCED_MODE");
    }
}
