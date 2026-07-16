using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Smb3Editor.Core;

public enum Prg1RootKind
{
    Overworld,
    Airship,
    CoinShip,
    GenericExit,
    BigQuestionRoom,
    ToadOrWarpRoom
}

public enum Prg1PointerSiteOrigin
{
    Overworld,
    Airship,
    CoinShip,
    GenericExit,
    BigQuestionRoom,
    ToadOrWarpRoom,
    LayoutHeader
}

public enum Prg1LayoutStorageKind
{
    RelocatableBankStream,
    SpecialNonRelocatable
}

public enum Prg1RootSecondaryDataKind
{
    None,
    EnemyStream,
    ItemOrConfigurationValue
}

public readonly record struct Prg1LayoutStreamId(int FileOffset, int ObjectSet);
public readonly record struct Prg1EnemyStreamId(int FileOffset);

/// <summary>
/// A little-endian, two-byte pointer site that targets a stream. Header sites retain
/// their containing stream and relative position so a later relocator can translate
/// the site itself when its containing layout moves.
/// </summary>
public sealed record Prg1PointerSite(
    int FileOffset,
    Prg1PointerSiteOrigin Origin,
    Prg1LayoutStreamId? ContainingLayout,
    int RelativeOffset);

public sealed record Prg1LayoutStream(
    Prg1LayoutStreamId Id,
    int Length,
    ImmutableArray<Prg1PointerSite> PointerSites,
    Prg1LayoutStorageKind StorageKind)
{
    public bool IsRelocatable => StorageKind == Prg1LayoutStorageKind.RelocatableBankStream;
}

public sealed record Prg1EnemyStream(
    Prg1EnemyStreamId Id,
    int Length,
    ImmutableArray<Prg1PointerSite> PointerSites)
{
    public bool IsRelocatable => true;
}

/// <summary>
/// A root-table value used as an item/configuration selector rather than as an
/// enemy-stream pointer. It must be retained and rewritten only by the owning
/// overworld/special-root serializer, never by the PRG6 enemy allocator.
/// </summary>
public sealed record Prg1ConfigurationReference(
    Prg1RootKind Kind,
    int World,
    int Index,
    ushort Value,
    Prg1PointerSite PointerSite);

public sealed record Prg1RootReference(
    int Ordinal,
    Prg1RootKind Kind,
    int World,
    int Index,
    int ObjectSet,
    Prg1LayoutStreamId Layout,
    Prg1EnemyStreamId? Enemy,
    Prg1RootSecondaryDataKind SecondaryDataKind,
    Prg1PointerSite LayoutPointerSite,
    Prg1PointerSite? EnemyPointerSite,
    Prg1ConfigurationReference? ConfigurationReference);

public sealed record Prg1JumpReference(
    Prg1LayoutStreamId SourceLayout,
    Prg1LayoutStreamId TargetLayout,
    Prg1EnemyStreamId TargetEnemy,
    Prg1PointerSite LayoutPointerSite,
    Prg1PointerSite EnemyPointerSite);

/// <summary>An immutable, complete reference graph for the authenticated US PRG1 ROM.</summary>
public sealed record Prg1ReferenceIndex(
    ImmutableArray<Prg1RootReference> Roots,
    ImmutableArray<Prg1LayoutStream> Layouts,
    ImmutableArray<Prg1EnemyStream> Enemies,
    ImmutableArray<Prg1JumpReference> JumpReferences,
    ImmutableArray<Prg1ConfigurationReference> ConfigurationReferences)
{
    public int LayoutPointerSiteCount => Layouts.Sum(static stream => stream.PointerSites.Length);
    public int EnemyPointerSiteCount => Enemies.Sum(static stream => stream.PointerSites.Length);
}

