namespace Smb3Editor.Core;

public sealed record Cpu6502RunResult(
    bool Halted,
    int Instructions,
    ushort ProgramCounter,
    byte Accumulator,
    byte X,
    byte Y,
    IReadOnlyList<Diagnostic> Diagnostics);

public sealed class Cpu6502Sandbox
{
    private const byte Carry = 1 << 0;
    private const byte Zero = 1 << 1;
    private const byte Interrupt = 1 << 2;
    private const byte Decimal = 1 << 3;
    private const byte Break = 1 << 4;
    private const byte Unused = 1 << 5;
    private const byte Overflow = 1 << 6;
    private const byte Negative = 1 << 7;

    private readonly byte[] _memory = new byte[65_536];

    public byte A { get; set; }
    public byte X { get; set; }
    public byte Y { get; set; }
    public byte StackPointer { get; set; } = 0xFD;
    public byte Status { get; set; } = Interrupt | Unused;
    public ushort ProgramCounter { get; set; }
    public Action<ushort, byte>? MemoryWriteObserver { get; set; }

    public Span<byte> Memory => _memory;

    public void Reset()
    {
        Array.Clear(_memory);
        A = 0;
        X = 0;
        Y = 0;
        StackPointer = 0xFD;
        Status = Interrupt | Unused;
        ProgramCounter = 0;
    }

