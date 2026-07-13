namespace Smb3Editor.Core;

/// <summary>Resolves the visible portable workspace, with a safe per-user fallback.</summary>
public static class WorkspacePaths
{
    private static string? _rootDirectory;

    public static string RootDirectory => _rootDirectory ?? LocalRootDirectory;
    public static string DataDirectory => Path.Combine(RootDirectory, "Data");
    public static string RomsDirectory => Path.Combine(RootDirectory, "ROMs");
    public static string EmulatorsDirectory => Path.Combine(RootDirectory, "Emulators");
    public static string ProjectsDirectory => Path.Combine(RootDirectory, "Projects");
    public static string ExportsDirectory => Path.Combine(RootDirectory, "Exports");
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static string PaletteLibraryPath => Path.Combine(DataDirectory, "palettes.json");
    public static string PlaytestDirectory => Path.Combine(DataDirectory, "Playtest");

    private static string LocalRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarpWhistle");

    public static bool Configure(string applicationDirectory)
    {
        try
        {
            var root = Path.GetFullPath(applicationDirectory);
            Directory.CreateDirectory(Path.Combine(root, "Data"));
            var probe = Path.Combine(root, "Data", $".write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            _rootDirectory = root;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _rootDirectory = LocalRootDirectory;
            Directory.CreateDirectory(DataDirectory);
            return false;
        }
    }
}
