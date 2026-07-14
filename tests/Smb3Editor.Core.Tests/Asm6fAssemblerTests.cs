namespace Smb3Editor.Core.Tests;

public sealed class Asm6fAssemblerTests
{
    [Fact]
    public void BundledAssemblerBuildsRaw6502BytesWithoutShowingAConsole()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"warp-whistle-asm6f-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "sample.asm");
            File.WriteAllText(source, "base $e240\nlda #$01\nrts\n");

            var result = new Asm6fAssembler().Assemble(source);

            Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Diagnostics));
            Assert.Equal([0xA9, 0x01, 0x60], result.Value);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
