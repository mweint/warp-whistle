namespace Smb3Editor.Core.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class ProjectStoreTests
{
    [Fact]
    public void ProjectRoundTripsWithoutEmbeddingRomBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smb3-editor-{Guid.NewGuid():N}.smb3proj");
        var project = new ProjectDocumentV2(
            ProjectDocumentV2.CurrentFormatVersion,
            new ProjectSource("us-prg1", "abc", "def", "C:/owned/game.nes"),
            new Dictionary<string, LevelDocument>(),
            new EditorState(Zoom: 1.1));

        try
        {
            var saved = ProjectStore.Save(project, path);
            var loaded = ProjectStore.Load(path);

            Assert.True(saved.IsSuccess);
            Assert.True(loaded.IsSuccess);
            Assert.Equal(project.Source, loaded.Value!.Source);
            Assert.Equal(1.1, loaded.Value.EditorState.Zoom);
            Assert.DoesNotContain("romBytes", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    [Fact]
    public void VersionOneProjectMigratesPackedCoordinatesAndPaletteNamesToCurrentVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smb3-editor-v1-{Guid.NewGuid():N}.smb3proj");
        var horizontal = CreateDocument("horizontal", vertical: false) with
        {
            Elements =
            [
                new LevelElement(0, LevelElementKind.FixedGenerator, 0x21, 5, 2, 0x01, null, 0x12, 0x05, 5, 2)
            ]
        };
        var vertical = CreateDocument("vertical", vertical: true) with
        {
            Enemies = [new EnemyElement(0, 0x72, 9, 7, 0x20)]
        };
        var legacy = new ProjectDocumentV2(
            1,
            new ProjectSource("us-prg1", "abc", "def", "C:/owned/game.nes"),
            new Dictionary<string, LevelDocument>
            {
                [horizontal.AreaId] = horizontal,
                [vertical.AreaId] = vertical
            },
            new EditorState(Zoom: 3));

        try
        {
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(legacy, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            var loaded = ProjectStore.Load(path);

            Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
            Assert.Equal(ProjectDocumentV2.CurrentFormatVersion, loaded.Value!.FormatVersion);
            Assert.Empty(loaded.Value.PaletteSlotLabels ?? []);
        Assert.False((loaded.Value.Patches ?? PatchSettings.None).HasEnabledOptions(["W1-1"]));
            Assert.Equal(18, loaded.Value.ModifiedAreas[horizontal.AreaId].Elements[0].Y);
            Assert.Equal(18, loaded.Value.ModifiedAreas[horizontal.AreaId].Elements[0].OriginalY);
            var enemy = loaded.Value.ModifiedAreas[vertical.AreaId].Enemies[0];
            Assert.Equal(9, enemy.X);
            Assert.Equal(37, enemy.Y);
            Assert.Equal((byte)0, enemy.Flags);
            Assert.Equal((byte)0x27, enemy.OriginalThirdByte);
            Assert.Contains(loaded.Diagnostics, static item => item.Code == "PROJECT_MIGRATED");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LocalSettingsRoundTripRomAndEmulatorPaths()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smb3-editor-settings-{Guid.NewGuid():N}.json");
        try
        {
            var saved = AppSettingsStore.Save(new AppSettingsV1(
                LastRomPath: "C:/owned/game.nes",
                EmulatorPath: "C:/emulators/mesen.exe",
                EmulatorArguments: ["--fullscreen", "{rom}"]), path);
            var loaded = AppSettingsStore.Load(path);

            Assert.True(saved.IsSuccess);
            Assert.True(loaded.IsSuccess);
            Assert.Equal("C:/owned/game.nes", loaded.Value!.LastRomPath);
            Assert.Equal("C:/emulators/mesen.exe", loaded.Value.EmulatorPath);
            Assert.Equal(["--fullscreen", "{rom}"], loaded.Value.EmulatorArguments);
            Assert.DoesNotContain("romBytes", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LegacyAddOnJsonMigratesToPatchesAndNewSavesUsePatches()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smb3-editor-patches-{Guid.NewGuid():N}.smb3proj");
        var project = new ProjectDocumentV2(
            4,
            new ProjectSource("us-prg1", "abc", "def", "C:/owned/game.nes"),
            new Dictionary<string, LevelDocument>(),
            new EditorState(),
            Patches: new PatchSettings(new PatchSetting(EnabledByDefault: true), new PatchSetting()));
        try
        {
            var json = JsonNode.Parse(JsonSerializer.Serialize(project, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))!.AsObject();
            json["addOns"] = json["patches"]!.DeepClone();
            json.Remove("patches");
            File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var loaded = ProjectStore.Load(path);
            Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
            Assert.True((loaded.Value!.Patches ?? PatchSettings.None).QuickRetry!.EnabledByDefault);
            Assert.Contains(loaded.Diagnostics, item => item.Code == "PROJECT_MIGRATED");

            var saved = ProjectStore.Save(loaded.Value, path);
            Assert.True(saved.IsSuccess);
            var output = File.ReadAllText(path);
            Assert.Contains("\"patches\"", output, StringComparison.Ordinal);
            Assert.DoesNotContain("\"addOns\"", output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".bak");
        }
    }

    private static LevelDocument CreateDocument(string id, bool vertical) => new(
        id,
        id,
        vertical ? 8 : 1,
        new LevelHeader(0, 0, 0x07, 0, (byte)(vertical ? 0x18 : 0x01), 0, 0),
        [],
        1,
        [],
        10,
        2,
        [id]);
}
