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
    PatchSetting? ContinuousAutoScroll = null)
{
    // Null settings mean no executable patches are included in a new project.
    // A setting is created only when the designer explicitly adds a patch in
    // the Patches manager; this keeps vanilla projects byte-identical.
    public static PatchSettings None { get; } = new(null, null);

    public bool HasEnabledOptions(IEnumerable<string> areaIds) =>
        (QuickRetry ?? new()).EnabledByDefault ||
        (StartSelectReturnToMap ?? new()).EnabledByDefault ||
        (ContinuousAutoScroll ?? new()).EnabledByDefault ||
        areaIds.Any(areaId => (QuickRetry ?? new()).IsEnabledFor(areaId) ||
                              (StartSelectReturnToMap ?? new()).IsEnabledFor(areaId) ||
                              (ContinuousAutoScroll ?? new()).IsEnabledFor(areaId));
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
    IReadOnlyList<string>? ExternalPatches = null)
{
    public const int CurrentFormatVersion = 6;

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
        []);

    public ProjectDocumentV2 WithArea(LevelDocument document)
    {
        var areas = new Dictionary<string, LevelDocument>(ModifiedAreas, StringComparer.Ordinal)
        {
            [document.AreaId] = document
        };
        return this with { ModifiedAreas = areas, EditorState = EditorState with { LastAreaId = document.AreaId } };
    }
}
