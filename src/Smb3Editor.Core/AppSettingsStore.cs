using System.Text.Json;

namespace Smb3Editor.Core;

public sealed record AppSettingsV1(
    int FormatVersion = 1,
    string? LastRomPath = null,
    string? EmulatorPath = null,
    IReadOnlyList<string>? EmulatorArguments = null,
    string PlayMode = "rom")
{
    public const int CurrentFormatVersion = 1;
}

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpWhistle",
        "settings.json");

    public static OperationResult<AppSettingsV1> Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
            {
                return OperationResult<AppSettingsV1>.Success(new AppSettingsV1());
            }

            var settings = JsonSerializer.Deserialize<AppSettingsV1>(File.ReadAllBytes(path), Options);
            if (settings is null || settings.FormatVersion != AppSettingsV1.CurrentFormatVersion)
            {
                return OperationResult<AppSettingsV1>.Failure(
                    Diagnostics.Error("SETTINGS_VERSION", "The local editor settings file has an unsupported format."));
            }

            return OperationResult<AppSettingsV1>.Success(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return OperationResult<AppSettingsV1>.Failure(
                Diagnostics.Error("SETTINGS_READ", $"Local editor settings could not be read: {ex.Message}"));
        }
    }

    public static OperationResult<string> Save(AppSettingsV1 settings, string? path = null)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
            return AtomicFile.Write(path ?? DefaultPath, bytes, maintainBackup: false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return OperationResult<string>.Failure(
                Diagnostics.Error("SETTINGS_WRITE", $"Local editor settings could not be saved: {ex.Message}"));
        }
    }
}
