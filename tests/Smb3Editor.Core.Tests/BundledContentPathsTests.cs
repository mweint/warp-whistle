namespace Smb3Editor.Core.Tests;

public sealed class BundledContentPathsTests
{
    [Fact]
    public void Asm6fResolverUsesTheCanonicalRuntimeToolPath()
    {
        var root = CreateTempDirectory();
        try
        {
            var tools = Path.Combine(root, "tools", "asm6f");
            Directory.CreateDirectory(tools);
            var expected = Path.Combine(tools, "asm6f_64.exe");
            File.WriteAllBytes(expected, []);

            Assert.Equal(expected, BundledContentPaths.ResolveAsm6fPath(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"warp-whistle-content-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
