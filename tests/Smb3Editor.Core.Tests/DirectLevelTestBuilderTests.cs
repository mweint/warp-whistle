namespace Smb3Editor.Core.Tests;

public sealed class DirectLevelTestBuilderTests
{
    [Fact]
    public void OptionalPrg1RomBuildsAndVerifiesDirectLevelImageWithoutChangingNormalCompile()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var source = RomImage.Load(path);
        Assert.True(source.IsSuccess, string.Join(Environment.NewLine, source.Diagnostics));
        if (source.Value!.Profile.Id != "us-prg1") return;

        var project = ProjectDocumentV2.Create(source.Value);
        var normal = new RomCompiler().Compile(project, source.Value);
        Assert.True(normal.IsSuccess, string.Join(Environment.NewLine, normal.Diagnostics));
        Assert.Equal(source.Value.Bytes, normal.Value!.RomBytes);

        var target = source.Value.Profile.Levels["W1-1"];
        var builder = new DirectLevelTestBuilder();
        var direct = builder.Build(normal.Value, source.Value, target);

        Assert.True(direct.IsSuccess, string.Join(Environment.NewLine, direct.Diagnostics));
        Assert.NotEqual(normal.Value.RomBytes, direct.Value!.RomBytes);
        Assert.True(builder.VerifyReadback(direct.Value, direct.Value.RomBytes).IsSuccess);
        Assert.Equal((byte)0x4C, direct.Value.RomBytes[0x3C4AD]);
        Assert.Equal((byte)0x20, direct.Value.RomBytes[0x3C937]);
        Assert.Equal((byte)0x4C, direct.Value.RomBytes[0x3CF9E]);
        Assert.Equal((byte)0x40, direct.Value.RomBytes[0x3CF9F]);
        Assert.Equal((byte)0xE2, direct.Value.RomBytes[0x3CFA0]);
    }

    [Fact]
    public void ReadbackRejectsChangedTemporaryImage()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var source = RomImage.Load(path);
        Assert.True(source.IsSuccess, string.Join(Environment.NewLine, source.Diagnostics));
        if (source.Value!.Profile.Id != "us-prg1") return;

        var normal = new RomCompiler().Compile(ProjectDocumentV2.Create(source.Value), source.Value);
        Assert.True(normal.IsSuccess);
        var builder = new DirectLevelTestBuilder();
        var direct = builder.Build(normal.Value!, source.Value, source.Value.Profile.Levels["W1-1"]);
        Assert.True(direct.IsSuccess);
        var altered = direct.Value!.RomBytes.ToArray();
        altered[0x3E250] ^= 0x01;

        Assert.False(builder.VerifyReadback(direct.Value, altered).IsSuccess);
    }

    [Fact]
    public void OptionalPrg1RomBuildsEveryCatalogedDirectTarget()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var source = RomImage.Load(path);
        Assert.True(source.IsSuccess, string.Join(Environment.NewLine, source.Diagnostics));
        if (source.Value!.Profile.Id != "us-prg1") return;

        var normal = new RomCompiler().Compile(ProjectDocumentV2.Create(source.Value), source.Value);
        Assert.True(normal.IsSuccess);
        var builder = new DirectLevelTestBuilder();
        foreach (var target in source.Value.Profile.Levels.Values)
        {
            var direct = builder.Build(normal.Value!, source.Value, target);
            Assert.True(direct.IsSuccess, $"{target.DisplayName}: {string.Join(Environment.NewLine, direct.Diagnostics)}");
        }
    }
}