/// <summary>
/// Builds the independently verified PRG1 stream graph. This parser is intentionally
/// fail-closed and is not used by the compiler until relocation is separately enabled.
/// </summary>
public static class Prg1ReferenceIndexBuilder
{
    private const string VerifiedSha1 = "6bd518e85eb46a4252af07910f61036e84b020d1";
    private const int HeaderSize = 16;
    private const int BankSize = 0x2000;
    private const int EnemyBank = 6;

    private static readonly int[] LayoutBankByObjectSet =
        [11, 15, 21, 16, 17, 19, 18, 18, 18, 20, 23, 19, 17, 19, 13, 26, 26, 26, 9];

    private static readonly ImmutableDictionary<int, ImmutableHashSet<int>> FourByteGenerators =
        new Dictionary<int, ImmutableHashSet<int>>
        {
            [1] = Set(11, 12, 35, 36, 37, 38, 39, 40, 41, 42),
            [2] = Set(13, 14, 35, 36, 37, 38, 39, 40, 41, 42, 46, 47, 48, 57),
            [3] = Set(35, 36, 37, 38, 39, 40, 41, 42, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71),
            [4] = Set(0, 35, 36, 37, 38, 39, 40, 41, 42, 54),
            [5] = Set(13, 35, 36, 37, 38, 39, 40, 41, 42, 45, 46),
            [6] = Set(35, 36, 37, 38, 39, 40, 41, 42, 49, 57),
            [7] = Set(35, 36, 37, 38, 39, 40, 41, 42, 49, 57),
            [8] = Set(35, 36, 37, 38, 39, 40, 41, 42, 49, 57),
            [9] = Set(10, 11, 12, 13, 35, 36, 37, 38, 39, 40, 41, 42),
            [10] = Set(1, 2, 35, 36, 37, 38, 39, 40, 41, 42, 48, 51),
            [11] = Set(13, 35, 36, 37, 38, 39, 40, 41, 42, 45, 46),
            [12] = Set(0, 35, 36, 37, 38, 39, 40, 41, 42, 54),
            [13] = Set(13, 35, 36, 37, 38, 39, 40, 41, 42, 45, 46),
            [14] = Set(35, 36, 37, 38, 39, 40, 41, 42, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71),
            [15] = Set()
        }.ToImmutableDictionary();

    // Headered file offsets, independently cross-checked against the PRG1 disassembly.
    private const int AirshipLayouts = 0x19291;
    private const int AirshipEnemies = 0x192A1;
    private const int ToadLayouts = 0x192F8;
    private const int ToadConfigurations = 0x19308;
    private const int CoinShipLayouts = 0x19337;
    private const int CoinShipEnemies = 0x19347;
    private const int BigQuestionLayouts = 0x3491B;
    private const int BigQuestionEnemies = 0x3492B;
    private const int BigQuestionObjectSets = 0x3493B;
    private const int GenericExitLayouts = 0x34B27;
    private const int GenericExitEnemies = 0x34B37;
    private const int GenericExitObjectSets = 0x34B47;

    public static OperationResult<Prg1ReferenceIndex> Build(RomImage source)
    {
        var authentication = AuthenticateSource(source);
        if (authentication is not null)
        {
            return OperationResult<Prg1ReferenceIndex>.Failure(authentication);
        }

        return BuildSafely(source, requireCleanRootCount: true);
    }

