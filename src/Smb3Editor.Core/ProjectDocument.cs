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
    IReadOnlyList<string>? EmulatorArguments = null);

public sealed record ProjectDocumentV2(
    int FormatVersion,
    ProjectSource Source,
    IReadOnlyDictionary<string, LevelDocument> ModifiedAreas,
    EditorState EditorState)
{
    public const int CurrentFormatVersion = 2;

    public static ProjectDocumentV2 Create(RomImage source) => new(
        CurrentFormatVersion,
        new ProjectSource(source.Profile.Id, source.Sha1, source.Sha256, source.SourcePath),
        new Dictionary<string, LevelDocument>(StringComparer.Ordinal),
        new EditorState());

    public ProjectDocumentV2 WithArea(LevelDocument document)
    {
        var areas = new Dictionary<string, LevelDocument>(ModifiedAreas, StringComparer.Ordinal)
        {
            [document.AreaId] = document
        };
        return this with { ModifiedAreas = areas, EditorState = EditorState with { LastAreaId = document.AreaId } };
    }
}
