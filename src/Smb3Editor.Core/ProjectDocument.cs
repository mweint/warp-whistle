namespace Smb3Editor.Core;

public sealed record ProjectSource(
    string ProfileId,
    string Sha1,
    string Sha256,
    string RomPathHint);

public sealed record EditorState(
    string? LastAreaId = null,
    double Zoom = 1,
    double PanX = 0,
    double PanY = 0,
    string? EmulatorPath = null,
    IReadOnlyList<string>? EmulatorArguments = null,
    string PlayMode = "rom");

public sealed record PaletteOverride(int Tileset, bool Objects, int Slot, IReadOnlyList<byte> Colors);
public sealed record PaletteSlotLabel(int Tileset, bool Objects, int Slot, string Name);
/// <summary>Fixed-size overworld tile edits, serialized only to their verified vanilla PRG1 slots.</summary>
public sealed record OverworldTileOverride(int World, IReadOnlyList<byte> Tiles);
/// <summary>One existing vanilla map-entry slot; raw pointers preserve bonus and other non-stage entries.</summary>
public sealed record OverworldLevelPointerOverride(int World, int Index, int Screen, int Column, int Row, int ObjectSet, ushort LevelOffset, ushort EnemyOffset);
/// <summary>A complete vanilla node set for one normal overworld. The compiler rebuilds its parallel tables.</summary>
public sealed record OverworldNodeSetOverride(int World, IReadOnlyList<OverworldNodeOverride> Nodes);
public sealed record OverworldNodeOverride(int Screen, int Column, int Row, int ObjectSet, ushort LevelOffset, ushort EnemyOffset);
public sealed record OverworldLockBridgeOverride(int World, int Slot, int Screen, int Column, int Row, byte ReplacementTile);
public sealed record OverworldMapSpriteOverride(int World, int Index, int Screen, int Column, int Row, byte Type, byte Item);
/// <summary>One shared vanilla overworld palette entry. Palette indices are shared by worlds that reference them.</summary>
public sealed record OverworldPaletteOverride(int Palette, bool Sprites, IReadOnlyList<byte> Colors);

public enum RomOutputMode
{
    Vanilla,
    EnhancedMmc3
}

public enum RomStorageMode
{
    FixedSlots,
    ExpandedBanks
}

/// <summary>One opt-in patch with a project default and explicit level overrides.</summary>
public sealed record PatchSetting(
    bool EnabledByDefault = false,
    IReadOnlyDictionary<string, bool>? LevelOverrides = null)
{
    public bool IsEnabledFor(string areaId) =>
        LevelOverrides is not null && LevelOverrides.TryGetValue(areaId, out var enabled)
            ? enabled
            : EnabledByDefault;
}

/// <summary>
/// Enhanced, hardware-compatible patches. These are deliberately separate
/// from vanilla level data and are applied only by the PRG1 patch compiler.
/// </summary>
public sealed record PatchSettings(
    PatchSetting? QuickRetry = null,
    PatchSetting? StartSelectReturnToMap = null,
    PatchSetting? ContinuousAutoScroll = null,
    IReadOnlyDictionary<string, PatchSetting>? Additional = null)
{
    // Null settings mean no executable patches are included in a new project.
    // A setting is created only when the designer explicitly adds a patch in
    // the Patches manager; this keeps vanilla projects byte-identical.
    public static PatchSettings None { get; } = new();

    public PatchSetting? Get(string id) => id switch
    {
        "quick-retry" => QuickRetry,
        "start-select-map" => StartSelectReturnToMap,
        "continuous-auto-scroll" => ContinuousAutoScroll,
        _ => Additional is not null && Additional.TryGetValue(id, out var setting) ? setting : null
    };

    public PatchSettings With(string id, PatchSetting? setting)
    {
        if (id == "quick-retry") return this with { QuickRetry = setting };
        if (id == "start-select-map") return this with { StartSelectReturnToMap = setting };
        if (id == "continuous-auto-scroll") return this with { ContinuousAutoScroll = setting };
        var additional = new Dictionary<string, PatchSetting>(Additional ?? new Dictionary<string, PatchSetting>(), StringComparer.Ordinal);
        if (setting is null) additional.Remove(id); else additional[id] = setting;
        return this with { Additional = additional };
    }

    public IEnumerable<KeyValuePair<string, PatchSetting>> Enumerate()
    {
        if (QuickRetry is not null) yield return new("quick-retry", QuickRetry);
        if (StartSelectReturnToMap is not null) yield return new("start-select-map", StartSelectReturnToMap);
        if (ContinuousAutoScroll is not null) yield return new("continuous-auto-scroll", ContinuousAutoScroll);
        if (Additional is not null)
            foreach (var pair in Additional) yield return pair;
    }

    public bool HasEnabledOptions(IEnumerable<string> areaIds) =>
        Enumerate().Any(pair => pair.Value.EnabledByDefault || areaIds.Any(pair.Value.IsEnabledFor));
}