    /// <summary>
    /// Rebuilds the graph from a current editor-produced PRG1 snapshot. The clean
    /// source is authenticated independently; the snapshot must retain its exact
    /// container, mapper declaration, and CHR data. This supports overworld table
    /// compaction without accepting an unauthenticated ROM as the source profile.
    /// </summary>
    public static OperationResult<Prg1ReferenceIndex> BuildCurrent(RomImage authenticatedSource, byte[] currentBytes)
    {
        var authentication = AuthenticateSource(authenticatedSource);
        if (authentication is not null)
            return OperationResult<Prg1ReferenceIndex>.Failure(authentication);
        if (currentBytes is null)
            return OperationResult<Prg1ReferenceIndex>.Failure(
                Diagnostics.Error("PRG1_INDEX_CURRENT", "The current PRG1 snapshot is unavailable."));
        if (currentBytes.Length != authenticatedSource.Bytes.Length ||
            currentBytes.Length < HeaderSize ||
            !currentBytes.AsSpan(0, HeaderSize).SequenceEqual(authenticatedSource.Bytes.AsSpan(0, HeaderSize)) ||
            !currentBytes.AsSpan(authenticatedSource.ChrOffset, authenticatedSource.ChrLength).SequenceEqual(authenticatedSource.Chr))
        {
            return OperationResult<Prg1ReferenceIndex>.Failure(Diagnostics.Error(
                "PRG1_INDEX_CURRENT",
                "The current snapshot does not preserve the authenticated PRG1 container, mapper declaration, and CHR data."));
        }

        var snapshotBytes = currentBytes.ToArray();
        var snapshot = RomImage.CreateForTesting(authenticatedSource.SourcePath, snapshotBytes, authenticatedSource.Profile);
        return BuildSafely(snapshot, requireCleanRootCount: false);
    }

    private static OperationResult<Prg1ReferenceIndex> BuildSafely(RomImage source, bool requireCleanRootCount)
    {
        try
        {
            return BuildSnapshot(source, requireCleanRootCount);
        }
        catch (ReferenceIndexException ex)
        {
            return OperationResult<Prg1ReferenceIndex>.Failure(
                Diagnostics.Error("PRG1_INDEX_INVALID", ex.Message));
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
        {
            return OperationResult<Prg1ReferenceIndex>.Failure(Diagnostics.Error(
                "PRG1_INDEX_RANGE",
                $"The PRG1 reference graph could not be read safely: {ex.Message}"));
        }
    }

    private static Diagnostic? AuthenticateSource(RomImage source)
    {
        if (!string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal) ||
            !string.Equals(source.Sha1, VerifiedSha1, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Convert.ToHexString(SHA1.HashData(source.Bytes)).ToLowerInvariant(), VerifiedSha1, StringComparison.Ordinal))
        {
            return Diagnostics.Error(
                "PRG1_INDEX_PROFILE",
                "The relocation reference index is available only for the authenticated US PRG1 ROM.");
        }

        return null;
    }

