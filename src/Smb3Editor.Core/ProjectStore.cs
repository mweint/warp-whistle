using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Smb3Editor.Core;

public static class ProjectStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static OperationResult<string> Save(ProjectDocumentV2 project, string path)
    {
        try
        {
            if (project.FormatVersion != ProjectDocumentV2.CurrentFormatVersion)
            {
                return OperationResult<string>.Failure(
                    Diagnostics.Error("PROJECT_VERSION", $"Only project format {ProjectDocumentV2.CurrentFormatVersion} can be saved."));
            }

            var json = JsonSerializer.SerializeToUtf8Bytes(project, Options);
            return AtomicFile.Write(path, json, maintainBackup: true);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return OperationResult<string>.Failure(
                Diagnostics.Error("PROJECT_SERIALIZE", $"The project could not be serialized: {ex.Message}"));
        }
    }

    public static OperationResult<ProjectDocumentV2> Load(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var root = JsonNode.Parse(bytes)?.AsObject();
            if (root is not null && root["patches"] is null && root["addOns"] is JsonNode legacyPatches)
            {
                root["patches"] = legacyPatches.DeepClone();
                root.Remove("addOns");
                bytes = JsonSerializer.SerializeToUtf8Bytes(root, Options);
            }
            var project = JsonSerializer.Deserialize<ProjectDocumentV2>(bytes, Options);
            if (project is null)
            {
                return OperationResult<ProjectDocumentV2>.Failure(
                    Diagnostics.Error("PROJECT_EMPTY", "The project file contains no project document."));
            }

            var diagnostics = new List<Diagnostic>();
            if (project.FormatVersion == 1)
            {
                project = MigrateV1(project, diagnostics);
            }
            if (project.FormatVersion == 2)
            {
                project = MigrateV2(project, diagnostics);
            }
            if (project.FormatVersion == 3)
            {
                project = MigrateV3(project, diagnostics);
            }
            if (project.FormatVersion == 4)
            {
                project = MigrateV4(project, diagnostics);
            }
            if (project.FormatVersion == 5)
            {
                project = MigrateV5(project, diagnostics);
            }
            if (project.FormatVersion == 6)
            {
                project = MigrateV6(project, diagnostics);
            }
            if (project.FormatVersion == 7)
            {
                project = MigrateV7(project, diagnostics);
            }
            if (project.FormatVersion == 8)
            {
                project = MigrateV8(project, diagnostics);
            }
            if (project.FormatVersion == 9)
            {
                project = MigrateV9(project, diagnostics);
            }
            if (project.FormatVersion == 10)
            {
                project = MigrateV10(project, diagnostics);
            }
            if (project.FormatVersion == 11)
            {
                project = MigrateV11(project, diagnostics);
            }
            if (project.FormatVersion != ProjectDocumentV2.CurrentFormatVersion)
            {
                return OperationResult<ProjectDocumentV2>.Failure(
                    Diagnostics.Error("PROJECT_VERSION", $"Project format {project.FormatVersion} is not supported."));
            }

            if (Smb3Profiles.FindById(project.Source.ProfileId) is null)
            {
                return OperationResult<ProjectDocumentV2>.Failure(
                    Diagnostics.Error("PROJECT_PROFILE", $"ROM profile '{project.Source.ProfileId}' is not supported."));
            }

            return OperationResult<ProjectDocumentV2>.Success(project, diagnostics);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return OperationResult<ProjectDocumentV2>.Failure(
                Diagnostics.Error("PROJECT_READ", $"The project could not be read: {ex.Message}"));
        }
    }

    private static ProjectDocumentV2 MigrateV1(ProjectDocumentV2 legacy, List<Diagnostic> diagnostics)
    {
        var ambiguousPositions = 0;
        var areas = new Dictionary<string, LevelDocument>(StringComparer.Ordinal);
        foreach (var pair in legacy.ModifiedAreas)
        {
            var document = pair.Value;
            var elements = document.Elements.Select(element =>
            {
                if (document.Header.IsVertical || element.Kind == LevelElementKind.Junction)
                {
                    return element;
                }

                var correctedOriginalY = element.OriginalFirstByte & 0x1F;
                var correctedY = element.Y;
                if (correctedOriginalY != element.OriginalY)
                {
                    if (element.Y == element.OriginalY)
                    {
                        correctedY = correctedOriginalY;
                    }
                    else
                    {
                        ambiguousPositions++;
                    }
                }

                return element with { Y = correctedY, OriginalY = correctedOriginalY };
            }).ToArray();

            var enemies = document.Enemies.Select(enemy =>
            {
                var originalSecond = enemy.OriginalSecondByte ?? (byte)enemy.X;
                var originalThird = enemy.OriginalThirdByte ?? (byte)(enemy.Flags | (enemy.Y & 0x1F));
                if (document.Header.IsVertical)
                {
                    var x = originalSecond & 0x0F;
                    var y = ((originalThird >> 4) * 15) + (originalThird & 0x0F);
                    return enemy with
                    {
                        X = x,
                        Y = y,
                        Flags = 0,
                        OriginalSecondByte = originalSecond,
                        OriginalThirdByte = originalThird,
                        OriginalX = x,
                        OriginalY = y
                    };
                }

                return enemy with
                {
                    OriginalSecondByte = originalSecond,
                    OriginalThirdByte = originalThird,
                    OriginalX = enemy.X,
                    OriginalY = enemy.Y
                };
            }).ToArray();

            areas[pair.Key] = document with { Elements = elements, Enemies = enemies };
        }

        diagnostics.Add(Diagnostics.Info("PROJECT_MIGRATED", "Migrated project format 1 to format 2 with corrected packed coordinates."));
        if (ambiguousPositions > 0)
        {
            diagnostics.Add(Diagnostics.Warning(
                "PROJECT_POSITION_AMBIGUOUS",
                $"{ambiguousPositions} previously moved lower-half object position(s) could not be inferred safely; their current visible rows were retained."));
        }

        return legacy with
        {
            FormatVersion = 2,
            ModifiedAreas = areas
        };
    }

    private static ProjectDocumentV2 MigrateV2(ProjectDocumentV2 legacy, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(Diagnostics.Info("PROJECT_MIGRATED", "Migrated project format 2 to format 3 with project-only palette names."));
        return legacy with { FormatVersion = 3, PaletteSlotLabels = [] };
    }

    private static ProjectDocumentV2 MigrateV3(ProjectDocumentV2 legacy, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(Diagnostics.Info("PROJECT_MIGRATED", "Migrated project format 3 to format 4 with opt-in patch settings."));
        return legacy with { FormatVersion = 4, Patches = PatchSettings.None };
    }

    private static ProjectDocumentV2 MigrateV4(ProjectDocumentV2 legacy, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(Diagnostics.Info("PROJECT_MIGRATED", "Migrated project format 4 to format 5 with patch controls."));
        return legacy with { FormatVersion = 5, Patches = legacy.Patches ?? PatchSettings.None };
    }

    private static ProjectDocumentV2 MigrateV5(ProjectDocumentV2 legacy, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(Diagnostics.Info("PROJECT_MIGRATED", "Migrated project format 5 to format 6 with explicit vanilla/enhanced output modes."));
        return legacy with
        {
            FormatVersion = 6,
            OutputMode = RomOutputMode.Vanilla,
            StorageMode = RomStorageMode.FixedSlots
        };
    }

    private static ProjectDocumentV2 MigrateV6(ProjectDocumentV2 legacy, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(Diagnostics.Info("PROJECT_MIGRATED", "Migrated project format 6 to format 7 with project-only overworld tiles."));
        return legacy with { FormatVersion = 7, OverworldTiles = [] };
    }

    private static ProjectDocumentV2 MigrateV7(ProjectDocumentV2 legacy, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(Diagnostics.Info("PROJECT_MIGRATED", "Migrated project format 7 to format 8 with fixed-slot overworld map-node edits."));
        return legacy with { FormatVersion = 8, OverworldLevelPointers = [] };
    }

    private static ProjectDocumentV2 MigrateV8(ProjectDocumentV2 legacy, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(Diagnostics.Info("PROJECT_MIGRATED", "Migrated project format 8 to format 9 with overworld lock and bridge edits."));
        return legacy with { FormatVersion = 9, OverworldLocksAndBridges = [] };
    }

    private static ProjectDocumentV2 MigrateV9(ProjectDocumentV2 legacy, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(Diagnostics.Info("PROJECT_MIGRATED", "Migrated project format 9 to format 10 with shared overworld palettes."));
        return legacy with { FormatVersion = 10, OverworldPalettes = [] };
    }

    private static ProjectDocumentV2 MigrateV10(ProjectDocumentV2 legacy, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(Diagnostics.Info("PROJECT_MIGRATED", "Migrated project format 10 to format 11 with rebuildable vanilla overworld node sets."));
        return legacy with { FormatVersion = 11, OverworldNodeSets = [] };
    }

    private static ProjectDocumentV2 MigrateV11(ProjectDocumentV2 legacy, List<Diagnostic> diagnostics)
    {
        diagnostics.Add(Diagnostics.Info("PROJECT_MIGRATED", "Migrated project format 11 to format 12 with vanilla overworld map sprites and transferable map pages."));
        return legacy with { FormatVersion = 12, OverworldMapSprites = [] };
    }

}
