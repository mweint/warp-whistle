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
        Assert.Equal((byte)0xAE, direct.Value.RomBytes[0x3CF9E]);
        Assert.Equal((byte)0x26, direct.Value.RomBytes[0x3CF9F]);
        Assert.Equal((byte)0x07, direct.Value.RomBytes[0x3CFA0]);
        // The PRG30 payload bridge must start after a complete instruction.
        Assert.Equal(new byte[] { 0x4C, 0x60, 0x9F }, direct.Value.RomBytes.Skip(0x3DF4C).Take(3));
        Assert.Equal(
            new byte[] { 0xA9, 0x04, 0x8D, 0x36, 0x07, 0xA9, 0x01, 0x8D, 0xF0, 0x7E, 0x4C, 0xC8, 0x88 },
            direct.Value.RomBytes.Skip(0x3DF70).Take(13));
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
    public void DirectLevelBuildPreservesEnabledPatchRuntime()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var source = RomImage.Load(path);
        Assert.True(source.IsSuccess, string.Join(Environment.NewLine, source.Diagnostics));
        if (source.Value!.Profile.Id != "us-prg1") return;

        var project = ProjectDocumentV2.Create(source.Value) with
        {
            Patches = new PatchSettings(
                QuickRetry: new PatchSetting(EnabledByDefault: true),
                ContinuousAutoScroll: new PatchSetting(
                    LevelOverrides: new Dictionary<string, bool> { ["W1-4"] = true }))
        };
        var compiled = new RomCompiler().Compile(project, source.Value);
        Assert.True(compiled.IsSuccess, string.Join(Environment.NewLine, compiled.Diagnostics));

        var compiledBytes = compiled.Value!.RomBytes.ToArray();
        var direct = new DirectLevelTestBuilder().Build(compiled.Value!, source.Value, source.Value.Profile.Levels["W1-4"]);

        Assert.True(direct.IsSuccess, string.Join(Environment.NewLine, direct.Diagnostics));
        Assert.Equal(compiledBytes.Skip(0x3E250).Take(128), direct.Value!.RomBytes.Skip(0x3E250).Take(128));
        Assert.Equal(compiledBytes.Skip(0x3E921).Take(111), direct.Value.RomBytes.Skip(0x3E921).Take(111));
        Assert.Equal(compiledBytes.Skip(0x3CF3E).Take(3), direct.Value.RomBytes.Skip(0x3CF3E).Take(3));
        Assert.Equal(compiledBytes.Skip(0x3C937).Take(3), direct.Value.RomBytes.Skip(0x3C937).Take(3));
        Assert.Equal(compiledBytes, compiled.Value.RomBytes);
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

    [Fact]
    public void OptionalPrg1DirectLevelUsesRelocatedSpritePointer()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
        var source = loaded.Value!;
        if (source.Profile.Id != "us-prg1") return;

        var target = source.Profile.Levels["W1-1"];
        var decoded = Smb3LevelCodec.Decode(source, target);
        Assert.True(decoded.IsSuccess, string.Join(Environment.NewLine, decoded.Diagnostics));
        var document = decoded.Value!;
        var template = document.Enemies[0];
        var grown = document with
        {
            Enemies = document.Enemies.Append(template with { Index = document.Enemies.Count }).ToArray()
        };
        var project = ProjectDocumentV2.Create(source) with { StorageMode = RomStorageMode.ManagedVanilla };
        var compiled = new RomCompiler().Compile(project.WithArea(grown), source);
        Assert.True(compiled.IsSuccess, string.Join(Environment.NewLine, compiled.Diagnostics));
        var graph = Prg1ReferenceIndexBuilder.BuildCurrent(source, compiled.Value!.RomBytes);
        Assert.True(graph.IsSuccess, string.Join(Environment.NewLine, graph.Diagnostics));
        var originalRoot = Prg1ReferenceIndexBuilder.Build(source).Value!.Roots.First(root =>
            root.Layout == new Prg1LayoutStreamId(target.LayoutOffset, target.Tileset) &&
            root.Enemy == new Prg1EnemyStreamId(target.EnemyOffset));
        var resolvedEnemy = graph.Value!.Roots.Single(root => root.Ordinal == originalRoot.Ordinal).Enemy!.Value;
        var expectedPointer = (ushort)(0xC000 + ((resolvedEnemy.FileOffset - source.PrgOffset) & 0x1FFF));

        var direct = new DirectLevelTestBuilder().Build(compiled.Value, source, target);

        Assert.True(direct.IsSuccess, string.Join(Environment.NewLine, direct.Diagnostics));
        const int prepareHarnessOffset = 0x3DF20;
        var harness = direct.Value!.RomBytes.AsSpan(prepareHarnessOffset, 45).ToArray();
        Assert.True(ContainsSequence(harness, [0xA9, (byte)expectedPointer, 0x85, 0x65]));
        Assert.True(ContainsSequence(harness, [0xA9, (byte)(expectedPointer >> 8), 0x85, 0x66]));
    }

    private static bool ContainsSequence(byte[] bytes, byte[] expected)
    {
        for (var index = 0; index <= bytes.Length - expected.Length; index++)
            if (bytes.AsSpan(index, expected.Length).SequenceEqual(expected)) return true;
        return false;
    }
}