    private static OperationResult<Prg1ReferenceIndex> BuildSnapshot(RomImage source, bool requireCleanRootCount)
    {
        var bytes = source.Bytes;
        var roots = ReadRoots(source);
        if (requireCleanRootCount && roots.Count != 380)
            throw Invalid($"Expected 380 root records, but found {roots.Count}.");

        var layoutSites = new Dictionary<Prg1LayoutStreamId, Dictionary<int, Prg1PointerSite>>();
        var enemySites = new Dictionary<Prg1EnemyStreamId, Dictionary<int, Prg1PointerSite>>();
        var pending = new Queue<(Prg1LayoutStreamId Layout, Prg1EnemyStreamId? Enemy)>();
        foreach (var root in roots)
        {
            AddSite(layoutSites, root.Layout, root.LayoutPointerSite);
            if (root.Enemy is { } enemy && root.EnemyPointerSite is { } enemySite)
                AddSite(enemySites, enemy, enemySite);
            pending.Enqueue((root.Layout, root.Enemy));
        }

        var visited = new HashSet<(Prg1LayoutStreamId Layout, Prg1EnemyStreamId? Enemy)>();
        var layouts = new Dictionary<Prg1LayoutStreamId, ParsedStream>();
        var enemies = new Dictionary<Prg1EnemyStreamId, ParsedStream>();
        var jumps = new Dictionary<(Prg1LayoutStreamId, Prg1LayoutStreamId, Prg1EnemyStreamId), Prg1JumpReference>();

        while (pending.TryDequeue(out var state))
        {
            if (!visited.Add(state)) continue;
            if (!layouts.TryGetValue(state.Layout, out var layout))
            {
                layout = ParseLayout(bytes, state.Layout);
                layouts.Add(state.Layout, layout);
            }

            var enemyHasJump = false;
            if (state.Enemy is { } enemyId)
            {
                if (!enemies.TryGetValue(enemyId, out var enemy))
                {
                    enemy = ParseEnemies(bytes, enemyId);
                    enemies.Add(enemyId, enemy);
                }
                enemyHasJump = enemy.HasJump;
            }

            if (!layout.HasJump && !enemyHasJump) continue;

            EnsureRange(bytes, state.Layout.FileOffset, 9, "layout header");
            var nextObjectSet = bytes[state.Layout.FileOffset + 6] & 0x0F;
            var layoutWord = ReadWord(bytes, state.Layout.FileOffset);
            var enemyWord = ReadWord(bytes, state.Layout.FileOffset + 2);

            // Some special junction commands use engine-owned world tables rather than
            // the alternate pointers in this header. Only a complete, valid header pair
            // constitutes a recursive stream reference.
            if (nextObjectSet == 0 || layoutWord < 0xA000 || enemyWord < 0xC000) continue;
            if (nextObjectSet > 15 || layoutWord > 0xBFFF || enemyWord > 0xDFFF)
                throw Invalid($"Layout ${state.Layout.FileOffset:X5} contains an out-of-range alternate-area pointer.");

            var targetLayout = new Prg1LayoutStreamId(LayoutFileOffset(nextObjectSet, layoutWord), nextObjectSet);
            var targetEnemy = new Prg1EnemyStreamId(EnemyFileOffset(enemyWord));
            var layoutSite = new Prg1PointerSite(state.Layout.FileOffset, Prg1PointerSiteOrigin.LayoutHeader, state.Layout, 0);
            var enemySite = new Prg1PointerSite(state.Layout.FileOffset + 2, Prg1PointerSiteOrigin.LayoutHeader, state.Layout, 2);
            AddSite(layoutSites, targetLayout, layoutSite);
            AddSite(enemySites, targetEnemy, enemySite);
            var jump = new Prg1JumpReference(state.Layout, targetLayout, targetEnemy, layoutSite, enemySite);
            jumps.TryAdd((state.Layout, targetLayout, targetEnemy), jump);
            pending.Enqueue((targetLayout, targetEnemy));
        }

        var layoutRecords = layouts
            .OrderBy(static item => item.Key.ObjectSet)
            .ThenBy(static item => item.Key.FileOffset)
            .Select(item => new Prg1LayoutStream(item.Key, item.Value.Length, SitesFor(layoutSites, item.Key), LayoutStorageKind(item.Key)))
            .ToImmutableArray();
        var enemyRecords = enemies
            .OrderBy(static item => item.Key.FileOffset)
            .Select(item => new Prg1EnemyStream(item.Key, item.Value.Length, SitesFor(enemySites, item.Key)))
            .ToImmutableArray();
        var jumpRecords = jumps.Values
            .OrderBy(static item => item.SourceLayout.FileOffset)
            .ThenBy(static item => item.SourceLayout.ObjectSet)
            .ThenBy(static item => item.TargetLayout.FileOffset)
            .ThenBy(static item => item.TargetEnemy.FileOffset)
            .ToImmutableArray();
        var configurationRecords = roots
            .Select(static root => root.ConfigurationReference)
            .OfType<Prg1ConfigurationReference>()
            .OrderBy(static item => item.PointerSite.FileOffset)
            .ToImmutableArray();

        var index = new Prg1ReferenceIndex(roots.ToImmutableArray(), layoutRecords, enemyRecords, jumpRecords, configurationRecords);
        return OperationResult<Prg1ReferenceIndex>.Success(index);
    }

