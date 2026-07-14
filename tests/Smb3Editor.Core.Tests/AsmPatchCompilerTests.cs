namespace Smb3Editor.Core.Tests;

public sealed class AsmPatchCompilerTests
{
    [Fact]
    public void ShippedStartSelectExampleBuildsAgainstVerifiedPrg1()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var source = RomImage.Load(path);
        Assert.True(source.IsSuccess, string.Join(Environment.NewLine, source.Diagnostics));
        if (source.Value!.Profile.Id != "us-prg1") return;

        var package = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "patches", "start-select-map"));
        var result = new AsmPatchCompiler(new Asm6fAssembler()).Apply(package, source.Value, source.Value.Bytes);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal([0x4C, 0x40, 0xE2], result.Value!.AsSpan(0x3CE6D, 3).ToArray());
        Assert.Equal([0xAD, 0x76, 0x03], result.Value.AsSpan(0x3E250, 3).ToArray());
    }
}
