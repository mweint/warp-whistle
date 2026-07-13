namespace Smb3Editor.Core.Tests;

public sealed class PatchCompilerTests
{
    [Fact]
    public void OptionalPrg1RomKeepsVanillaOutputIdenticalAndAppliesOptInPatches()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var source = RomImage.Load(path);
        Assert.True(source.IsSuccess, string.Join(Environment.NewLine, source.Diagnostics));
        if (source.Value!.Profile.Id != "us-prg1") return;

        var compiler = new RomCompiler();
        var vanilla = compiler.Compile(ProjectDocumentV2.Create(source.Value), source.Value);
        Assert.True(vanilla.IsSuccess, string.Join(Environment.NewLine, vanilla.Diagnostics));
        Assert.Equal(source.Value.Bytes, vanilla.Value!.RomBytes);

        var enabled = ProjectDocumentV2.Create(source.Value) with
        {
            Patches = new PatchSettings(
                new PatchSetting(LevelOverrides: new Dictionary<string, bool> { ["W1-1"] = true }),
                new PatchSetting(EnabledByDefault: true))
        };
        var enhanced = compiler.Compile(enabled, source.Value);

        Assert.True(enhanced.IsSuccess, string.Join(Environment.NewLine, enhanced.Diagnostics));
        Assert.Equal(source.Value.Bytes.Length, enhanced.Value!.RomBytes.Length);
        Assert.Equal((byte)4, enhanced.Value.RomBytes[6] >> 4);
        AssertHookTargetsEntryOpcode(enhanced.Value.RomBytes, 0x3CF9E, 0xA5);
        AssertHookTargetsEntryOpcode(enhanced.Value.RomBytes, 0x3CE6D, 0xAD);
        var runtime = enhanced.Value.RomBytes.Skip(0x3E250).Take(128).ToArray();
        Assert.True(ContainsSequence(runtime, [0x4C, 0x91, 0x8F]));
        Assert.True(ContainsSequence(runtime, [0x4C, 0x60, 0x8E]));
        Assert.True(ContainsSequence(runtime, [0xA9, 0x01, 0x8D, 0x13, 0x07]));
        Assert.Contains("4C11E9", Convert.ToHexString(enhanced.Value.RomBytes.Skip(0x3E250).Take(128).ToArray()));
        Assert.Equal(new byte[] { 0xA2, 0xFF, 0x9A, 0xEE, 0x55 }, enhanced.Value.RomBytes.Skip(0x3E921).Take(5).ToArray());
        Assert.True(ContainsSequence(enhanced.Value.RomBytes.AsSpan(0x3E921, 64), [0xAD, 0xB9, 0x7E, 0x85, 0x61]));
        Assert.True(ContainsSequence(enhanced.Value.RomBytes.AsSpan(0x3E921, 111), [0xAD, 0xBC, 0x7E, 0x85, 0x66, 0xA9, 0x00, 0x8D, 0x13, 0x07, 0xA9, 0x00, 0x85, 0x14, 0xA9, 0x00, 0x85, 0xF1, 0x8D, 0x76, 0x03, 0xA2, 0x17, 0x9D, 0xE0, 0x04, 0xCA, 0x10, 0xFB, 0xA9, 0x01, 0x8D, 0xF0, 0x7E, 0x4C, 0xC8, 0x88]));
        Assert.Contains("ADB97E8561ADB A".Replace(" ", ""), Convert.ToHexString(enhanced.Value.RomBytes.Skip(0x3E921).Take(64).ToArray()));
        Assert.Contains(enhanced.Diagnostics, item => item.Code == "PATCH_READY");
    }

    [Fact]
    public void PatchSettingUsesLevelOverrideBeforeGlobalDefault()
    {
        var setting = new PatchSetting(true, new Dictionary<string, bool> { ["W1-1"] = false });
        Assert.False(setting.IsEnabledFor("W1-1"));
        Assert.True(setting.IsEnabledFor("W1-2"));
    }

    [Fact]
    public void GlobalOnlyPatchesUseTheDirectResolverInsteadOfScanningAnEmptyTable()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var source = RomImage.Load(path);
        Assert.True(source.IsSuccess, string.Join(Environment.NewLine, source.Diagnostics));
        if (source.Value!.Profile.Id != "us-prg1") return;

        var project = ProjectDocumentV2.Create(source.Value) with
        {
            Patches = new PatchSettings(new PatchSetting(EnabledByDefault: true), new PatchSetting(EnabledByDefault: true))
        };
        var result = new RomCompiler().Compile(project, source.Value);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Diagnostics));
        AssertHookTargetsEntryOpcode(result.Value!.RomBytes, 0x3CF9E, 0xA5);
        AssertHookTargetsEntryOpcode(result.Value.RomBytes, 0x3CE6D, 0xAD);
        var runtime = result.Value!.RomBytes.Skip(0x3E250).Take(128).ToArray();
        Assert.Contains("A90360", Convert.ToHexString(result.Value!.RomBytes.Skip(0x3E250).Take(128).ToArray()));
        Assert.DoesNotContain("A200BD", Convert.ToHexString(result.Value.RomBytes.Skip(0x3E250).Take(128).ToArray()));
    }

    private static void AssertHookTargetsEntryOpcode(byte[] rom, int hookOffset, byte expectedOpcode)
    {
        Assert.Equal(0x4C, rom[hookOffset]);
        var cpuAddress = rom[hookOffset + 1] | (rom[hookOffset + 2] << 8);
        Assert.InRange(cpuAddress, 0xE000, 0xFFFF);
        var fileOffset = 0x3E010 + (cpuAddress - 0xE000);
        Assert.Equal(expectedOpcode, rom[fileOffset]);
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> sequence)
    {
        for (var i = 0; i <= bytes.Length - sequence.Length; i++)
            if (bytes.Slice(i, sequence.Length).SequenceEqual(sequence)) return true;
        return false;
    }

}
