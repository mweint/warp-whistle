namespace Smb3Editor.Core.Tests;

public sealed class Cpu6502SandboxTests
{
    [Fact]
    public void RunsIsolatedProgramAndCapturesOutput()
    {
        byte[] program =
        [
            0xA9, 0x2A,       // LDA #$2A
            0x8D, 0x00, 0x60, // STA $6000
            0xE8,             // INX
            0x00              // host-safe halt
        ];
        var plan = new GeneratorExecutionPlan(0x8000, 0xFFFF, 100, 0x6000, 1, new Dictionary<ushort, byte>());

        var result = Smb3GeneratorSandbox.Execute(program, 0x8000, plan);

        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x2A }, result.Value);
    }

    [Fact]
    public void InfiniteLoopStopsAtInstructionLimit()
    {
        var cpu = new Cpu6502Sandbox();
        cpu.Load(0x8000, [0x4C, 0x00, 0x80]);

        var result = cpu.Run(0x8000, 25);

        Assert.False(result.Halted);
        Assert.Equal(25, result.Instructions);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "CPU_LIMIT");
    }
}

