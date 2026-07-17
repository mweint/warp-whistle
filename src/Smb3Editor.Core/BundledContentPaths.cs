namespace Smb3Editor.Core;

/// <summary>Resolves the one runtime layout shared by development and portable releases.</summary>
public static class BundledContentPaths
{
    public static string RootDirectory => AppContext.BaseDirectory;

    public static string ItemGroupsPath => Path.Combine(RootDirectory, "items.json");
    public static string PatchesDirectory => Path.Combine(RootDirectory, "patches");
    public static string Asm6fPath => ResolveAsm6fPath();

    public static string ResolveAsm6fPath(string? applicationDirectory = null)
    {
        applicationDirectory ??= AppContext.BaseDirectory;
        return Path.Combine(applicationDirectory, "tools", "asm6f", "asm6f_64.exe");
    }
}