    private static List<Prg1RootReference> ReadRoots(RomImage source)
    {
        var parsedMaps = Smb3OverworldParser.Parse(source);
        if (!parsedMaps.IsSuccess)
            throw Invalid($"The ordinary overworld roots are malformed: {string.Join("; ", parsedMaps.Diagnostics.Select(static d => d.Message))}");

        var roots = new List<Prg1RootReference>(380);
        var ordinal = 0;
        foreach (var map in parsedMaps.Value!.Where(static map => map.World < 8).OrderBy(static map => map.World))
        {
            foreach (var pointer in map.LevelPointers.OrderBy(static pointer => pointer.Index))
            {
                roots.Add(CreateRoot(source.Bytes, ordinal++, Prg1RootKind.Overworld, map.World + 1, pointer.Index,
                    pointer.ObjectSet, pointer.LevelPointerOffset, pointer.EnemyPointerOffset, null));
            }
        }

        for (var world = 0; world < 8; world++)
        {
            roots.Add(CreateRoot(source.Bytes, ordinal++, Prg1RootKind.Airship, world + 1, 0, 10,
                AirshipLayouts + world * 2, AirshipEnemies + world * 2, null));
            roots.Add(CreateRoot(source.Bytes, ordinal++, Prg1RootKind.CoinShip, world + 1, 0, 10,
                CoinShipLayouts + world * 2, CoinShipEnemies + world * 2, null));
            roots.Add(CreateRoot(source.Bytes, ordinal++, Prg1RootKind.GenericExit, world + 1, 0,
                ReadByte(source.Bytes, GenericExitObjectSets + world), GenericExitLayouts + world * 2, GenericExitEnemies + world * 2, null));
            roots.Add(CreateRoot(source.Bytes, ordinal++, Prg1RootKind.BigQuestionRoom, world + 1, 0,
                ReadByte(source.Bytes, BigQuestionObjectSets + world), BigQuestionLayouts + world * 2, BigQuestionEnemies + world * 2, null));
            roots.Add(CreateRoot(source.Bytes, ordinal++, Prg1RootKind.ToadOrWarpRoom, world + 1, 0, 7,
                ToadLayouts + world * 2, null, ToadConfigurations + world * 2));
        }

        return roots;
    }

    private static Prg1RootReference CreateRoot(
        byte[] bytes, int ordinal, Prg1RootKind kind, int world, int index, int objectSet,
        int layoutPointerOffset, int? enemyPointerOffset, int? configurationPointerOffset)
    {
        if (!FourByteGenerators.ContainsKey(objectSet))
            throw Invalid($"{kind} root {world}:{index} uses unsupported object set {objectSet}.");
        var layoutWord = ReadWord(bytes, layoutPointerOffset);
        // Overworld tables store a bank-relative value. Most normal stages happen
        // to look like $A000-$BFFF CPU pointers, while bonus roots can validly be 0.
        var layout = new Prg1LayoutStreamId(LayoutFileOffset(objectSet, layoutWord), objectSet);
        var origin = OriginFor(kind);
        var layoutSite = new Prg1PointerSite(layoutPointerOffset, origin, null, 0);

        Prg1EnemyStreamId? enemy = null;
        Prg1PointerSite? enemySite = null;
        Prg1ConfigurationReference? configuration = null;
        var secondaryKind = Prg1RootSecondaryDataKind.None;
        if (enemyPointerOffset is { } site)
        {
            var enemyWord = ReadWord(bytes, site);
            var pointerSite = new Prg1PointerSite(site, origin, null, 0);
            if (enemyWord is >= 0xC000 and <= 0xDFFF)
            {
                enemy = new Prg1EnemyStreamId(EnemyFileOffset(enemyWord));
                enemySite = pointerSite;
                secondaryKind = Prg1RootSecondaryDataKind.EnemyStream;
            }
            else if (kind == Prg1RootKind.Overworld && objectSet is 7 or 15)
            {
                configuration = new Prg1ConfigurationReference(kind, world, index, enemyWord, pointerSite);
                secondaryKind = Prg1RootSecondaryDataKind.ItemOrConfigurationValue;
            }
            else
            {
                throw Invalid($"{kind} root {world}:{index} has invalid enemy pointer ${enemyWord:X4}.");
            }
        }
        if (configurationPointerOffset is { } configurationSite)
        {
            if (secondaryKind != Prg1RootSecondaryDataKind.None)
                throw Invalid($"{kind} root {world}:{index} has conflicting secondary data tables.");
            var pointerSite = new Prg1PointerSite(configurationSite, origin, null, 0);
            configuration = new Prg1ConfigurationReference(kind, world, index, ReadWord(bytes, configurationSite), pointerSite);
            secondaryKind = Prg1RootSecondaryDataKind.ItemOrConfigurationValue;
        }

        return new Prg1RootReference(ordinal, kind, world, index, objectSet, layout, enemy,
            secondaryKind, layoutSite, enemySite, configuration);
    }

