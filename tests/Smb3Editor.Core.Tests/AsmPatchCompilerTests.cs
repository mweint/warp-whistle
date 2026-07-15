namespace Smb3Editor.Core.Tests;

public sealed class AsmPatchCompilerTests
{
    [Fact]
    public void ShippedBuiltInPackageAssemblesAgainstVerifiedPrg1()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var source = RomImage.Load(path);
        Assert.True(source.IsSuccess, string.Join(Environment.NewLine, source.Diagnostics));
        if (source.Value!.Profile.Id != "us-prg1") return;

        var package = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "patches", "builtins"));
        var result = new AsmPatchCompiler(new Asm6fAssembler()).Apply(package, source.Value, source.Value.Bytes);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal(0x4C, result.Value![0x3CF9E]);
        Assert.Equal(0x4C, result.Value[0x3CE6D]);
        Assert.Equal(0x20, result.Value[0x13C0B]);
        Assert.Equal([0xA5, 0xF1], result.Value.AsSpan(0x3E250, 2).ToArray());
        Assert.True(Contains(result.Value, [0xA5, 0x18, 0x0D, 0x17, 0x05, 0x29, 0x20]), "Start+Select must read newly-pressed buttons from zero-page $18.");
    }

    [Fact]
    public void CatalogDiscoversFeatureMetadataAndRequirements()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "patches"));
        var result = PatchCatalog.Discover(root);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Diagnostics));
        var scroll = Assert.Single(result.Value!.SelectMany(static package => package.Features), feature => feature.Id == "continuous-auto-scroll");
        Assert.False(scroll.RecommendedDefault);
        Assert.True(scroll.SupportsLevelOverrides);
        Assert.Equal(211, Assert.Single(scroll.Requirements).EnemyId);

        var infiniteLives = Assert.Single(result.Value!.SelectMany(static package => package.Features), feature => feature.Id == "infinite-lives");
        Assert.False(infiniteLives.RecommendedDefault);
        Assert.False(infiniteLives.SupportsLevelOverrides);
    }

    [Fact]
    public void InfiniteLivesPatchOnlyReplacesTheVerifiedLifeLossInstruction()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var source = RomImage.Load(path);
        Assert.True(source.IsSuccess, string.Join(Environment.NewLine, source.Diagnostics));
        if (source.Value!.Profile.Id != "us-prg1") return;

        var package = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "patches", "infinite-lives"));
        var result = new AsmPatchCompiler(new Asm6fAssembler()).Apply(
            package,
            source.Value,
            source.Value.Bytes,
            new HashSet<string>(StringComparer.Ordinal) { "infinite-lives" });

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal([0xEA, 0xEA, 0xEA], result.Value!.AsSpan(0x3D133, 3).ToArray());
        Assert.Equal(source.Value.Bytes.AsSpan(0x3D130, 3).ToArray(), result.Value.AsSpan(0x3D130, 3).ToArray());
        Assert.Equal(source.Value.Bytes.AsSpan(0x3D136, 3).ToArray(), result.Value.AsSpan(0x3D136, 3).ToArray());
    }

    private static bool Contains(byte[] bytes, byte[] pattern)
    {
        for (var index = 0; index <= bytes.Length - pattern.Length; index++)
            if (bytes.AsSpan(index, pattern.Length).SequenceEqual(pattern)) return true;
        return false;
    }
}