public sealed record ProjectDocumentV2(
    int FormatVersion,
    ProjectSource Source,
    IReadOnlyDictionary<string, LevelDocument> ModifiedAreas,
    EditorState EditorState,
    IReadOnlyList<PaletteOverride>? PaletteOverrides = null,
    IReadOnlyList<PaletteSlotLabel>? PaletteSlotLabels = null,
    PatchSettings? Patches = null,
    RomOutputMode OutputMode = RomOutputMode.Vanilla,
    RomStorageMode StorageMode = RomStorageMode.FixedSlots,
    IReadOnlyList<string>? ExternalPatches = null,
    IReadOnlyList<OverworldTileOverride>? OverworldTiles = null,
    IReadOnlyList<OverworldLevelPointerOverride>? OverworldLevelPointers = null,
    IReadOnlyList<OverworldNodeSetOverride>? OverworldNodeSets = null,
    IReadOnlyList<OverworldLockBridgeOverride>? OverworldLocksAndBridges = null,
    IReadOnlyList<OverworldPaletteOverride>? OverworldPalettes = null,
    IReadOnlyList<OverworldMapSpriteOverride>? OverworldMapSprites = null)
{
    public const int CurrentFormatVersion = 12;

    public static ProjectDocumentV2 Create(RomImage source) => new(
        CurrentFormatVersion,
        new ProjectSource(source.Profile.Id, source.Sha1, source.Sha256, source.SourcePath),
        new Dictionary<string, LevelDocument>(StringComparer.Ordinal),
        new EditorState(),
        [],
        [],
        PatchSettings.None,
        RomOutputMode.Vanilla,
        RomStorageMode.FixedSlots,
        [],
        [],
        [],
        [],
        [],
        [],
        []);

    public ProjectDocumentV2 WithArea(LevelDocument document)
    {
        var areas = new Dictionary<string, LevelDocument>(ModifiedAreas, StringComparer.Ordinal)
        {
            [document.AreaId] = document
        };
        return this with { ModifiedAreas = areas, EditorState = EditorState with { LastAreaId = document.AreaId } };
    }

    public ProjectDocumentV2 WithOverworld(OverworldDocument document)
    {
        var maps = (OverworldTiles ?? []).Where(item => item.World != document.World)
            .Append(new OverworldTileOverride(document.World, document.Tiles.ToArray()))
            .OrderBy(static item => item.World).ToArray();
        return this with { OverworldTiles = maps };
    }

    public ProjectDocumentV2 WithOverworldLevelPointer(int world, OverworldLevelPointer pointer)
    {
        var replacement = new OverworldLevelPointerOverride(world, pointer.Index, pointer.Screen, pointer.Column, pointer.Row,
            pointer.ObjectSet, pointer.LevelOffset, pointer.EnemyOffset);
        var entries = (OverworldLevelPointers ?? [])
            .Where(item => item.World != world || item.Index != pointer.Index)
            .Append(replacement)
            .OrderBy(static item => item.World).ThenBy(static item => item.Index).ToArray();
        return this with { OverworldLevelPointers = entries };
    }

    public ProjectDocumentV2 WithOverworldNodes(OverworldDocument document)
    {
        var nodes = document.LevelPointers
            .OrderBy(static node => node.Screen).ThenBy(static node => node.Row).ThenBy(static node => node.Column)
            .Select(static node => new OverworldNodeOverride(node.Screen, node.Column, node.Row, node.ObjectSet, node.LevelOffset, node.EnemyOffset))
            .ToArray();
        var sets = (OverworldNodeSets ?? []).Where(item => item.World != document.World)
            .Append(new OverworldNodeSetOverride(document.World, nodes))
            .OrderBy(static item => item.World).ToArray();
        // Index-based overrides address the source table. They cannot safely coexist
        // with a rebuilt table for the same world.
        var legacy = (OverworldLevelPointers ?? []).Where(item => item.World != document.World).ToArray();
        return this with { OverworldNodeSets = sets, OverworldLevelPointers = legacy };
    }

    public ProjectDocumentV2 WithOverworldLockBridge(OverworldLockBridge item)
    {
        var replacement = new OverworldLockBridgeOverride(item.World, item.Slot, item.Screen, item.Column, item.Row, item.ReplacementTile);
        var entries = (OverworldLocksAndBridges ?? [])
            .Where(existing => existing.World != item.World || existing.Slot != item.Slot)
            .Append(replacement)
            .OrderBy(static existing => existing.World).ThenBy(static existing => existing.Slot).ToArray();
        return this with { OverworldLocksAndBridges = entries };
    }

    public ProjectDocumentV2 WithOverworldMapSprite(OverworldMapSprite item)
    {
        var replacement = new OverworldMapSpriteOverride(item.World, item.Index, item.Screen, item.Column, item.Row, item.Type, item.Item);
        var entries = (OverworldMapSprites ?? [])
            .Where(existing => existing.World != item.World || existing.Index != item.Index)
            .Append(replacement)
            .OrderBy(static existing => existing.World).ThenBy(static existing => existing.Index).ToArray();
        return this with { OverworldMapSprites = entries };
    }


    public ProjectDocumentV2 WithOverworldPalette(int palette, bool sprites, IReadOnlyList<byte> colors)
    {
        if (palette < 0 || colors.Count != 16) return this;
        var entries = (OverworldPalettes ?? [])
            .Where(item => item.Palette != palette || item.Sprites != sprites)
            .Append(new OverworldPaletteOverride(palette, sprites, colors.Select(static color => (byte)(color & 0x3F)).ToArray()))
            .OrderBy(static item => item.Sprites).ThenBy(static item => item.Palette).ToArray();
        return this with { OverworldPalettes = entries };
    }
}
