namespace Smb3Editor.Core.Tests;

public sealed class Prg1VanillaRelocationTests
{
    [Fact]
    public void OptionalAuthenticatedPrg1UnchangedProjectIsByteIdentical()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;

        var built = new RomCompiler().Compile(ProjectDocumentV2.Create(source), source);

        Assert.True(built.IsSuccess, Messages(built.Diagnostics));
        Assert.Equal(source.Bytes, built.Value!.RomBytes);
    }

    [Fact]
    public void OptionalAuthenticatedPrg1OverflowingLayoutRelocatesAliasesAndJumpSource()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;
        var before = RequireGraph(source);
        var document = RequireDocument(source, "W1-1");
        var grown = GrowLayout(document, 1);

        var built = new RomCompiler().Compile(ProjectDocumentV2.Create(source).WithArea(grown), source);

        Assert.True(built.IsSuccess, Messages(built.Diagnostics));
        var after = RequireGraph(source, built.Value!.RomBytes);
        var oldId = new Prg1LayoutStreamId(source.Profile.Levels["W1-1"].LayoutOffset, document.Tileset);
        var referringRoots = before.Roots.Where(root => root.Layout == oldId).ToArray();
        Assert.NotEmpty(referringRoots);
        var newId = after.Roots.Single(root => root.Ordinal == referringRoots[0].Ordinal).Layout;
        Assert.NotEqual(oldId, newId);
        Assert.All(referringRoots, root => Assert.Equal(newId, after.Roots.Single(item => item.Ordinal == root.Ordinal).Layout));
        Assert.Equal(GrowEncodedLayoutLength(document, 1), after.Layouts.Single(stream => stream.Id == newId).Length);
        Assert.Contains(after.JumpReferences, jump => jump.SourceLayout == newId);
        var oldStream = before.Layouts.Single(stream => stream.Id == oldId);
        Assert.Equal(oldStream.PointerSites.Count(static site => site.Origin == Prg1PointerSiteOrigin.LayoutHeader),
            after.Layouts.Single(stream => stream.Id == newId).PointerSites
                .Count(static site => site.Origin == Prg1PointerSiteOrigin.LayoutHeader));
    }

    [Fact]
    public void OptionalAuthenticatedPrg1OverflowingEnemyStreamRelocatesEveryAlias()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;
        var before = RequireGraph(source);
        var document = RequireDocument(source, "W1-1");
        var grown = GrowEnemies(document, 1);

        var built = new RomCompiler().Compile(ProjectDocumentV2.Create(source).WithArea(grown), source);

        Assert.True(built.IsSuccess, Messages(built.Diagnostics));
        var after = RequireGraph(source, built.Value!.RomBytes);
        var oldId = new Prg1EnemyStreamId(source.Profile.Levels["W1-1"].EnemyOffset);
        var referringRoots = before.Roots.Where(root => root.Enemy == oldId).ToArray();
        var newId = after.Roots.Single(root => root.Ordinal == referringRoots[0].Ordinal).Enemy;
        Assert.NotNull(newId);
        Assert.NotEqual(oldId, newId.Value);
        Assert.All(referringRoots, root => Assert.Equal(newId, after.Roots.Single(item => item.Ordinal == root.Ordinal).Enemy));
        Assert.Equal(document.OriginalEnemyLength + 3, after.Enemies.Single(stream => stream.Id == newId.Value).Length);
    }

    [Fact]
    public void OptionalAuthenticatedPrg1SharedPoolAllocationIsDeterministic()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;
        var project = ProjectDocumentV2.Create(source)
            .WithArea(GrowLayout(RequireDocument(source, "W1-1"), 1));
        var compiler = new RomCompiler();

        var first = compiler.Compile(project, source);
        var second = compiler.Compile(project, source);

        Assert.True(first.IsSuccess, Messages(first.Diagnostics));
        Assert.True(second.IsSuccess, Messages(second.Diagnostics));
        Assert.Equal(first.Value!.RomBytes, second.Value!.RomBytes);
        RequireGraph(source, first.Value.RomBytes);
    }

    [Fact]
    public void OptionalAuthenticatedPrg1PoolExhaustionFailsWithoutReturningPartialRom()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;
        var document = RequireDocument(source, "W1-1");
        var grown = GrowLayout(document, 3000);

        var built = new RomCompiler().Compile(ProjectDocumentV2.Create(source).WithArea(grown), source);

        Assert.False(built.IsSuccess);
        Assert.Null(built.Value);
        Assert.Contains(built.Diagnostics, static item => item.Code == "RELOC_LAYOUT_FULL");
    }

    [Fact]
    public void OptionalAuthenticatedPrg1ModifiedAreasShareOneCompactedBank()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;
        var project = ProjectDocumentV2.Create(source)
            .WithArea(GrowLayout(RequireDocument(source, "W1-1"), 1))
            .WithArea(GrowLayout(RequireDocument(source, "W1-3"), 1));

        var built = new RomCompiler().Compile(project, source);

        Assert.True(built.IsSuccess, Messages(built.Diagnostics));
        RequireGraph(source, built.Value!.RomBytes);
    }

    [Fact]
    public void OptionalAuthenticatedPrg1SharedPhysicalLayoutBankFailsClosed()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;
        var project = ProjectDocumentV2.Create(source).WithArea(GrowLayout(RequireDocument(source, "W1-2"), 1));

        var built = new RomCompiler().Compile(project, source);

        Assert.False(built.IsSuccess);
        Assert.Null(built.Value);
        Assert.Contains(built.Diagnostics, static item => item.Code == "RELOC_LAYOUT_OVERLAP");
    }

    [Fact]
    public void OptionalAuthenticatedPrg1LevelPatchFlagsUseRelocatedLayoutPointer()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;
        var document = RequireDocument(source, "W1-1");
        var project = ProjectDocumentV2.Create(source).WithArea(GrowLayout(document, 1)) with
        {
            Patches = new PatchSettings(
                QuickRetry: new PatchSetting(LevelOverrides: new Dictionary<string, bool> { ["W1-1"] = true }))
        };
        var relocated = Prg1VanillaStreamRelocator.Compile(project, source, source.Bytes.ToArray());
        Assert.True(relocated.IsSuccess, Messages(relocated.Diagnostics));
        var original = new Prg1LayoutStreamId(source.Profile.Levels["W1-1"].LayoutOffset, document.Tileset);
        var destination = relocated.Value!.LayoutDestinations[original];
        Assert.NotEqual(original, destination);

        var built = new RomCompiler().Compile(project, source);

        Assert.True(built.IsSuccess, Messages(built.Diagnostics));
        const int levelFlagsOffset = 0x3FF3A;
        var expectedPointer = (ushort)(0xA000 + ((destination.FileOffset - source.PrgOffset) & 0x1FFF));
        var actualPointer = (ushort)(built.Value!.RomBytes[levelFlagsOffset] | (built.Value.RomBytes[levelFlagsOffset + 1] << 8));
        var originalPointer = (ushort)(0xA000 + ((original.FileOffset - source.PrgOffset) & 0x1FFF));
        Assert.Equal(expectedPointer, actualPointer);
        Assert.NotEqual(originalPointer, actualPointer);
        Assert.Equal(0x01, built.Value.RomBytes[levelFlagsOffset + 2]);
    }

    [Fact]
    public void OptionalAuthenticatedPrg1CapacityAnalysisMatchesCompilerAllocation()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;
        var compiler = new RomCompiler();
        var document = GrowLayout(RequireDocument(source, "W1-1"), 1);
        var project = ProjectDocumentV2.Create(source).WithArea(document);

        var capacity = compiler.AnalyzeVanillaCapacity(project, source).Find("W1-1");
        var built = compiler.Compile(project, source);

        Assert.NotNull(capacity);
        Assert.True(capacity.Layout.RequiresRelocation);
        Assert.True(capacity.Layout.Fits);
        Assert.True(capacity.Layout.SharedPoolUsed >= capacity.Layout.Used);
        Assert.True(built.IsSuccess, Messages(built.Diagnostics));
    }

    [Fact]
    public void OptionalAuthenticatedPrg1CapacityAnalysisAndCompilerRejectSameOverflow()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;
        var compiler = new RomCompiler();
        var project = ProjectDocumentV2.Create(source).WithArea(GrowLayout(RequireDocument(source, "W1-1"), 3000));

        var report = compiler.AnalyzeVanillaCapacity(project, source);
        var capacity = report.Find("W1-1");
        var built = compiler.Compile(project, source);

        Assert.NotNull(capacity);
        Assert.False(capacity.Layout.Fits);
        Assert.True(capacity.Layout.Used > capacity.Layout.MaximumStreamLength);
        Assert.Contains(report.Diagnostics, static item => item.Code == "RELOC_LAYOUT_FULL");
        Assert.False(built.IsSuccess);
        Assert.Contains(built.Diagnostics, static item => item.Code == "RELOC_LAYOUT_FULL");
    }

    [Fact]
    public void OptionalAuthenticatedPrg1CapacityUsesTheAuthenticatedCompactedRegion()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;
        var document = RequireDocument(source, "W1-1");
        var report = new RomCompiler().AnalyzeVanillaCapacity(ProjectDocumentV2.Create(source).WithArea(document), source);
        var capacity = report.Find("W1-1");

        Assert.NotNull(capacity);
        Assert.True(capacity.Layout.MaximumStreamLength > document.OriginalLayoutLength);
        Assert.True(capacity.Layout.SharedPoolCapacity > 62);
    }

    private static LevelDocument GrowLayout(LevelDocument document, int count)
    {
        var template = document.Elements.First(static item => item.Kind != LevelElementKind.Junction);
        var elements = document.Elements.ToList();
        for (var index = 0; index < count; index++) elements.Add(template with { Index = elements.Count });
        return document with { Elements = elements };
    }

    private static int GrowEncodedLayoutLength(LevelDocument document, int count)
    {
        var encoded = Smb3LevelCodec.EncodeLayout(GrowLayout(document, count));
        Assert.True(encoded.IsSuccess, Messages(encoded.Diagnostics));
        return encoded.Value!.Length;
    }

    private static LevelDocument GrowEnemies(LevelDocument document, int count)
    {
        var template = document.Enemies[0];
        var enemies = document.Enemies.ToList();
        for (var index = 0; index < count; index++) enemies.Add(template with { Index = enemies.Count });
        return document with { Enemies = enemies };
    }

    private static LevelDocument RequireDocument(RomImage source, string areaId)
    {
        var decoded = Smb3LevelCodec.Decode(source, source.Profile.Levels[areaId]);
        Assert.True(decoded.IsSuccess, Messages(decoded.Diagnostics));
        return decoded.Value!;
    }

    private static Prg1ReferenceIndex RequireGraph(RomImage source, byte[]? bytes = null)
    {
        var graph = bytes is null
            ? Prg1ReferenceIndexBuilder.Build(source)
            : Prg1ReferenceIndexBuilder.BuildCurrent(source, bytes);
        Assert.True(graph.IsSuccess, Messages(graph.Diagnostics));
        return graph.Value!;
    }

    private static RomImage? LoadOptionalPrg1()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess, Messages(loaded.Diagnostics));
        return loaded.Value!.Profile.Id == "us-prg1" ? loaded.Value : null;
    }

    private static string Messages(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(static item => $"{item.Code}: {item.Message}"));
}
