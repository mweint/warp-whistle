using System.Collections.Immutable;

namespace Smb3Editor.Core;

internal sealed record Prg1RelocationBuild(
    byte[] RomBytes,
    ImmutableDictionary<Prg1LayoutStreamId, Prg1LayoutStreamId> LayoutDestinations,
    ImmutableDictionary<Prg1EnemyStreamId, Prg1EnemyStreamId> EnemyDestinations,
    Prg1VanillaCapacityReport Capacity);

public sealed record Prg1StreamCapacity(
    int Used,
    int OriginalCapacity,
    int MaximumStreamLength,
    int SharedPoolUsed,
    int SharedPoolCapacity,
    bool RequiresRelocation,
    bool Fits);

public sealed record Prg1AreaCapacity(
    string AreaId,
    string DisplayName,
    int LayoutBank,
    Prg1StreamCapacity Layout,
    Prg1StreamCapacity Sprites);

public sealed record Prg1VanillaCapacityReport(
    IReadOnlyDictionary<string, Prg1AreaCapacity> Areas,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public Prg1AreaCapacity? Find(string areaId) => Areas.GetValueOrDefault(areaId);
}

/// <summary>
/// Compiles editor-owned PRG1 streams in place when they fit and deterministically
/// compacts a legal stream bank when one of its streams outgrows its original slot.
/// It never crosses banks, expands the ROM, moves special streams, or accepts a
/// partial reference graph.
/// </summary>
internal static class Prg1VanillaStreamRelocator
{
    private const int HeaderSize = 16;
    private const int BankSize = 0x2000;
    private const int EnemyBank = 6;

    private static readonly int[] LayoutBankByObjectSet =
        [11, 15, 21, 16, 17, 19, 18, 18, 18, 20, 23, 19, 17, 19, 13, 26, 26, 26, 9];

    // Southbird's byte-exact PRG1 disassembly identifies each range below as the
    // bank's trailing level include followed only by empty/unused bytes. These are
    // the complete writable layout extents; PRG11/26/9 remain excluded.
    private static readonly ImmutableDictionary<int, (int Start, int EndExclusive)> LayoutRegionManifest =
        new Dictionary<int, (int, int)>
        {
            [13] = (0x1A587, 0x1C010), [15] = (0x1E509, 0x20010), [16] = (0x20587, 0x22010),
            [17] = (0x227E0, 0x24010), [18] = (0x24BA7, 0x26010), [19] = (0x26A6F, 0x28010),
            [20] = (0x28F36, 0x2A010), [21] = (0x2A7F7, 0x2C010), [23] = (0x2EC07, 0x30010)
        }.ToImmutableDictionary();
    private static readonly (int Start, int EndExclusive) EnemyRegionManifest = (0x0C012, 0x0E010);

    public static OperationResult<Prg1RelocationBuild> Compile(
        ProjectDocumentV2 project,
        RomImage source,
        byte[] currentBytes)
    {
        try
        {
            return CompileCore(project, source, currentBytes);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return OperationResult<Prg1RelocationBuild>.Failure(Diagnostics.Error(
                "RELOC_INVALID",
                $"The PRG1 relocation transaction was rejected safely: {ex.Message}"));
        }
    }

    public static Prg1VanillaCapacityReport Analyze(
        ProjectDocumentV2 project,
        RomImage source,
        byte[] currentBytes)
    {
        var result = Compile(project, source, currentBytes);
        return result.Value?.Capacity ?? new Prg1VanillaCapacityReport(
            new Dictionary<string, Prg1AreaCapacity>(), result.Diagnostics);
    }