    public void Load(ushort address, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > _memory.Length - address)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        bytes.CopyTo(_memory.AsSpan(address));
    }

    public Cpu6502RunResult Run(ushort entryPoint, int instructionLimit, ushort? stopAddress = null)
    {
        if (instructionLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(instructionLimit));
        }

        ProgramCounter = entryPoint;
        var diagnostics = new List<Diagnostic>();
        for (var instructions = 0; instructions < instructionLimit; instructions++)
        {
            if (stopAddress == ProgramCounter)
            {
                return Result(true, instructions, diagnostics);
            }

            var opcodeAddress = ProgramCounter;
            var opcode = Fetch();
            if (!Execute(opcode))
            {
                if (opcode == 0x00)
                {
                    return Result(true, instructions + 1, diagnostics);
                }

                diagnostics.Add(Diagnostics.Error("CPU_OPCODE", $"Unsupported 6502 opcode ${opcode:X2} at ${opcodeAddress:X4}."));
                return Result(false, instructions + 1, diagnostics);
            }
        }

        diagnostics.Add(Diagnostics.Error("CPU_LIMIT", $"The sandbox stopped after {instructionLimit:N0} instructions."));
        return Result(false, instructionLimit, diagnostics);
    }

    private bool Execute(byte opcode)
    {
        switch (opcode)
        {
            case 0x00: return false; // BRK is a safe host halt.
            case 0xEA: return true;

            // Loads
            case 0xA9: A = Fetch(); SetNz(A); return true;
            case 0xA5: A = Read(Zp()); SetNz(A); return true;
            case 0xB5: A = Read(Zpx()); SetNz(A); return true;
            case 0xAD: A = Read(Abs()); SetNz(A); return true;
            case 0xBD: A = Read(AbsX()); SetNz(A); return true;
            case 0xB9: A = Read(AbsY()); SetNz(A); return true;
            case 0xA1: A = Read(IndirectX()); SetNz(A); return true;
            case 0xB1: A = Read(IndirectY()); SetNz(A); return true;
            case 0xA2: X = Fetch(); SetNz(X); return true;
            case 0xA6: X = Read(Zp()); SetNz(X); return true;
            case 0xB6: X = Read(Zpy()); SetNz(X); return true;
            case 0xAE: X = Read(Abs()); SetNz(X); return true;
            case 0xBE: X = Read(AbsY()); SetNz(X); return true;
            case 0xA0: Y = Fetch(); SetNz(Y); return true;
            case 0xA4: Y = Read(Zp()); SetNz(Y); return true;
            case 0xB4: Y = Read(Zpx()); SetNz(Y); return true;
            case 0xAC: Y = Read(Abs()); SetNz(Y); return true;
            case 0xBC: Y = Read(AbsX()); SetNz(Y); return true;

            // Stores
            case 0x85: Write(Zp(), A); return true;
            case 0x95: Write(Zpx(), A); return true;
            case 0x8D: Write(Abs(), A); return true;
            case 0x9D: Write(AbsX(), A); return true;
            case 0x99: Write(AbsY(), A); return true;
            case 0x81: Write(IndirectX(), A); return true;
            case 0x91: Write(IndirectY(), A); return true;
            case 0x86: Write(Zp(), X); return true;
            case 0x96: Write(Zpy(), X); return true;
            case 0x8E: Write(Abs(), X); return true;
            case 0x9E: Write(AbsY(), X); return true;
            case 0x84: Write(Zp(), Y); return true;
            case 0x94: Write(Zpx(), Y); return true;
            case 0x8C: Write(Abs(), Y); return true;
            case 0x9C: Write(AbsX(), Y); return true;

            // Transfers and stack
            case 0xAA: X = A; SetNz(X); return true;
            case 0xA8: Y = A; SetNz(Y); return true;
            case 0x8A: A = X; SetNz(A); return true;
            case 0x98: A = Y; SetNz(A); return true;
            case 0xBA: X = StackPointer; SetNz(X); return true;
            case 0x9A: StackPointer = X; return true;
            case 0x48: Push(A); return true;
            case 0x68: A = Pop(); SetNz(A); return true;
            case 0x08: Push((byte)(Status | Break | Unused)); return true;
            case 0x28: Status = (byte)((Pop() & ~Break) | Unused); return true;

            // Register increments/decrements
            case 0xE8: X++; SetNz(X); return true;
            case 0xC8: Y++; SetNz(Y); return true;
            case 0xCA: X--; SetNz(X); return true;
            case 0x88: Y--; SetNz(Y); return true;

            // Memory increments/decrements
            case 0xE6: Modify(Zp(), static value => (byte)(value + 1)); return true;
            case 0xF6: Modify(Zpx(), static value => (byte)(value + 1)); return true;
            case 0xEE: Modify(Abs(), static value => (byte)(value + 1)); return true;
            case 0xFE: Modify(AbsX(), static value => (byte)(value + 1)); return true;
            case 0xC6: Modify(Zp(), static value => (byte)(value - 1)); return true;
            case 0xD6: Modify(Zpx(), static value => (byte)(value - 1)); return true;
            case 0xCE: Modify(Abs(), static value => (byte)(value - 1)); return true;
            case 0xDE: Modify(AbsX(), static value => (byte)(value - 1)); return true;

            // Boolean operations
            case 0x29: A &= Fetch(); SetNz(A); return true;
            case 0x25: A &= Read(Zp()); SetNz(A); return true;
            case 0x35: A &= Read(Zpx()); SetNz(A); return true;
            case 0x2D: A &= Read(Abs()); SetNz(A); return true;
            case 0x3D: A &= Read(AbsX()); SetNz(A); return true;
            case 0x39: A &= Read(AbsY()); SetNz(A); return true;
            case 0x21: A &= Read(IndirectX()); SetNz(A); return true;
            case 0x31: A &= Read(IndirectY()); SetNz(A); return true;
            case 0x09: A |= Fetch(); SetNz(A); return true;
            case 0x05: A |= Read(Zp()); SetNz(A); return true;
            case 0x15: A |= Read(Zpx()); SetNz(A); return true;
            case 0x0D: A |= Read(Abs()); SetNz(A); return true;
            case 0x1D: A |= Read(AbsX()); SetNz(A); return true;
            case 0x19: A |= Read(AbsY()); SetNz(A); return true;
            case 0x01: A |= Read(IndirectX()); SetNz(A); return true;
            case 0x11: A |= Read(IndirectY()); SetNz(A); return true;
            case 0x49: A ^= Fetch(); SetNz(A); return true;
            case 0x45: A ^= Read(Zp()); SetNz(A); return true;
            case 0x55: A ^= Read(Zpx()); SetNz(A); return true;
            case 0x4D: A ^= Read(Abs()); SetNz(A); return true;
            case 0x5D: A ^= Read(AbsX()); SetNz(A); return true;
            case 0x59: A ^= Read(AbsY()); SetNz(A); return true;
            case 0x41: A ^= Read(IndirectX()); SetNz(A); return true;
            case 0x51: A ^= Read(IndirectY()); SetNz(A); return true;
            case 0x24: Bit(Read(Zp())); return true;
            case 0x2C: Bit(Read(Abs())); return true;

            // Arithmetic and comparisons
            case 0x69: Adc(Fetch()); return true;
            case 0x65: Adc(Read(Zp())); return true;
            case 0x75: Adc(Read(Zpx())); return true;
            case 0x6D: Adc(Read(Abs())); return true;
            case 0x7D: Adc(Read(AbsX())); return true;
            case 0x79: Adc(Read(AbsY())); return true;
            case 0x61: Adc(Read(IndirectX())); return true;
            case 0x71: Adc(Read(IndirectY())); return true;
            case 0xE9: Sbc(Fetch()); return true;
            case 0xEB: Sbc(Fetch()); return true;
            case 0xE5: Sbc(Read(Zp())); return true;
            case 0xF5: Sbc(Read(Zpx())); return true;
            case 0xED: Sbc(Read(Abs())); return true;
            case 0xFD: Sbc(Read(AbsX())); return true;
            case 0xF9: Sbc(Read(AbsY())); return true;
            case 0xE1: Sbc(Read(IndirectX())); return true;
            case 0xF1: Sbc(Read(IndirectY())); return true;
            case 0xC9: Compare(A, Fetch()); return true;
            case 0xC5: Compare(A, Read(Zp())); return true;
            case 0xD5: Compare(A, Read(Zpx())); return true;
            case 0xCD: Compare(A, Read(Abs())); return true;
            case 0xDD: Compare(A, Read(AbsX())); return true;
            case 0xD9: Compare(A, Read(AbsY())); return true;
            case 0xC1: Compare(A, Read(IndirectX())); return true;
            case 0xD1: Compare(A, Read(IndirectY())); return true;
            case 0xE0: Compare(X, Fetch()); return true;
            case 0xE4: Compare(X, Read(Zp())); return true;
            case 0xEC: Compare(X, Read(Abs())); return true;
            case 0xC0: Compare(Y, Fetch()); return true;
            case 0xC4: Compare(Y, Read(Zp())); return true;
            case 0xCC: Compare(Y, Read(Abs())); return true;

            // Shifts and rotates
            case 0x0A: A = Asl(A); return true;
            case 0x4A: A = Lsr(A); return true;
            case 0x2A: A = Rol(A); return true;
            case 0x6A: A = Ror(A); return true;
            case 0x06: Modify(Zp(), Asl); return true;
            case 0x16: Modify(Zpx(), Asl); return true;
            case 0x0E: Modify(Abs(), Asl); return true;
            case 0x1E: Modify(AbsX(), Asl); return true;
            case 0x46: Modify(Zp(), Lsr); return true;
            case 0x56: Modify(Zpx(), Lsr); return true;
            case 0x4E: Modify(Abs(), Lsr); return true;
            case 0x5E: Modify(AbsX(), Lsr); return true;
            case 0x26: Modify(Zp(), Rol); return true;
            case 0x36: Modify(Zpx(), Rol); return true;
            case 0x2E: Modify(Abs(), Rol); return true;
            case 0x3E: Modify(AbsX(), Rol); return true;
            case 0x66: Modify(Zp(), Ror); return true;
            case 0x76: Modify(Zpx(), Ror); return true;
            case 0x6E: Modify(Abs(), Ror); return true;
            case 0x7E: Modify(AbsX(), Ror); return true;

            // Branches
            case 0x10: Branch((Status & Negative) == 0); return true;
            case 0x30: Branch((Status & Negative) != 0); return true;
            case 0x50: Branch((Status & Overflow) == 0); return true;
            case 0x70: Branch((Status & Overflow) != 0); return true;
            case 0x90: Branch((Status & Carry) == 0); return true;
            case 0xB0: Branch((Status & Carry) != 0); return true;
            case 0xD0: Branch((Status & Zero) == 0); return true;
            case 0xF0: Branch((Status & Zero) != 0); return true;

            // Jumps and returns
            case 0x4C: ProgramCounter = Abs(); return true;
            case 0x6C: ProgramCounter = ReadWordBug(Abs()); return true;
            case 0x20:
                {
                    var destination = Abs();
                    var returnAddress = (ushort)(ProgramCounter - 1);
                    Push((byte)(returnAddress >> 8));
                    Push((byte)returnAddress);
                    ProgramCounter = destination;
                    return true;
                }
            case 0x60:
                {
                    var low = Pop();
                    var high = Pop();
                    ProgramCounter = (ushort)(((high << 8) | low) + 1);
                    return true;
                }
            case 0x40:
                {
                    Status = (byte)((Pop() & ~Break) | Unused);
                    var low = Pop();
                    var high = Pop();
                    ProgramCounter = (ushort)(low | (high << 8));
                    return true;
                }

            // Flag operations
            case 0x18: Status &= unchecked((byte)~Carry); return true;
            case 0x38: Status |= Carry; return true;
            case 0x58: Status &= unchecked((byte)~Interrupt); return true;
            case 0x78: Status |= Interrupt; return true;
            case 0xB8: Status &= unchecked((byte)~Overflow); return true;
            case 0xD8: Status &= unchecked((byte)~Decimal); return true;
            case 0xF8: Status |= Decimal; return true;
            default: return false;
        }
    }

    private Cpu6502RunResult Result(bool halted, int instructions, IReadOnlyList<Diagnostic> diagnostics) =>
        new(halted, instructions, ProgramCounter, A, X, Y, diagnostics);

    private byte Fetch() => Read(ProgramCounter++);
    private ushort Zp() => Fetch();
    private ushort Zpx() => (byte)(Fetch() + X);
    private ushort Zpy() => (byte)(Fetch() + Y);
    private ushort Abs()
    {
        var low = Fetch();
        return (ushort)(low | (Fetch() << 8));
    }

    private ushort AbsX() => (ushort)(Abs() + X);
    private ushort AbsY() => (ushort)(Abs() + Y);
    private ushort IndirectX() => ReadWordZeroPage((byte)(Fetch() + X));
    private ushort IndirectY() => (ushort)(ReadWordZeroPage(Fetch()) + Y);
    private byte Read(ushort address) => _memory[address];
    private void Write(ushort address, byte value)
    {
        _memory[address] = value;
        MemoryWriteObserver?.Invoke(address, value);
    }

    private ushort ReadWordZeroPage(byte address) =>
        (ushort)(Read(address) | (Read((byte)(address + 1)) << 8));

    private ushort ReadWordBug(ushort address)
    {
        var next = (ushort)((address & 0xFF00) | ((address + 1) & 0x00FF));
        return (ushort)(Read(address) | (Read(next) << 8));
    }

    private void Push(byte value) => Write((ushort)(0x0100 | StackPointer--), value);
    private byte Pop() => Read((ushort)(0x0100 | ++StackPointer));

    private void Modify(ushort address, Func<byte, byte> operation)
    {
        var value = operation(Read(address));
        Write(address, value);
        SetNz(value);
    }

    private void SetNz(byte value)
    {
        Status = value == 0 ? (byte)(Status | Zero) : (byte)(Status & ~Zero);
        Status = (value & 0x80) != 0 ? (byte)(Status | Negative) : (byte)(Status & ~Negative);
    }

    private void Adc(byte value)
    {
        var carry = (Status & Carry) != 0 ? 1 : 0;
        var sum = A + value + carry;
        var result = (byte)sum;
        Status = sum > 0xFF ? (byte)(Status | Carry) : (byte)(Status & ~Carry);
        Status = ((~(A ^ value) & (A ^ result) & 0x80) != 0) ? (byte)(Status | Overflow) : (byte)(Status & ~Overflow);
        A = result;
        SetNz(A);
    }

    private void Sbc(byte value) => Adc((byte)~value);

    private void Compare(byte register, byte value)
    {
        var result = (byte)(register - value);
        Status = register >= value ? (byte)(Status | Carry) : (byte)(Status & ~Carry);
        SetNz(result);
    }

    private void Bit(byte value)
    {
        Status = (A & value) == 0 ? (byte)(Status | Zero) : (byte)(Status & ~Zero);
        Status = (byte)((Status & ~(Negative | Overflow)) | (value & (Negative | Overflow)));
    }

    private byte Asl(byte value)
    {
        Status = (value & 0x80) != 0 ? (byte)(Status | Carry) : (byte)(Status & ~Carry);
        var result = (byte)(value << 1);
        SetNz(result);
        return result;
    }

    private byte Lsr(byte value)
    {
        Status = (value & 1) != 0 ? (byte)(Status | Carry) : (byte)(Status & ~Carry);
        var result = (byte)(value >> 1);
        SetNz(result);
        return result;
    }

    private byte Rol(byte value)
    {
        var carryIn = (Status & Carry) != 0 ? 1 : 0;
        Status = (value & 0x80) != 0 ? (byte)(Status | Carry) : (byte)(Status & ~Carry);
        var result = (byte)((value << 1) | carryIn);
        SetNz(result);
        return result;
    }

    private byte Ror(byte value)
    {
        var carryIn = (Status & Carry) != 0 ? 0x80 : 0;
        Status = (value & 1) != 0 ? (byte)(Status | Carry) : (byte)(Status & ~Carry);
        var result = (byte)((value >> 1) | carryIn);
        SetNz(result);
        return result;
    }

    private void Branch(bool condition)
    {
        var relative = unchecked((sbyte)Fetch());
        if (condition)
        {
            ProgramCounter = (ushort)(ProgramCounter + relative);
        }
    }
}