    private static ParsedStream ParseLayout(byte[] bytes, Prg1LayoutStreamId id)
    {
        var bank = LayoutBankByObjectSet[id.ObjectSet];
        var bankEnd = HeaderSize + (bank + 1) * BankSize;
        // Root offsets are stored relative to the object's $A000 mapping. Bonus
        // roots can therefore reside before the normal bank window, but may never
        // cross the verified upper bank boundary for their object set.
        if (id.FileOffset < HeaderSize || id.FileOffset > bankEnd - 10)
            throw Invalid($"Layout ${id.FileOffset:X5} is outside object set {id.ObjectSet}'s verified range.");

        var cursor = id.FileOffset + 9;
        var hasJump = false;
        while (cursor < bankEnd)
        {
            if (bytes[cursor] == 0xFF) return new ParsedStream(cursor + 1 - id.FileOffset, hasJump);
            if (cursor > bankEnd - 3) throw Invalid($"Layout ${id.FileOffset:X5} has a truncated command.");
            var first = bytes[cursor];
            var shape = bytes[cursor + 2];
            var domain = first >> 5;
            hasJump |= IsLayoutJump(id.ObjectSet, domain, shape);
            var length = 3;
            if ((first & 0xE0) != 0xE0 && (shape & 0xF0) != 0)
            {
                var generator = domain * 15 + (shape >> 4) - 1;
                if (FourByteGenerators[id.ObjectSet].Contains(generator)) length = 4;
            }
            if (cursor > bankEnd - length) throw Invalid($"Layout ${id.FileOffset:X5} has a truncated {length}-byte command.");
            cursor += length;
        }
        throw Invalid($"Layout ${id.FileOffset:X5} has no terminator in PRG bank {bank}.");
    }

    private static ParsedStream ParseEnemies(byte[] bytes, Prg1EnemyStreamId id)
    {
        var bankStart = HeaderSize + EnemyBank * BankSize;
        var bankEnd = HeaderSize + (EnemyBank + 1) * BankSize;
        if (id.FileOffset < bankStart || id.FileOffset > bankEnd - 2)
            throw Invalid($"Enemy/item stream ${id.FileOffset:X5} is outside its verified readable range.");
        var cursor = id.FileOffset + 1;
        var hasJump = false;
        while (cursor < bankEnd)
        {
            if (bytes[cursor] == 0xFF) return new ParsedStream(cursor + 1 - id.FileOffset, hasJump);
            if (cursor > bankEnd - 3) throw Invalid($"Enemy stream ${id.FileOffset:X5} has a truncated command.");
            hasJump |= bytes[cursor] is 0x08 or 0xD5;
            cursor += 3;
        }
        throw Invalid($"Enemy stream ${id.FileOffset:X5} has no terminator in PRG bank {EnemyBank}.");
    }

    private static bool IsLayoutJump(int objectSet, int domain, int shape)
    {
        if (domain == 1 && (shape & 0xF0) is 0x90 or 0xC0 or 0xE0) return true;
        if (domain == 2 && (shape == 0x07 || (shape & 0xF0) == 0x10)) return true;
        if (domain != 0) return false;
        return objectSet switch
        {
            1 => shape == 0x04,
            2 => shape is 0x00 or 0x06,
            3 or 14 => shape == 0x0F,
            4 or 12 => shape == 0x05,
            6 or 8 => shape == 0x0A,
            7 or 15 => shape == 0x04,
            9 => shape == 0x0B,
            _ => false
        };
    }