    private static OperationResult<Prg1RelocationBuild> CompileCore(
        ProjectDocumentV2 project,
        RomImage source,
        byte[] currentBytes)
    {
        var diagnostics = new List<Diagnostic>();
        var beforeResult = Prg1ReferenceIndexBuilder.BuildCurrent(source, currentBytes);
        diagnostics.AddRange(beforeResult.Diagnostics);
        if (!beforeResult.IsSuccess)
            return OperationResult<Prg1RelocationBuild>.Failure(diagnostics.ToArray());

        var before = beforeResult.Value!;
        var authenticatedResult = Prg1ReferenceIndexBuilder.Build(source);
        diagnostics.AddRange(authenticatedResult.Diagnostics);
        if (!authenticatedResult.IsSuccess)
            return OperationResult<Prg1RelocationBuild>.Failure(diagnostics.ToArray());
        var authenticated = authenticatedResult.Value!;
        var layouts = new Dictionary<Prg1LayoutStreamId, StreamMutation<Prg1LayoutStreamId>>();
        var enemies = new Dictionary<Prg1EnemyStreamId, StreamMutation<Prg1EnemyStreamId>>();

        foreach (var pair in project.ModifiedAreas.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (!source.Profile.Levels.TryGetValue(pair.Key, out var location))
            {
                diagnostics.Add(Diagnostics.Error("BUILD_AREA", $"Area '{pair.Key}' is not present in the source profile."));
                continue;
            }

            var encodedLayout = Smb3LevelCodec.EncodeLayout(pair.Value);
            var encodedEnemies = Smb3LevelCodec.EncodeEnemies(pair.Value);
            diagnostics.AddRange(encodedLayout.Diagnostics);
            diagnostics.AddRange(encodedEnemies.Diagnostics);
            if (!encodedLayout.IsSuccess || !encodedEnemies.IsSuccess) continue;

            var layoutId = new Prg1LayoutStreamId(location.LayoutOffset, location.Tileset);
            var enemyId = new Prg1EnemyStreamId(location.EnemyOffset);
            var graphLayout = before.Layouts.SingleOrDefault(item => item.Id == layoutId);
            var graphEnemy = before.Enemies.SingleOrDefault(item => item.Id == enemyId);
            if (graphLayout is null || !graphLayout.IsRelocatable)
            {
                diagnostics.Add(Diagnostics.Error("RELOC_LAYOUT_GRAPH",
                    $"{pair.Value.DisplayName}'s layout is not a relocatable stream in the current PRG1 reference graph."));
                continue;
            }
            if (graphEnemy is null || !graphEnemy.IsRelocatable)
            {
                diagnostics.Add(Diagnostics.Error("RELOC_ENEMY_GRAPH",
                    $"{pair.Value.DisplayName}'s sprite stream is not a true PRG6 stream in the current PRG1 reference graph."));
                continue;
            }
            if (pair.Value.OriginalLayoutLength != graphLayout.Length || pair.Value.OriginalEnemyLength != graphEnemy.Length)
            {
                diagnostics.Add(Diagnostics.Error("RELOC_SLOT_MISMATCH",
                    $"{pair.Value.DisplayName}'s saved source lengths do not match the current authenticated stream graph."));
                continue;
            }
            if (!encodedLayout.Value!.AsSpan(0, 4).SequenceEqual(currentBytes.AsSpan(layoutId.FileOffset, 4)))
            {
                diagnostics.Add(Diagnostics.Error("RELOC_HEADER_POINTER",
                    $"{pair.Value.DisplayName} changes raw alternate-area pointers; symbolic jump editing is required before that can be compiled safely."));
                continue;
            }

            AddMutation(layouts, layoutId, encodedLayout.Value!, graphLayout.Length, pair.Value.DisplayName, diagnostics, "layout");
            AddMutation(enemies, enemyId, encodedEnemies.Value!, graphEnemy.Length, pair.Value.DisplayName, diagnostics, "sprite");
        }

        if (diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error))
            return OperationResult<Prg1RelocationBuild>.Failure(diagnostics.ToArray());

        // Snapshot every stream before assigning destinations. A compacted output
        // can overlap source ranges, so no payload may be read from the output while
        // the transaction is in progress.
        var layoutPayloads = before.Layouts
            .Where(static stream => stream.IsRelocatable)
            .ToDictionary(
                static stream => stream.Id,
                stream => layouts.TryGetValue(stream.Id, out var mutation)
                    ? mutation.Bytes.ToArray()
                    : currentBytes.AsSpan(stream.Id.FileOffset, stream.Length).ToArray());
        var enemyPayloads = before.Enemies
            .Where(static stream => stream.IsRelocatable)
            .ToDictionary(
                static stream => stream.Id,
                stream => enemies.TryGetValue(stream.Id, out var mutation)
                    ? mutation.Bytes.ToArray()
                    : currentBytes.AsSpan(stream.Id.FileOffset, stream.Length).ToArray());

        var layoutDestinations = before.Layouts.ToDictionary(static stream => stream.Id, static stream => stream.Id);
        var enemyDestinations = before.Enemies.ToDictionary(static stream => stream.Id, static stream => stream.Id);
        var pinned = FindPinnedTargets(before);
        var layoutRegions = BuildLayoutRegions(authenticated, before, layoutPayloads, pinned.Layouts, diagnostics);
        var enemyRegion = BuildEnemyRegion(authenticated, before, enemyPayloads, pinned.Enemies, diagnostics);
        var compactLayoutBanks = layouts.Values.Where(static mutation => mutation.RequiresRelocation)
            .Select(mutation => LayoutBank(mutation.Id.ObjectSet)).ToHashSet();
        var compactEnemies = enemies.Values.Any(static mutation => mutation.RequiresRelocation);

        foreach (var bank in compactLayoutBanks.Order())
        {
            if (!layoutRegions.TryGetValue(bank, out var region))
            {
                diagnostics.Add(Diagnostics.Error("RELOC_LAYOUT_BANK",
                    $"PRG{bank} has no authenticated relocatable layout region."));
                continue;
            }
            var blocked = layouts.Values.FirstOrDefault(mutation => mutation.RequiresRelocation &&
                LayoutBank(mutation.Id.ObjectSet) == bank && region.PinnedLayouts.Contains(mutation.Id));
            if (blocked is not null)
            {
                diagnostics.Add(Diagnostics.Error("RELOC_LAYOUT_OVERLAP",
                    $"{blocked.DisplayNames} cannot grow safely because one of its header references shares bytes with another stock layout interpretation."));
                continue;
            }
            if (region.HasPhysicalOverlaps)
            {
                diagnostics.Add(Diagnostics.Error("RELOC_LAYOUT_OVERLAP",
                    $"PRG{bank} contains shared physical layout components; compacting this bank remains disabled until those components can be detached without changing either interpretation."));
                continue;
            }
            if (!CanAssignLayouts(region, layoutPayloads))
            {
                diagnostics.Add(Diagnostics.Error("RELOC_LAYOUT_FULL",
                    $"PRG{bank}'s compacted layout data needs {region.Used} bytes but its legal region has {region.Capacity}."));
                continue;
            }
            AssignLayouts(region, layoutPayloads, layoutDestinations);
            if (region.LayoutStreams.Any(stream =>
                    layoutDestinations[stream.Id].FileOffset + layoutPayloads[stream.Id].Length > region.EndExclusive))
            {
                diagnostics.Add(Diagnostics.Error("RELOC_LAYOUT_FULL",
                    $"PRG{bank}'s fixed shared layouts leave no contiguous arrangement for the requested compacted data."));
            }
        }

