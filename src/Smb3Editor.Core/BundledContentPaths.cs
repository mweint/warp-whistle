namespace Smb3Editor.Core;

/// <summary>Resolves bundled read-only content for both development and portable releases.</summary>
public static class BundledContentPaths
{
    public static string RootDirectory
    {
        get
        {
            var packaged = Path.Combine(AppContext.BaseDirectory, "Resources");
            return Directory.Exists(packaged) ? packaged : AppContext.BaseDirectory;
        }
    }

    public static string ItemGroupsPath => Path.Combine(RootDirectory, "items.json");
    public static string PatchesDirectory => Path.Combine(RootDirectory, "patches");
    public static string Asm6fPath => Path.Combine(RootDirectory, "asm6f_64.exe");
}
