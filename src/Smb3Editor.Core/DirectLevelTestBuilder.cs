using System.Security.Cryptography;

namespace Smb3Editor.Core;

/// <summary>
/// Creates a disposable PRG1-only test image. The startup harness is never used
/// by normal compilation, ROM export, or BPS export.
/// </summary>
public sealed record DirectLevelTestArtifact(
    byte[] RomBytes,
    string AreaId,
    string Sha256);

public interface IDirectLevelTestBuilder
{
    OperationResult<DirectLevelTestArtifact> Build(ProjectDocumentV2 project, RomImage source, LevelLocation selectedLevel);
    OperationResult<DirectLevelTestArtifact> Build(BuildArtifact compiledRom, RomImage source, LevelLocation selectedLevel);
    OperationResult<string> VerifyReadback(DirectLevelTestArtifact artifact, ReadOnlySpan<byte> bytes);
}

public sealed class DirectLevelTestBuilder : IDirectLevelTestBuilder
{
    // PRG1 sites verified against the byte-perfect Southbird disassembly.
    // $E911 is the verified temporary fixed-bank area. When patches are
    // enabled it contains the retry entry stub; Play Level owns this area
    // only in its disposable test ROM, never in normal exports.
    private const int TitleEntryOffset = 0x3C4AD;
    private const int PrepareLevelCallOffset = 0x3C937;
    private const int RestartExitOffset = 0x3CF9E;
    private const int AutoScrollCallOffset = 0x3CF3E;
    private const int HarnessOffset = 0x3E921;
    private const int TestAutoScrollWrapperOffset = 0x3DF20;
    // The entry stub
    // reproduces the stock map-to-level transition immediately before $88C8;
    // entering $88C8 directly leaves the title-screen NMI handler active.
    private const ushort EntryStubAddress = 0xE911;
    private const ushort PrepareHarnessAddress = 0xE932;
    private const ushort TestAutoScrollWrapperAddress = 0x9F10;
    private const int AutoScrollWrapperLength = 35;
    private const ushort PatchRuntimeAddress = 0xE240;
    private const ushort PatchRuntimeEnd = 0xE2BF;
    private const ushort LevelPreparationEntry = 0x88C8;
    private const int HarnessCapacity = 111;
    private static readonly byte[] TitleEntryExpected = [0x20, 0xAF, 0xA8];
    private static readonly byte[] PrepareLevelExpected = [0x20, 0xFF, 0xB0];
    private static readonly byte[] RestartExitExpected = [0xAE, 0x26, 0x07];
    private readonly IRomCompiler _compiler;

    public DirectLevelTestBuilder(IRomCompiler? compiler = null) => _compiler = compiler ?? new RomCompiler();

    public OperationResult<DirectLevelTestArtifact> Build(ProjectDocumentV2 project, RomImage source, LevelLocation selectedLevel)
    {
        var compiled = _compiler.Compile(project, source);
        if (!compiled.IsSuccess)
        {
            return OperationResult<DirectLevelTestArtifact>.Failure(compiled.Diagnostics.ToArray());
        }

        var directTest = Build(compiled.Value!, source, selectedLevel);
        if (!directTest.IsSuccess || (!(project.Patches ?? PatchSettings.None).HasEnabledOptions(source.Profile.Levels.Keys) && (project.ExternalPatches?.Count ?? 0) == 0))
        {
            return directTest;
        }

        return OperationResult<DirectLevelTestArtifact>.Success(
            directTest.Value!,
            directTest.Diagnostics.Append(Diagnostics.Warning(
                "PLAY_LEVEL_PATCH_SCOPE",
                "Play Level uses its own disposable restart harness. Test executable patches with Play ROM.")));
    }

    public OperationResult<DirectLevelTestArtifact> Build(BuildArtifact compiledRom, RomImage source, LevelLocation selectedLevel)
    {
        var diagnostics = new List<Diagnostic>(compiledRom.Diagnostics);
        if (!string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal))
        {
            return OperationResult<DirectLevelTestArtifact>.Failure(
                diagnostics.Append(Diagnostics.Error("PLAY_LEVEL_PROFILE", "Play Level is currently verified only for Super Mario Bros. 3 (USA, PRG1 / Rev A).")).ToArray());
        }