        if (compactEnemies)
        {
            var blocked = enemies.Values.FirstOrDefault(mutation => mutation.RequiresRelocation &&
                enemyRegion?.PinnedEnemies.Contains(mutation.Id) == true);
            if (blocked is not null)
            {
                diagnostics.Add(Diagnostics.Error("RELOC_ENEMY_OVERLAP",
                    $"{blocked.DisplayNames} cannot grow safely because its header pointer shares bytes with another stock layout interpretation."));
            }
            else if (enemyRegion?.HasPhysicalOverlaps == true)
            {
                diagnostics.Add(Diagnostics.Error("RELOC_ENEMY_OVERLAP",
                    "PRG6 contains shared physical sprite components; compacting it is disabled until those components can be detached safely."));
            }
            else if (enemyRegion is null || !CanAssignEnemies(enemyRegion, enemyPayloads))
            {
                var used = enemyRegion?.Used ?? 0;
                var capacityBytes = enemyRegion?.Capacity ?? 0;
                diagnostics.Add(Diagnostics.Error("RELOC_ENEMY_FULL",
                    $"PRG6's compacted sprite data needs {used} bytes but its legal region has {capacityBytes}."));
            }
            else
            {
                AssignEnemies(enemyRegion, enemyPayloads, enemyDestinations);
                if (enemyRegion.EnemyStreams.Any(stream =>
                        enemyDestinations[stream.Id].FileOffset + enemyPayloads[stream.Id].Length > enemyRegion.EndExclusive))
                {
                    diagnostics.Add(Diagnostics.Error("RELOC_ENEMY_FULL",
                        "PRG6's fixed shared headers leave no contiguous arrangement for the requested compacted sprite data."));
                }
            }
        }

        var capacity = BuildCapacityReport(project, source, layouts, enemies, layoutPayloads, enemyPayloads,
            layoutRegions, enemyRegion, diagnostics);