public sealed record GeneratorExecutionPlan(
    ushort EntryPoint,
    ushort StopAddress,
    int InstructionLimit,
    ushort TileBufferAddress,
    int TileBufferLength,
    IReadOnlyDictionary<ushort, byte> InitialMemory);

public static class Smb3GeneratorSandbox
{
    public static OperationResult<byte[]> Execute(ReadOnlySpan<byte> mappedProgram, ushort mappedAddress, GeneratorExecutionPlan plan)
    {
        try
        {
            if (plan.TileBufferLength < 0 || plan.TileBufferAddress > 65_536 - plan.TileBufferLength)
            {
                return OperationResult<byte[]>.Failure(Diagnostics.Error("SANDBOX_BUFFER", "The tile output buffer lies outside isolated 6502 memory."));
            }

            var cpu = new Cpu6502Sandbox();
            cpu.Load(mappedAddress, mappedProgram);
            foreach (var pair in plan.InitialMemory)
            {
                cpu.Memory[pair.Key] = pair.Value;
            }

            var run = cpu.Run(plan.EntryPoint, plan.InstructionLimit, plan.StopAddress);
            if (!run.Halted || run.Diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                return OperationResult<byte[]>.Failure(run.Diagnostics.ToArray());
            }

            var output = cpu.Memory.Slice(plan.TileBufferAddress, plan.TileBufferLength).ToArray();
            return OperationResult<byte[]>.Success(output, [Diagnostics.Info("SANDBOX_OK", $"Level generation completed in {run.Instructions:N0} instructions.")]);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or OverflowException)
        {
            return OperationResult<byte[]>.Failure(Diagnostics.Error("SANDBOX_SETUP", $"The isolated 6502 execution plan is invalid: {ex.Message}"));
        }
    }
}