        if (!source.Profile.Levels.TryGetValue(selectedLevel.AreaId, out var verifiedLevel) ||
            verifiedLevel.LayoutOffset != selectedLevel.LayoutOffset ||
            verifiedLevel.EnemyOffset != selectedLevel.EnemyOffset)
        {
            return OperationResult<DirectLevelTestArtifact>.Failure(
                diagnostics.Append(Diagnostics.Error("PLAY_LEVEL_TARGET", "The selected level is not a verified PRG1 catalog target.")).ToArray());
        }

        if (!TryGetPointers(source, verifiedLevel, out var layoutPointer, out var enemyPointer, out var reason))
        {
            return OperationResult<DirectLevelTestArtifact>.Failure(
                diagnostics.Append(Diagnostics.Error("PLAY_LEVEL_POINTER", reason!)).ToArray());
        }

        var entryStub = CreateEntryStub();
        var prepareHarness = CreatePrepareHarness(verifiedLevel.Tileset, layoutPointer, enemyPointer);
        if (entryStub.Length > PrepareHarnessAddress - EntryStubAddress ||
            PrepareHarnessAddress - EntryStubAddress + prepareHarness.Length > HarnessCapacity)
        {
            return OperationResult<DirectLevelTestArtifact>.Failure(
                diagnostics.Append(Diagnostics.Error("PLAY_LEVEL_HARNESS", "The verified PRG1 test harness exceeded its reserved temporary patch area.")).ToArray());
        }

        var output = compiledRom.RomBytes.ToArray();
        byte[]? autoScrollWrapper = null;
        if (HasJsrIntoRetryArea(output, AutoScrollCallOffset))
        {
            var target = (ushort)(output[AutoScrollCallOffset + 1] | (output[AutoScrollCallOffset + 2] << 8));
            var sourceOffset = 0x3E010 + target - 0xE000;
            autoScrollWrapper = output.AsSpan(sourceOffset, AutoScrollWrapperLength).ToArray();
            if (!output.AsSpan(TestAutoScrollWrapperOffset, AutoScrollWrapperLength).ToArray().All(value => value == 0xFF))
            {
                return OperationResult<DirectLevelTestArtifact>.Failure(
                    diagnostics.Append(Diagnostics.Error("PLAY_LEVEL_HARNESS", "The disposable auto-scroll wrapper area is unavailable.")).ToArray());
            }
        }

        // Play Level owns this complete temporary region. Remove any export-only
        // retry/wrapper bytes before installing the disposable launch harness.
        output.AsSpan(HarnessOffset, HarnessCapacity).Fill(0xFF);
        WriteJump(output, TitleEntryOffset, EntryStubAddress);
        WriteJsr(output, PrepareLevelCallOffset, PrepareHarnessAddress);
        WriteJump(output, RestartExitOffset, EntryStubAddress);
        if (autoScrollWrapper is not null)
        {
            autoScrollWrapper.CopyTo(output, TestAutoScrollWrapperOffset);
            WriteJsr(output, AutoScrollCallOffset, TestAutoScrollWrapperAddress);
        }
        entryStub.CopyTo(output, HarnessOffset);
        prepareHarness.CopyTo(output, HarnessOffset + PrepareHarnessAddress - EntryStubAddress);

        var artifact = new DirectLevelTestArtifact(
            output,
            verifiedLevel.AreaId,
            Convert.ToHexString(SHA256.HashData(output)).ToLowerInvariant());
        var verification = VerifyReadback(artifact, output);
        if (!verification.IsSuccess)
        {
            return OperationResult<DirectLevelTestArtifact>.Failure(diagnostics.Concat(verification.Diagnostics).ToArray());
        }