        if (diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error))
            return OperationResult<Prg1RelocationBuild>.FailureWithValue(
                new Prg1RelocationBuild(
                    currentBytes,
                    layoutDestinations.ToImmutableDictionary(),
                    enemyDestinations.ToImmutableDictionary(),
                    capacity),
                diagnostics);

        // Transactional from here onward: no caller-visible bytes change unless all
        // writes and the rebuilt graph verify successfully.
        var output = currentBytes.ToArray();
        foreach (var payload in layoutPayloads.OrderBy(static item => item.Key.ObjectSet).ThenBy(static item => item.Key.FileOffset))
        {
            if (compactLayoutBanks.Contains(LayoutBank(payload.Key.ObjectSet)) || layouts.ContainsKey(payload.Key))
                payload.Value.CopyTo(output, layoutDestinations[payload.Key].FileOffset);
        }
        foreach (var payload in enemyPayloads.OrderBy(static item => item.Key.FileOffset))
        {
            if (compactEnemies || enemies.ContainsKey(payload.Key))
                payload.Value.CopyTo(output, enemyDestinations[payload.Key].FileOffset);
        }

        var pointerWrites = new Dictionary<int, ushort>();
        foreach (var stream in before.Layouts)
        {
            if (!layoutDestinations.TryGetValue(stream.Id, out var destination) || destination == stream.Id) continue;
            var word = LayoutCpuPointer(destination);
            foreach (var site in stream.PointerSites)
                AddPointerWrite(pointerWrites, TranslateSite(site, layoutDestinations), word, diagnostics);
        }
        foreach (var stream in before.Enemies)
        {
            if (!enemyDestinations.TryGetValue(stream.Id, out var destination) || destination == stream.Id) continue;
            var word = EnemyCpuPointer(destination);
            foreach (var site in stream.PointerSites)
                AddPointerWrite(pointerWrites, TranslateSite(site, layoutDestinations), word, diagnostics);
        }
        if (diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error))
            return OperationResult<Prg1RelocationBuild>.Failure(diagnostics.ToArray());
        foreach (var write in pointerWrites.OrderBy(static item => item.Key))
            WriteWord(output, write.Key, write.Value);

        foreach (var payload in layoutPayloads)
        {
            var destination = layoutDestinations[payload.Key].FileOffset;
            if (output[destination + payload.Value.Length - 1] != 0xFF)
            {
                diagnostics.Add(Diagnostics.Error("RELOC_LAYOUT_OVERWRITE",
                    $"Compaction overwrote the terminator of layout payload {payload.Key}."));
            }
            for (var index = 4; index < payload.Value.Length; index++)
            {
                if (output[destination + index] == payload.Value[index]) continue;
                diagnostics.Add(Diagnostics.Error("RELOC_LAYOUT_OVERWRITE",
                    $"Compaction changed layout payload {payload.Key} at relative byte {index}."));
                break;
            }
        }
        if (diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error))
            return OperationResult<Prg1RelocationBuild>.Failure(diagnostics.ToArray());

        var afterResult = Prg1ReferenceIndexBuilder.BuildCurrent(source, output);
        diagnostics.AddRange(afterResult.Diagnostics);
        if (!afterResult.IsSuccess)
            return OperationResult<Prg1RelocationBuild>.Failure(diagnostics.ToArray());
        if (!VerifyGraph(before, afterResult.Value!, layoutDestinations, enemyDestinations, layouts, enemies, diagnostics))
            return OperationResult<Prg1RelocationBuild>.Failure(diagnostics.ToArray());

        foreach (var mutation in layouts.Values.OrderBy(static item => item.Id.FileOffset))
        {
            var destination = layoutDestinations[mutation.Id];
            diagnostics.Add(Diagnostics.Info("BUILD_AREA_OK",
                $"Compiled {mutation.DisplayNames}: {mutation.Bytes.Length}/{mutation.OriginalLength} layout bytes" +
                (destination == mutation.Id ? "." : $"; relocated within PRG{LayoutBank(mutation.Id.ObjectSet)}.")));
        }
        foreach (var mutation in enemies.Values.OrderBy(static item => item.Id.FileOffset))
        {
            var destination = enemyDestinations[mutation.Id];
            diagnostics.Add(Diagnostics.Info("BUILD_ENEMY_OK",
                $"Compiled {mutation.DisplayNames}: {mutation.Bytes.Length}/{mutation.OriginalLength} sprite bytes" +
                (destination == mutation.Id ? "." : "; relocated within PRG6.")));
        }

        return OperationResult<Prg1RelocationBuild>.Success(
            new Prg1RelocationBuild(
                output,
                layoutDestinations.ToImmutableDictionary(),
                enemyDestinations.ToImmutableDictionary(),
                capacity with { Diagnostics = diagnostics.ToArray() }),
            diagnostics);
    }

    private static Prg1VanillaCapacityReport BuildCapacityReport(
        ProjectDocumentV2 project,
        RomImage source,
        IReadOnlyDictionary<Prg1LayoutStreamId, StreamMutation<Prg1LayoutStreamId>> layouts,
        IReadOnlyDictionary<Prg1EnemyStreamId, StreamMutation<Prg1EnemyStreamId>> enemies,
        IReadOnlyDictionary<Prg1LayoutStreamId, byte[]> layoutPayloads,
        IReadOnlyDictionary<Prg1EnemyStreamId, byte[]> enemyPayloads,
        IReadOnlyDictionary<int, CompactRegion> layoutRegions,
        CompactRegion? enemyRegion,
        IReadOnlyList<Diagnostic> diagnostics)
    {
        var areas = new Dictionary<string, Prg1AreaCapacity>(StringComparer.Ordinal);
        foreach (var pair in project.ModifiedAreas.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (!source.Profile.Levels.TryGetValue(pair.Key, out var location)) continue;
            var layoutId = new Prg1LayoutStreamId(location.LayoutOffset, location.Tileset);
            var enemyId = new Prg1EnemyStreamId(location.EnemyOffset);
            if (!layouts.TryGetValue(layoutId, out var layout) || !enemies.TryGetValue(enemyId, out var enemy)) continue;

            var bank = LayoutBank(layoutId.ObjectSet);
            var hasLayoutRegion = layoutRegions.TryGetValue(bank, out var layoutRegion);
            var layoutPinned = hasLayoutRegion && (layoutRegion!.PinnedLayouts.Contains(layoutId) || layoutRegion.HasPhysicalOverlaps);
            var maximumLayoutLength = hasLayoutRegion && !layoutPinned
                ? MaximumLayoutLength(layoutRegion!, layoutId, layoutPayloads)
                : layout.OriginalLength;
            var layoutFits = !layout.RequiresRelocation || hasLayoutRegion && !layoutPinned &&
                CanAssignLayouts(layoutRegion!, layoutPayloads);
            var enemyPinned = enemyRegion?.PinnedEnemies.Contains(enemyId) == true || enemyRegion?.HasPhysicalOverlaps == true;
            var maximumEnemyLength = enemyRegion is null || enemyPinned
                ? enemy.OriginalLength
                : MaximumEnemyLength(enemyRegion, enemyId, enemyPayloads);
            var enemyFits = !enemy.RequiresRelocation || enemyRegion is not null && !enemyPinned &&
                CanAssignEnemies(enemyRegion, enemyPayloads);

            areas[pair.Key] = new Prg1AreaCapacity(
                pair.Key,
                pair.Value.DisplayName,
                bank,
                new Prg1StreamCapacity(
                    layout.Bytes.Length,
                    layout.OriginalLength,
                    Math.Max(layout.OriginalLength, maximumLayoutLength),
                    hasLayoutRegion ? layoutRegion!.Used : 0,
                    hasLayoutRegion ? layoutRegion!.Capacity : 0,
                    layout.RequiresRelocation,
                    layoutFits),
                new Prg1StreamCapacity(
                    enemy.Bytes.Length,
                    enemy.OriginalLength,
                    Math.Max(enemy.OriginalLength, maximumEnemyLength),
                    enemyRegion?.Used ?? 0,
                    enemyRegion?.Capacity ?? 0,
                    enemy.RequiresRelocation,
                    enemyFits));
        }
        return new Prg1VanillaCapacityReport(areas, diagnostics.ToArray());
    }

    private static ImmutableDictionary<int, CompactRegion> BuildLayoutRegions(
        Prg1ReferenceIndex authenticated,
        Prg1ReferenceIndex current,
        IReadOnlyDictionary<Prg1LayoutStreamId, byte[]> payloads,
        ImmutableHashSet<Prg1LayoutStreamId> pinned,
        List<Diagnostic> diagnostics)
    {
        var regions = new Dictionary<int, CompactRegion>();
        foreach (var group in current.Layouts.Where(static stream => stream.IsRelocatable)
                     .GroupBy(stream => LayoutBank(stream.Id.ObjectSet)).OrderBy(static group => group.Key))
        {
            var streams = group.OrderBy(static stream => stream.Id.FileOffset)
                .ThenBy(static stream => stream.Id.ObjectSet).ToImmutableArray();
            if (!LayoutRegionManifest.TryGetValue(group.Key, out var manifest))
            {
                diagnostics.Add(Diagnostics.Error("RELOC_LAYOUT_REGION",
                    $"PRG{group.Key} has no disassembly-backed writable layout manifest."));
                continue;
            }
            var bankStart = HeaderSize + group.Key * BankSize;
            var bankEnd = manifest.EndExclusive;
            var authenticatedStreams = authenticated.Layouts.Where(stream => stream.IsRelocatable && LayoutBank(stream.Id.ObjectSet) == group.Key).ToArray();
            if (authenticatedStreams.Length == 0)
            {
                diagnostics.Add(Diagnostics.Error("RELOC_LAYOUT_REGION",
                    $"PRG{group.Key} has no authenticated source layout boundary."));
                continue;
            }
            var authenticatedStart = authenticatedStreams.Min(static stream => stream.Id.FileOffset);
            var start = manifest.Start;
            var bankPinned = streams.Select(static stream => stream.Id).Where(pinned.Contains).ToImmutableHashSet();
            var occupied = MergeIntervals(streams.Where(stream => bankPinned.Contains(stream.Id))
                .Select(stream => (stream.Id.FileOffset, stream.Id.FileOffset + stream.Length)));
            var used = occupied.Sum(static interval => interval.End - interval.Start) +
                       streams.Where(stream => !bankPinned.Contains(stream.Id)).Sum(stream => payloads[stream.Id].Length);
            if (start < bankStart || start >= bankEnd || authenticatedStart != start ||
                streams.Any(stream => stream.Id.FileOffset < start || stream.Id.FileOffset + stream.Length > bankEnd))
            {
                diagnostics.Add(Diagnostics.Error("RELOC_LAYOUT_REGION",
                    $"PRG{group.Key}'s relocatable layout region starts outside its legal bank."));
                continue;
            }

            // An identical physical offset interpreted under different object sets
            // is intentionally retained as distinct logical data. Object-set-specific
            // command lengths/semantics make collapsing it unsafe without proof.
            foreach (var overlap in streams.GroupBy(static stream => stream.Id.FileOffset).Where(static set => set.Count() > 1))
            {
                diagnostics.Add(Diagnostics.Info("RELOC_LAYOUT_IDENTITY",
                    $"PRG{group.Key} offset ${overlap.Key:X5} is referenced under multiple object sets and will be packed as separate logical streams."));
            }
            var hasOverlaps = MergeIntervals(streams.Select(stream =>
                    (stream.Id.FileOffset, stream.Id.FileOffset + stream.Length)))
                .Sum(static interval => interval.End - interval.Start) < streams.Sum(static stream => stream.Length);
            regions[group.Key] = new CompactRegion(group.Key, start, bankEnd, used, streams, [], bankPinned, [], hasOverlaps);
        }
        return regions.ToImmutableDictionary();
    }

    private static CompactRegion? BuildEnemyRegion(
        Prg1ReferenceIndex authenticated,
        Prg1ReferenceIndex current,
        IReadOnlyDictionary<Prg1EnemyStreamId, byte[]> payloads,
        ImmutableHashSet<Prg1EnemyStreamId> pinned,
        List<Diagnostic> diagnostics)
    {
        var streams = current.Enemies.Where(static stream => stream.IsRelocatable)
            .OrderBy(static stream => stream.Id.FileOffset).ToImmutableArray();
        if (streams.IsEmpty) return null;
        var bankStart = HeaderSize + EnemyBank * BankSize;
        var bankEnd = EnemyRegionManifest.EndExclusive;
        var authenticatedStreams = authenticated.Enemies.Where(static stream => stream.IsRelocatable).ToArray();
        if (authenticatedStreams.Length == 0)
        {
            diagnostics.Add(Diagnostics.Error("RELOC_ENEMY_REGION", "PRG6 has no authenticated source sprite boundary."));
            return null;
        }
        var authenticatedStart = authenticatedStreams.Min(static stream => stream.Id.FileOffset);
        var start = EnemyRegionManifest.Start;
        if (start < bankStart || start >= bankEnd || authenticatedStart != start ||
            streams.Any(stream => stream.Id.FileOffset < start || stream.Id.FileOffset + stream.Length > bankEnd))
        {
            diagnostics.Add(Diagnostics.Error("RELOC_ENEMY_REGION", "PRG6's sprite region starts outside its legal bank."));
            return null;
        }
        var bankPinned = streams.Select(static stream => stream.Id).Where(pinned.Contains).ToImmutableHashSet();
        var occupied = MergeIntervals(streams.Where(stream => bankPinned.Contains(stream.Id))
            .Select(stream => (stream.Id.FileOffset, stream.Id.FileOffset + stream.Length)));
        var used = occupied.Sum(static interval => interval.End - interval.Start) +
                   streams.Where(stream => !bankPinned.Contains(stream.Id)).Sum(stream => payloads[stream.Id].Length);
        var hasOverlaps = MergeIntervals(streams.Select(stream =>
                (stream.Id.FileOffset, stream.Id.FileOffset + stream.Length)))
            .Sum(static interval => interval.End - interval.Start) < streams.Sum(static stream => stream.Length);
        return new CompactRegion(EnemyBank, start, bankEnd, used, [], streams, [], bankPinned, hasOverlaps);
    }

    private static (ImmutableHashSet<Prg1LayoutStreamId> Layouts, ImmutableHashSet<Prg1EnemyStreamId> Enemies)
        FindPinnedTargets(Prg1ReferenceIndex graph)
    {
        var intervals = graph.Layouts.Where(static stream => stream.IsRelocatable).ToArray();
        bool SharedByAnotherLayout(Prg1PointerSite site) => site.ContainingLayout is { } containing &&
            intervals.Any(stream => stream.Id != containing && site.FileOffset < stream.Id.FileOffset + stream.Length &&
                                    site.FileOffset + 2 > stream.Id.FileOffset);
        return (
            graph.Layouts.Where(stream => stream.PointerSites.Any(site =>
                    site.Origin == Prg1PointerSiteOrigin.LayoutHeader && SharedByAnotherLayout(site)))
                .Select(static stream => stream.Id).ToImmutableHashSet(),
            graph.Enemies.Where(stream => stream.PointerSites.Any(site =>
                    site.Origin == Prg1PointerSiteOrigin.LayoutHeader && SharedByAnotherLayout(site)))
                .Select(static stream => stream.Id).ToImmutableHashSet());
    }

    private static void AssignLayouts(
        CompactRegion region,
        IReadOnlyDictionary<Prg1LayoutStreamId, byte[]> payloads,
        Dictionary<Prg1LayoutStreamId, Prg1LayoutStreamId> destinations)
    {
        var occupied = MergeIntervals(region.LayoutStreams.Where(stream => region.PinnedLayouts.Contains(stream.Id))
            .Select(stream => (stream.Id.FileOffset, stream.Id.FileOffset + stream.Length))).ToList();
        var cursor = region.Start;
        foreach (var stream in region.LayoutStreams.Where(stream => !region.PinnedLayouts.Contains(stream.Id)))
        {
            cursor = FindSpace(cursor, payloads[stream.Id].Length, occupied);
            destinations[stream.Id] = new Prg1LayoutStreamId(cursor, stream.Id.ObjectSet);
            occupied.Add((cursor, cursor + payloads[stream.Id].Length));
            occupied = MergeIntervals(occupied).ToList();
            cursor += payloads[stream.Id].Length;
        }
    }

    private static bool CanAssignLayouts(
        CompactRegion region,
        IReadOnlyDictionary<Prg1LayoutStreamId, byte[]> payloads,
        Prg1LayoutStreamId? overrideId = null,
        int overrideLength = 0)
    {
        var occupied = MergeIntervals(region.LayoutStreams.Where(stream => region.PinnedLayouts.Contains(stream.Id))
            .Select(stream => (stream.Id.FileOffset, stream.Id.FileOffset + stream.Length))).ToList();
        var cursor = region.Start;
        foreach (var stream in region.LayoutStreams.Where(stream => !region.PinnedLayouts.Contains(stream.Id)))
        {
            var length = overrideId is { } id && stream.Id == id ? overrideLength : payloads[stream.Id].Length;
            cursor = FindSpace(cursor, length, occupied);
            if (cursor + length > region.EndExclusive) return false;
            occupied.Add((cursor, cursor + length));
            occupied = MergeIntervals(occupied).ToList();
            cursor += length;
        }
        return true;
    }

    private static int MaximumLayoutLength(
        CompactRegion region,
        Prg1LayoutStreamId id,
        IReadOnlyDictionary<Prg1LayoutStreamId, byte[]> payloads)
    {
        var low = 0;
        var high = region.Capacity;
        while (low < high)
        {
            var candidate = low + (high - low + 1) / 2;
            if (CanAssignLayouts(region, payloads, id, candidate)) low = candidate;
            else high = candidate - 1;
        }
        return low;
    }

    private static void AssignEnemies(
        CompactRegion region,
        IReadOnlyDictionary<Prg1EnemyStreamId, byte[]> payloads,
        Dictionary<Prg1EnemyStreamId, Prg1EnemyStreamId> destinations)
    {
        var occupied = MergeIntervals(region.EnemyStreams.Where(stream => region.PinnedEnemies.Contains(stream.Id))
            .Select(stream => (stream.Id.FileOffset, stream.Id.FileOffset + stream.Length))).ToList();
        var cursor = region.Start;
        foreach (var stream in region.EnemyStreams.Where(stream => !region.PinnedEnemies.Contains(stream.Id)))
        {
            cursor = FindSpace(cursor, payloads[stream.Id].Length, occupied);
            destinations[stream.Id] = new Prg1EnemyStreamId(cursor);
            occupied.Add((cursor, cursor + payloads[stream.Id].Length));
            occupied = MergeIntervals(occupied).ToList();
            cursor += payloads[stream.Id].Length;
        }
    }

    private static bool CanAssignEnemies(
        CompactRegion region,
        IReadOnlyDictionary<Prg1EnemyStreamId, byte[]> payloads,
        Prg1EnemyStreamId? overrideId = null,
        int overrideLength = 0)
    {
        var occupied = MergeIntervals(region.EnemyStreams.Where(stream => region.PinnedEnemies.Contains(stream.Id))
            .Select(stream => (stream.Id.FileOffset, stream.Id.FileOffset + stream.Length))).ToList();
        var cursor = region.Start;
        foreach (var stream in region.EnemyStreams.Where(stream => !region.PinnedEnemies.Contains(stream.Id)))
        {
            var length = overrideId is { } id && stream.Id == id ? overrideLength : payloads[stream.Id].Length;
            cursor = FindSpace(cursor, length, occupied);
            if (cursor + length > region.EndExclusive) return false;
            occupied.Add((cursor, cursor + length));
            occupied = MergeIntervals(occupied).ToList();
            cursor += length;
        }
        return true;
    }

    private static int MaximumEnemyLength(
        CompactRegion region,
        Prg1EnemyStreamId id,
        IReadOnlyDictionary<Prg1EnemyStreamId, byte[]> payloads)
    {
        var low = 0;
        var high = region.Capacity;
        while (low < high)
        {
            var candidate = low + (high - low + 1) / 2;
            if (CanAssignEnemies(region, payloads, id, candidate)) low = candidate;
            else high = candidate - 1;
        }
        return low;
    }

    private static int FindSpace(int cursor, int length, IReadOnlyList<(int Start, int End)> occupied)
    {
        foreach (var interval in occupied.OrderBy(static item => item.Start))
        {
            if (cursor + length <= interval.Start) break;
            if (cursor < interval.End) cursor = interval.End;
        }
        return cursor;
    }

    private static ImmutableArray<(int Start, int End)> MergeIntervals(IEnumerable<(int Start, int End)> source)
    {
        var result = new List<(int Start, int End)>();
        foreach (var interval in source.OrderBy(static item => item.Start).ThenBy(static item => item.End))
        {
            if (result.Count == 0 || interval.Start > result[^1].End) result.Add(interval);
            else result[^1] = (result[^1].Start, Math.Max(result[^1].End, interval.End));
        }
        return result.ToImmutableArray();
    }

    private static bool VerifyGraph(
        Prg1ReferenceIndex before,
        Prg1ReferenceIndex after,
        IReadOnlyDictionary<Prg1LayoutStreamId, Prg1LayoutStreamId> layoutDestinations,
        IReadOnlyDictionary<Prg1EnemyStreamId, Prg1EnemyStreamId> enemyDestinations,
        IReadOnlyDictionary<Prg1LayoutStreamId, StreamMutation<Prg1LayoutStreamId>> layoutMutations,
        IReadOnlyDictionary<Prg1EnemyStreamId, StreamMutation<Prg1EnemyStreamId>> enemyMutations,
        List<Diagnostic> diagnostics)
    {
        Prg1LayoutStreamId MapLayout(Prg1LayoutStreamId id) => layoutDestinations.TryGetValue(id, out var mapped) ? mapped : id;
        Prg1EnemyStreamId MapEnemy(Prg1EnemyStreamId id) => enemyDestinations.TryGetValue(id, out var mapped) ? mapped : id;

        var expectedRoots = before.Roots.Select(root =>
            $"{root.Ordinal}:{root.Kind}:{root.World}:{root.Index}:{root.ObjectSet}:{MapLayout(root.Layout)}:{(root.Enemy is { } enemy ? MapEnemy(enemy) : null)}:{root.SecondaryDataKind}:{root.ConfigurationReference?.Value}").ToArray();
        var actualRoots = after.Roots.Select(root =>
            $"{root.Ordinal}:{root.Kind}:{root.World}:{root.Index}:{root.ObjectSet}:{root.Layout}:{root.Enemy}:{root.SecondaryDataKind}:{root.ConfigurationReference?.Value}").ToArray();
        if (!expectedRoots.SequenceEqual(actualRoots))
            return GraphError(diagnostics, "The rebuilt root graph does not match the requested stream destinations.");

        var expectedLayouts = before.Layouts.Select(stream =>
        {
            var id = MapLayout(stream.Id);
            var length = layoutMutations.TryGetValue(stream.Id, out var mutation) ? mutation.Bytes.Length : stream.Length;
            var sites = stream.PointerSites.Select(site => SiteFingerprint(site, layoutDestinations)).OrderBy(static value => value);
            return $"{id}:{length}:{stream.StorageKind}:{string.Join(',', sites)}";
        }).OrderBy(static value => value).ToArray();
        var actualLayouts = after.Layouts.Select(stream =>
            $"{stream.Id}:{stream.Length}:{stream.StorageKind}:{string.Join(',', stream.PointerSites.Select(static site => SiteFingerprint(site, null)).OrderBy(static value => value))}")
            .OrderBy(static value => value).ToArray();
        if (!expectedLayouts.SequenceEqual(actualLayouts))
        {
            var missing = expectedLayouts.Except(actualLayouts).FirstOrDefault();
            var unexpected = actualLayouts.Except(expectedLayouts).FirstOrDefault();
            return GraphError(diagnostics, $"The rebuilt layout graph has an unresolved, missing, or unexpected pointer site. Expected-only: {missing ?? "none"}; actual-only: {unexpected ?? "none"}.");
        }

        var expectedEnemies = before.Enemies.Select(stream =>
        {
            var id = MapEnemy(stream.Id);
            var length = enemyMutations.TryGetValue(stream.Id, out var mutation) ? mutation.Bytes.Length : stream.Length;
            var sites = stream.PointerSites.Select(site => SiteFingerprint(site, layoutDestinations)).OrderBy(static value => value);
            return $"{id}:{length}:{string.Join(',', sites)}";
        }).OrderBy(static value => value).ToArray();
        var actualEnemies = after.Enemies.Select(stream =>
            $"{stream.Id}:{stream.Length}:{string.Join(',', stream.PointerSites.Select(static site => SiteFingerprint(site, null)).OrderBy(static value => value))}")
            .OrderBy(static value => value).ToArray();
        if (!expectedEnemies.SequenceEqual(actualEnemies))
            return GraphError(diagnostics, "The rebuilt sprite graph has an unresolved, missing, or unexpected pointer site.");

        var expectedJumps = before.JumpReferences.Select(jump =>
            $"{MapLayout(jump.SourceLayout)}:{MapLayout(jump.TargetLayout)}:{MapEnemy(jump.TargetEnemy)}").OrderBy(static value => value).ToArray();
        var actualJumps = after.JumpReferences.Select(jump =>
            $"{jump.SourceLayout}:{jump.TargetLayout}:{jump.TargetEnemy}").OrderBy(static value => value).ToArray();
        if (!expectedJumps.SequenceEqual(actualJumps))
            return GraphError(diagnostics, "The rebuilt alternate-area graph does not match the original topology.");

        return true;
    }

    private static string SiteFingerprint(Prg1PointerSite site, IReadOnlyDictionary<Prg1LayoutStreamId, Prg1LayoutStreamId>? mappings)
    {
        var offset = mappings is null ? site.FileOffset : TranslateSite(site, mappings);
        var containing = site.ContainingLayout is { } layout
            ? (mappings is not null && mappings.TryGetValue(layout, out var mapped) ? mapped : layout).ToString()
            : string.Empty;
        return $"{offset:X}:{site.Origin}:{containing}:{site.RelativeOffset}";
    }

    private static bool GraphError(List<Diagnostic> diagnostics, string message)
    {
        diagnostics.Add(Diagnostics.Error("RELOC_GRAPH_MISMATCH", message));
        return false;
    }

    private static void AddMutation<TId>(
        Dictionary<TId, StreamMutation<TId>> mutations,
        TId id,
        byte[] bytes,
        int originalLength,
        string displayName,
        List<Diagnostic> diagnostics,
        string streamLabel) where TId : notnull
    {
        if (mutations.TryGetValue(id, out var existing))
        {
            if (!existing.Bytes.AsSpan().SequenceEqual(bytes))
                diagnostics.Add(Diagnostics.Error("RELOC_ALIAS_CONFLICT",
                    $"Shared {streamLabel} stream {id} has conflicting edits from {existing.DisplayNames} and {displayName}."));
            else
                mutations[id] = existing with { DisplayNames = $"{existing.DisplayNames}, {displayName}" };
            return;
        }
        mutations[id] = new StreamMutation<TId>(id, bytes, originalLength, displayName);
    }

    private static int TranslateSite(Prg1PointerSite site, IReadOnlyDictionary<Prg1LayoutStreamId, Prg1LayoutStreamId> layouts) =>
        site.ContainingLayout is { } containing && layouts.TryGetValue(containing, out var destination)
            ? checked(destination.FileOffset + site.RelativeOffset)
            : site.FileOffset;

    private static void AddPointerWrite(Dictionary<int, ushort> writes, int offset, ushort value, List<Diagnostic> diagnostics)
    {
        if (writes.TryGetValue(offset, out var existing) && existing != value)
            diagnostics.Add(Diagnostics.Error("RELOC_POINTER_COLLISION", $"Pointer site ${offset:X5} has conflicting relocation targets."));
        else
            writes[offset] = value;
    }

    private static ushort LayoutCpuPointer(Prg1LayoutStreamId id)
    {
        var bank = LayoutBank(id.ObjectSet);
        var bankStart = HeaderSize + bank * BankSize;
        var relative = id.FileOffset - bankStart;
        if (relative is < 0 or >= BankSize) throw new InvalidOperationException("A relocated layout crossed its PRG bank.");
        return checked((ushort)(0xA000 + relative));
    }

    private static ushort EnemyCpuPointer(Prg1EnemyStreamId id)
    {
        var bankStart = HeaderSize + EnemyBank * BankSize;
        var relative = id.FileOffset - bankStart;
        if (relative is < 0 or >= BankSize) throw new InvalidOperationException("A relocated sprite stream crossed PRG6.");
        return checked((ushort)(0xC000 + relative));
    }

    private static int LayoutBank(int objectSet) => objectSet >= 0 && objectSet < LayoutBankByObjectSet.Length
        ? LayoutBankByObjectSet[objectSet]
        : throw new InvalidOperationException($"Object set {objectSet} has no verified layout bank.");

    private static void WriteWord(byte[] bytes, int offset, ushort value)
    {
        if (offset < 0 || offset > bytes.Length - 2) throw new InvalidOperationException("A relocation pointer site is outside the ROM.");
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }

    private sealed record CompactRegion(
        int Bank,
        int Start,
        int EndExclusive,
        int Used,
        ImmutableArray<Prg1LayoutStream> LayoutStreams,
        ImmutableArray<Prg1EnemyStream> EnemyStreams,
        ImmutableHashSet<Prg1LayoutStreamId> PinnedLayouts,
        ImmutableHashSet<Prg1EnemyStreamId> PinnedEnemies,
        bool HasPhysicalOverlaps)
    {
        public int Length => EndExclusive - Start;
        public int Capacity => Length;
        public bool Fits => Used <= Capacity;
    }

    private sealed record StreamMutation<TId>(TId Id, byte[] Bytes, int OriginalLength, string DisplayNames)
    {
        public bool RequiresRelocation => Bytes.Length > OriginalLength;
    }
}
