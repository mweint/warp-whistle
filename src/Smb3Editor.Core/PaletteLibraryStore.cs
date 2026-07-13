using System.Text.Json;

namespace Smb3Editor.Core;

public sealed record SavedPalette(string Name, bool Objects, IReadOnlyList<byte> Colors, bool IsBuiltIn = false)
{
    public override string ToString() => Name;
}

public static class PaletteLibraryStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultPath => WorkspacePaths.PaletteLibraryPath;

    public static OperationResult<IReadOnlyList<SavedPalette>> Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return OperationResult<IReadOnlyList<SavedPalette>>.Success(StarterPalettes);
            var palettes = JsonSerializer.Deserialize<List<SavedPalette>>(File.ReadAllBytes(path), Options) ?? [];
            return OperationResult<IReadOnlyList<SavedPalette>>.Success(MergeWithStarters(palettes));
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
            var json = JsonSerializer.SerializeToUtf8Bytes(palettes.Where(static palette => !palette.IsBuiltIn), Options);
            return AtomicFile.Write(path, json, maintainBackup: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return OperationResult<string>.Failure(
                Diagnostics.Error("PALETTE_LIBRARY_WRITE", $"The palette library could not be saved: {ex.Message}"));
        }
    }

    // These are editor-library examples only. Applying one still requires the designer to
    // choose one of the stock ROM slots it will replace.
    private static readonly IReadOnlyList<SavedPalette> StarterPalettes =
    [
        new("Forest Dusk", false, [0x0F, 0x01, 0x11, 0x21, 0x0F, 0x06, 0x16, 0x26, 0x0F, 0x09, 0x19, 0x29, 0x0F, 0x0C, 0x1C, 0x2C], true),
        new("Desert Sunset", false, [0x0F, 0x07, 0x17, 0x27, 0x0F, 0x08, 0x18, 0x28, 0x0F, 0x09, 0x19, 0x39, 0x0F, 0x0A, 0x1A, 0x2A], true),
        new("Moonlit Water", false, [0x0F, 0x01, 0x11, 0x31, 0x0F, 0x02, 0x12, 0x22, 0x0F, 0x03, 0x13, 0x23, 0x0F, 0x0C, 0x1C, 0x2C], true),
        new("Snowy Morning", false, [0x0F, 0x10, 0x20, 0x30, 0x0F, 0x11, 0x21, 0x31, 0x0F, 0x12, 0x22, 0x32, 0x0F, 0x1C, 0x2C, 0x3C], true),
        new("Volcanic Night", false, [0x0F, 0x05, 0x15, 0x25, 0x0F, 0x06, 0x16, 0x26, 0x0F, 0x07, 0x17, 0x27, 0x0F, 0x08, 0x18, 0x28], true),
        new("Candy Sky", false, [0x0F, 0x14, 0x24, 0x34, 0x0F, 0x15, 0x25, 0x35, 0x0F, 0x16, 0x26, 0x36, 0x0F, 0x18, 0x28, 0x38], true),
        new("Toad House", true, [0x0F, 0x16, 0x27, 0x38, 0x0F, 0x07, 0x17, 0x27, 0x0F, 0x01, 0x11, 0x21, 0x0F, 0x0C, 0x1C, 0x2C], true),
        new("Underground Glow", true, [0x0F, 0x00, 0x10, 0x20, 0x0F, 0x06, 0x16, 0x26, 0x0F, 0x08, 0x18, 0x28, 0x0F, 0x09, 0x19, 0x29], true),
        new("Koopa Cove", true, [0x0F, 0x02, 0x12, 0x22, 0x0F, 0x03, 0x13, 0x23, 0x0F, 0x09, 0x19, 0x29, 0x0F, 0x0C, 0x1C, 0x2C], true),
        new("Castle Torchlight", true, [0x0F, 0x00, 0x10, 0x30, 0x0F, 0x05, 0x15, 0x25, 0x0F, 0x06, 0x16, 0x36, 0x0F, 0x08, 0x18, 0x38], true)
    ];

    private static IReadOnlyList<SavedPalette> MergeWithStarters(IReadOnlyList<SavedPalette> palettes) =>
        palettes.Concat(StarterPalettes.Where(starter => !palettes.Any(item =>
            item.Objects == starter.Objects && string.Equals(item.Name, starter.Name, StringComparison.OrdinalIgnoreCase)))).ToArray();
}