        diagnostics.Add(Diagnostics.Info("PLAY_LEVEL_READY", $"Built a disposable direct test for {verifiedLevel.DisplayName} as Small Mario."));
        return OperationResult<DirectLevelTestArtifact>.Success(artifact, diagnostics);
    }

    public OperationResult<string> VerifyReadback(DirectLevelTestArtifact artifact, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16 + 262_144 + 131_072 ||
            bytes[0] != (byte)'N' || bytes[1] != (byte)'E' || bytes[2] != (byte)'S' || bytes[3] != 0x1A ||
            bytes[4] != 16 || bytes[5] != 16 || ((bytes[6] >> 4) | (bytes[7] & 0xF0)) != 4)
        {
            return OperationResult<string>.Failure(Diagnostics.Error("PLAY_LEVEL_VERIFY", "The temporary test ROM no longer has the expected PRG1 iNES structure."));
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(hash, artifact.Sha256, StringComparison.Ordinal) ||
            !HasExpectedBytes(bytes, TitleEntryOffset, [0x4C, 0x11, 0xE9]) ||
            !HasExpectedBytes(bytes, PrepareLevelCallOffset, [0x20, 0x32, 0xE9]) ||
            !HasExpectedBytes(bytes, RestartExitOffset, [0x4C, 0x11, 0xE9]) ||
            !HasExpectedBytes(bytes, HarnessOffset, [0xA0, 0x06, 0x20, 0xCE, 0x96]) ||
            !HasExpectedBytes(bytes, HarnessOffset + PrepareHarnessAddress - EntryStubAddress, [0xA9]) ||
            bytes[HarnessOffset + HarnessCapacity - 1] != 0xFF)
        {
            return OperationResult<string>.Failure(Diagnostics.Error("PLAY_LEVEL_VERIFY", "The temporary test ROM did not pass direct-level patch verification."));
        }

        return OperationResult<string>.Success("Temporary direct-level ROM verified.");
    }

    private static bool TryGetPointers(RomImage source, LevelLocation level, out ushort layoutPointer, out ushort enemyPointer, out string? reason)
    {
        layoutPointer = 0;
        enemyPointer = 0;
        reason = null;
        var layoutBank = (level.LayoutOffset - source.PrgOffset) / 0x2000;
        var enemyBank = (level.EnemyOffset - source.PrgOffset) / 0x2000;
        var expectedLayoutBank = level.Tileset switch
        {
            0 => 11, 1 => 15, 2 => 21, 3 => 16, 4 => 17, 5 => 19, 6 => 18, 7 => 18,
            8 => 18, 9 => 20, 10 => 23, 11 => 19, 12 => 17, 13 => 19, 14 => 13, 15 => 26,
            16 => 26, 17 => 26, 18 => 9, _ => -1
        };
        if (expectedLayoutBank < 0 || layoutBank != expectedLayoutBank || enemyBank != 6)
        {
            reason = $"{level.DisplayName} does not use the verified PRG1 direct-test layout/enemy bank arrangement.";
            return false;
        }

        layoutPointer = (ushort)(0xA000 + ((level.LayoutOffset - source.PrgOffset) & 0x1FFF));
        enemyPointer = (ushort)(0xC000 + ((level.EnemyOffset - source.PrgOffset) & 0x1FFF));
        return true;
    }

    private static byte[] CreateEntryStub() =>
    [
        0xA0, 0x06,                   // LDY #$06
        0x20, 0xCE, 0x96,             // JSR Clear_RAM_thru_ZeroPage: remove all death-state RAM
        0xEE, 0x55, 0x79,             // INC UpdSel_Disable: stop title-screen NMI updates
        0xA9, 0x28,                   // LDA #%00101000
        0x8D, 0x00, 0x20,             // STA PPU_CTL1
        0x85, 0xFF,                   // STA PPU_CTL1_Copy
        0xA9, 0x00,                   // LDA #$00
        0x8D, 0x01, 0x20,             // STA PPU_CTL2: hide display during setup
        0xA9, 0x04,                   // LDA #$04
        0x8D, 0xEE, 0x05,             // STA Level_TimerMSD
        0xA9, 0x00,                   // LDA #$00
        0x8D, 0x13, 0x07,             // STA Map_ReturnStatus: clear the death exit before restarting
        0x4C, (byte)(LevelPreparationEntry & 0xFF), (byte)(LevelPreparationEntry >> 8) // JMP $88C8
    ];

    private static byte[] CreatePrepareHarness(int tileset, ushort layoutPointer, ushort enemyPointer)
    {
        var code = new List<byte>();
        void LoadAndStore(byte value, ushort address)
        {
            code.Add(0xA9); code.Add(value);       // LDA #value
            code.Add(0x8D); code.Add((byte)address); code.Add((byte)(address >> 8)); // STA address
        }
        void LoadAndStoreZeroPage(byte value, byte address)
        {
            code.Add(0xA9); code.Add(value);       // LDA #value
            code.Add(0x85); code.Add(address);     // STA address
        }

        LoadAndStore((byte)tileset, 0x070A); // Level_Tileset
        LoadAndStoreZeroPage((byte)layoutPointer, 0x61);
        LoadAndStoreZeroPage((byte)(layoutPointer >> 8), 0x62);
        LoadAndStore((byte)layoutPointer, 0x7EB9); // original layout pointer
        LoadAndStore((byte)(layoutPointer >> 8), 0x7EBA);
        LoadAndStoreZeroPage((byte)enemyPointer, 0x65);
        LoadAndStoreZeroPage((byte)(enemyPointer >> 8), 0x66);
        LoadAndStore((byte)enemyPointer, 0x7EBB);  // original enemy pointer
        LoadAndStore((byte)(enemyPointer >> 8), 0x7EBC);
        LoadAndStore(0, 0x00ED); // Player_Suit: Small Mario
        LoadAndStore(0, 0x03F3); // Map_Power_Disp
        LoadAndStore(0, 0x0726); // Player_Current
        LoadAndStore(0, 0x0727); // World_Num: World 1
        LoadAndStore(0, 0x072B); // Total_Players: one player
        LoadAndStore(4, 0x0736); // Player_Lives: normal one-player starting lives
        code.Add(0x60);           // RTS to the original level setup
        return code.ToArray();
    }

    private static bool HasExpectedBytes(ReadOnlySpan<byte> bytes, int offset, ReadOnlySpan<byte> expected) =>
        offset >= 0 && offset <= bytes.Length - expected.Length && bytes.Slice(offset, expected.Length).SequenceEqual(expected);

    private static bool HasExpectedBytesAny(ReadOnlySpan<byte> bytes, int offset, params byte[][] expected)
    {
        foreach (var item in expected)
        {
            if (HasExpectedBytes(bytes, offset, item)) return true;
        }
        return false;
    }

    private static bool HasJsrIntoRetryArea(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset > bytes.Length - 3 || bytes[offset] != 0x20) return false;
        var target = (ushort)(bytes[offset + 1] | (bytes[offset + 2] << 8));
        return target is >= 0xE911 and <= 0xE9AF;
    }

    private static bool HasJsrIntoPatchRuntime(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset > bytes.Length - 3 || bytes[offset] != 0x20) return false;
        var target = (ushort)(bytes[offset + 1] | (bytes[offset + 2] << 8));
        return target is >= PatchRuntimeAddress and <= PatchRuntimeEnd;
    }

    private static bool HasJumpIntoPatchRuntime(ReadOnlySpan<byte> bytes, int offset)
    {
        if (offset < 0 || offset > bytes.Length - 3 || bytes[offset] != 0x4C) return false;
        var target = (ushort)(bytes[offset + 1] | (bytes[offset + 2] << 8));
        return target is >= PatchRuntimeAddress and <= PatchRuntimeEnd;
    }

    private static bool HasExpectedFill(ReadOnlySpan<byte> bytes, int offset, int length, byte value) =>
        offset >= 0 && length >= 0 && offset <= bytes.Length - length && bytes.Slice(offset, length).ToArray().All(item => item == value);

    private static bool HasSourceBytes(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> source, int offset, int length) =>
        offset >= 0 && length >= 0 && offset <= bytes.Length - length && offset <= source.Length - length &&
        bytes.Slice(offset, length).SequenceEqual(source.Slice(offset, length));

    private static bool HasExpectedHarness(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> source) =>
        HasExpectedFill(bytes, HarnessOffset, HarnessCapacity, 0xFF) ||
        HasSourceBytes(bytes, source, HarnessOffset, HarnessCapacity) ||
        HasExpectedBytes(bytes, HarnessOffset, [0xA0, 0x06, 0x20, 0xCE, 0x96]) ||
        HasExpectedBytes(bytes, HarnessOffset, [0xEE, 0x55, 0x79, 0xA9, 0x28]) ||
        HasExpectedBytes(bytes, HarnessOffset, [0xA2, 0xFF, 0x9A, 0xEE, 0x55]);

    private static void WriteJump(byte[] bytes, int offset, ushort target)
    {
        bytes[offset] = 0x4C;
        bytes[offset + 1] = (byte)target;
        bytes[offset + 2] = (byte)(target >> 8);
    }

    private static void WriteJsr(byte[] bytes, int offset, ushort target)
    {
        bytes[offset] = 0x20;
        bytes[offset + 1] = (byte)target;
        bytes[offset + 2] = (byte)(target >> 8);
    }
}
