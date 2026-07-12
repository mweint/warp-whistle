using System.Text.Json;

namespace Smb3Editor.Core;

public sealed record SavedPalette(string Name, bool Objects, IReadOnlyList<byte> Colors);

public static class PaletteLibraryStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WarpWhistle", "palettes.json");

    public static OperationResult<IReadOnlyList<SavedPalette>> Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return OperationResult<IReadOnlyList<SavedPalette>>.Success([]);
            var palettes = JsonSerializer.Deserialize<List<SavedPalette>>(File.ReadAllBytes(path), Options) ?? [];
            return OperationResult<IReadOnlyList<SavedPalette>>.Success(palettes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return OperationResult<IReadOnlyList<SavedPalette>>.Failure(
                Diagnostics.Error("PALETTE_LIBRARY_READ", $"The palette library could not be read: {ex.Message}"));
        }
    }

    public static OperationResult<string> Save(IReadOnlyList<SavedPalette> palettes, string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            var json = JsonSerializer.SerializeToUtf8Bytes(palettes, Options);
            return AtomicFile.Write(path, json, maintainBackup: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return OperationResult<string>.Failure(
                Diagnostics.Error("PALETTE_LIBRARY_WRITE", $"The palette library could not be saved: {ex.Message}"));
        }
    }
}