    private static int LayoutFileOffset(int objectSet, ushort cpuPointer)
    {
        if (objectSet < 0 || objectSet >= LayoutBankByObjectSet.Length)
            throw Invalid($"Object set {objectSet} has no verified layout bank.");
        return checked(HeaderSize + LayoutBankByObjectSet[objectSet] * BankSize + cpuPointer - 0xA000);
    }

    private static int EnemyFileOffset(ushort cpuPointer) => checked(HeaderSize + EnemyBank * BankSize + cpuPointer - 0xC000);

    private static Prg1LayoutStorageKind LayoutStorageKind(Prg1LayoutStreamId id)
    {
        var bankStart = HeaderSize + LayoutBankByObjectSet[id.ObjectSet] * BankSize;
        var bankEnd = bankStart + BankSize;
        return id.ObjectSet is >= 1 and <= 14 && id.FileOffset >= bankStart && id.FileOffset < bankEnd
            ? Prg1LayoutStorageKind.RelocatableBankStream
            : Prg1LayoutStorageKind.SpecialNonRelocatable;
    }

    private static ushort ReadWord(byte[] bytes, int offset)
    {
        EnsureRange(bytes, offset, 2, "pointer site");
        return (ushort)(bytes[offset] | bytes[offset + 1] << 8);
    }

    private static byte ReadByte(byte[] bytes, int offset)
    {
        EnsureRange(bytes, offset, 1, "table value");
        return bytes[offset];
    }

    private static void EnsureRange(byte[] bytes, int offset, int length, string label)
    {
        if (offset < 0 || length < 0 || offset > bytes.Length - length)
            throw Invalid($"The {label} at ${offset:X} is outside the authenticated ROM.");
    }

    private static void AddSite<TKey>(Dictionary<TKey, Dictionary<int, Prg1PointerSite>> sites, TKey key, Prg1PointerSite site)
        where TKey : notnull
    {
        if (!sites.TryGetValue(key, out var byOffset)) sites[key] = byOffset = [];
        if (byOffset.TryGetValue(site.FileOffset, out var existing) && existing != site)
            throw Invalid($"Pointer site ${site.FileOffset:X5} has conflicting graph identities.");
        byOffset[site.FileOffset] = site;
    }

    private static ImmutableArray<Prg1PointerSite> SitesFor<TKey>(
        Dictionary<TKey, Dictionary<int, Prg1PointerSite>> sites, TKey key) where TKey : notnull =>
        sites.TryGetValue(key, out var found)
            ? found.Values.OrderBy(static site => site.FileOffset).ToImmutableArray()
            : [];

    private static Prg1PointerSiteOrigin OriginFor(Prg1RootKind kind) => kind switch
    {
        Prg1RootKind.Overworld => Prg1PointerSiteOrigin.Overworld,
        Prg1RootKind.Airship => Prg1PointerSiteOrigin.Airship,
        Prg1RootKind.CoinShip => Prg1PointerSiteOrigin.CoinShip,
        Prg1RootKind.GenericExit => Prg1PointerSiteOrigin.GenericExit,
        Prg1RootKind.BigQuestionRoom => Prg1PointerSiteOrigin.BigQuestionRoom,
        Prg1RootKind.ToadOrWarpRoom => Prg1PointerSiteOrigin.ToadOrWarpRoom,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static ImmutableHashSet<int> Set(params int[] values) => values.ToImmutableHashSet();
    private static ReferenceIndexException Invalid(string message) => new(message);
    private sealed record ParsedStream(int Length, bool HasJump);
    private sealed class ReferenceIndexException(string message) : Exception(message);
}
