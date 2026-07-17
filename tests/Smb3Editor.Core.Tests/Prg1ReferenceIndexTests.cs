namespace Smb3Editor.Core.Tests;

public sealed class Prg1ReferenceIndexTests
{
    [Fact]
    public void NonPrg1RomIsRejectedWithoutParsing()
    {
        var bytes = new byte[16 + 262_144 + 131_072];
        bytes[0] = (byte)'N';
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'S';
        bytes[3] = 0x1A;
        bytes[4] = 16;
        bytes[5] = 16;
        var profile = Smb3Profiles.FindById("us-prg0")!;
        var source = RomImage.CreateForTesting("prg0.nes", bytes, profile);

        var result = Prg1ReferenceIndexBuilder.Build(source);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "PRG1_INDEX_PROFILE");
    }

    [Fact]
    public void OptionalAuthenticatedPrg1RomBuildsCompleteDeterministicGraph()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;

        var first = Prg1ReferenceIndexBuilder.Build(source);
        var second = Prg1ReferenceIndexBuilder.Build(source);
        Assert.True(first.IsSuccess, string.Join(Environment.NewLine, first.Diagnostics));
        Assert.True(second.IsSuccess, string.Join(Environment.NewLine, second.Diagnostics));

        var graph = first.Value!;
        Assert.Equal(380, graph.Roots.Length);
        Assert.Equal(340, graph.Roots.Count(static root => root.Kind == Prg1RootKind.Overworld));
        Assert.All(Enum.GetValues<Prg1RootKind>().Where(static kind => kind != Prg1RootKind.Overworld),
            kind => Assert.Equal(8, graph.Roots.Count(root => root.Kind == kind)));
        Assert.Equal(224, graph.Layouts.Length);
        Assert.Equal(202, graph.Enemies.Length);
        Assert.Equal(131, graph.JumpReferences.Length);
        Assert.Equal(511, graph.LayoutPointerSiteCount);
        Assert.Equal(462, graph.EnemyPointerSiteCount);
        Assert.Equal(49, graph.ConfigurationReferences.Length);
        Assert.Equal(223, graph.Layouts.Count(static stream => stream.IsRelocatable));
        Assert.Single(graph.Layouts, static stream => !stream.IsRelocatable);
        Assert.All(graph.Enemies, static stream => Assert.True(stream.IsRelocatable));
        Assert.Equal(41, graph.Roots.Count(static root =>
            root.Kind == Prg1RootKind.Overworld && root.SecondaryDataKind == Prg1RootSecondaryDataKind.ItemOrConfigurationValue));
        Assert.Equal(8, graph.Roots.Count(static root => root.Kind == Prg1RootKind.ToadOrWarpRoom &&
            root.SecondaryDataKind == Prg1RootSecondaryDataKind.ItemOrConfigurationValue));

        Assert.Equal(Fingerprint(graph), Fingerprint(second.Value!));
    }

    [Fact]
    public void OptionalAuthenticatedPrg1GraphHasBoundedTerminatedStreamsAndTranslatedHeaderSites()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;
        var built = Prg1ReferenceIndexBuilder.Build(source);
        Assert.True(built.IsSuccess, string.Join(Environment.NewLine, built.Diagnostics));
        var graph = built.Value!;

        Assert.All(graph.Layouts, stream =>
        {
            Assert.InRange(stream.Id.ObjectSet, 1, 15);
            Assert.True(stream.Length >= 10);
            Assert.Equal(0xFF, source.Bytes[stream.Id.FileOffset + stream.Length - 1]);
            Assert.All(stream.PointerSites, site => Assert.InRange(site.FileOffset, 16, source.Bytes.Length - 2));
        });
        Assert.All(graph.Enemies, stream =>
        {
            Assert.True(stream.Length >= 2);
            Assert.InRange(stream.Id.FileOffset, 16 + (6 * 0x2000), 16 + (7 * 0x2000) - 2);
            Assert.Equal(0xFF, source.Bytes[stream.Id.FileOffset + stream.Length - 1]);
            Assert.All(stream.PointerSites, site => Assert.InRange(site.FileOffset, 16, source.Bytes.Length - 2));
        });
        Assert.All(graph.JumpReferences, jump =>
        {
            Assert.Equal(jump.SourceLayout, jump.LayoutPointerSite.ContainingLayout);
            Assert.Equal(jump.SourceLayout, jump.EnemyPointerSite.ContainingLayout);
            Assert.Equal(0, jump.LayoutPointerSite.RelativeOffset);
            Assert.Equal(2, jump.EnemyPointerSite.RelativeOffset);
            Assert.Equal(jump.SourceLayout.FileOffset, jump.LayoutPointerSite.FileOffset);
            Assert.Equal(jump.SourceLayout.FileOffset + 2, jump.EnemyPointerSite.FileOffset);
        });
    }

    [Fact]
    public void OptionalAuthenticatedPrg1EnemyBankHasDisjointStreamsAndVerifiedTrailingPadding()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;
        var built = Prg1ReferenceIndexBuilder.Build(source);
        Assert.True(built.IsSuccess, string.Join(Environment.NewLine, built.Diagnostics));

        var streams = built.Value!.Enemies.OrderBy(static stream => stream.Id.FileOffset).ToArray();
        Assert.Equal(streams.Length, streams.Select(static stream => stream.Id).Distinct().Count());
        for (var index = 1; index < streams.Length; index++)
        {
            var previousEnd = streams[index - 1].Id.FileOffset + streams[index - 1].Length;
            Assert.True(previousEnd <= streams[index].Id.FileOffset,
                $"PRG6 sprite streams overlap at ${streams[index - 1].Id.FileOffset:X5} and ${streams[index].Id.FileOffset:X5}.");
        }

        var usedEnd = streams.Max(static stream => stream.Id.FileOffset + stream.Length);
        Assert.Equal(0x0DA44, usedEnd);
        Assert.All(source.Bytes.AsSpan(0x0DA75, 0x0E010 - 0x0DA75).ToArray(), static value => Assert.Equal(0xFF, value));
        Assert.All(streams.SelectMany(static stream => stream.PointerSites)
                .Where(static site => site.Origin == Prg1PointerSiteOrigin.LayoutHeader),
            static site =>
            {
                Assert.NotNull(site.ContainingLayout);
                Assert.Equal(2, site.RelativeOffset);
                Assert.Equal(site.ContainingLayout.Value.FileOffset + 2, site.FileOffset);
            });
    }



    [Fact]
    public void OptionalAuthenticatedPrg1CurrentSnapshotRebuildsRelocatedOverworldRootSites()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;
        var clean = Prg1ReferenceIndexBuilder.Build(source);
        Assert.True(clean.IsSuccess, string.Join(Environment.NewLine, clean.Diagnostics));

        var maps = Smb3OverworldParser.Parse(source);
        Assert.True(maps.IsSuccess, string.Join(Environment.NewLine, maps.Diagnostics));
        var world = maps.Value![0];
        var project = ProjectDocumentV2.Create(source).WithOverworldNodes(
            world with { LevelPointers = world.LevelPointers.Skip(1).ToArray() });
        var currentBytes = source.Bytes.ToArray();
        var diagnostics = Smb3OverworldSerializer.ApplyNodeSetOverrides(project, source, currentBytes);
        Assert.DoesNotContain(diagnostics, static item => item.Severity == DiagnosticSeverity.Error);

        var rebuilt = Prg1ReferenceIndexBuilder.BuildCurrent(source, currentBytes);
        Assert.True(rebuilt.IsSuccess, string.Join(Environment.NewLine, rebuilt.Diagnostics));
        Assert.Equal(379, rebuilt.Value!.Roots.Length);
        var cleanWorld2 = clean.Value!.Roots.Single(static root =>
            root.Kind == Prg1RootKind.Overworld && root.World == 2 && root.Index == 0);
        var rebuiltWorld2 = rebuilt.Value.Roots.Single(static root =>
            root.Kind == Prg1RootKind.Overworld && root.World == 2 && root.Index == 0);
        Assert.NotEqual(cleanWorld2.LayoutPointerSite.FileOffset, rebuiltWorld2.LayoutPointerSite.FileOffset);
        Assert.Equal(cleanWorld2.Layout, rebuiltWorld2.Layout);
    }

    [Fact]
    public void OptionalAuthenticatedPrg1SourceMutationIsRejected()
    {
        var source = LoadOptionalPrg1();
        if (source is null) return;
        source.Bytes[^1] ^= 0x01;

        var result = Prg1ReferenceIndexBuilder.Build(source);

        Assert.False(result.IsSuccess);
        Assert.Contains(result.Diagnostics, static item => item.Code == "PRG1_INDEX_PROFILE");
    }

    private static RomImage? LoadOptionalPrg1()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
        return loaded.Value!.Profile.Id == "us-prg1" ? loaded.Value : null;
    }

    private static string Fingerprint(Prg1ReferenceIndex graph) => string.Join("\n",
        graph.Roots.Select(root => $"R:{root.Ordinal}:{root.Kind}:{root.World}:{root.Index}:{root.ObjectSet}:{root.Layout.FileOffset:X}:{root.Enemy?.FileOffset:X}:{root.SecondaryDataKind}:{root.LayoutPointerSite.FileOffset:X}:{root.EnemyPointerSite?.FileOffset:X}:{root.ConfigurationReference?.Value:X}")
            .Concat(graph.Layouts.SelectMany(stream => stream.PointerSites.Select(site => $"L:{stream.Id.ObjectSet}:{stream.Id.FileOffset:X}:{stream.Length}:{stream.StorageKind}:{site.FileOffset:X}:{site.Origin}:{site.ContainingLayout?.FileOffset:X}:{site.RelativeOffset}")))
            .Concat(graph.Enemies.SelectMany(stream => stream.PointerSites.Select(site => $"E:{stream.Id.FileOffset:X}:{stream.Length}:{site.FileOffset:X}:{site.Origin}:{site.ContainingLayout?.FileOffset:X}:{site.RelativeOffset}")))
            .Concat(graph.JumpReferences.Select(jump => $"J:{jump.SourceLayout.ObjectSet}:{jump.SourceLayout.FileOffset:X}:{jump.TargetLayout.ObjectSet}:{jump.TargetLayout.FileOffset:X}:{jump.TargetEnemy.FileOffset:X}"))
            .Concat(graph.ConfigurationReferences.Select(item => $"C:{item.Kind}:{item.World}:{item.Index}:{item.Value:X}:{item.PointerSite.FileOffset:X}")));
}
