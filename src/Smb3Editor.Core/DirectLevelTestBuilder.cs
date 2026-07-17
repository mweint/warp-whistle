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
    // PRG30 is fixed at CPU $8000-$9FFF. These four verified $FF ranges are
    // separate from both the stock fixed bank and every built-in patch runtime.
    private const int SetupPrimaryOffset = 0x3DF20;
    private const int SetupPrimaryCapacity = 48;
    private const int SetupContinuationOffset = 0x3DF70;
    private const int SetupContinuationCapacity = 30;
    private const int EntryStubOffset = 0x3DFC6;
    private const int EntryStubCapacity = 74;
    // The entry stub
    // reproduces the stock map-to-level transition immediately before $88C8;
    // entering $88C8 directly leaves the title-screen NMI handler active.
    private const ushort EntryStubAddress = 0x9FB6;
    private const ushort SetupPrimaryAddress = 0x9F10;
    private const ushort SetupContinuationAddress = 0x9F60;
    private const ushort LevelPreparationEntry = 0x88C8;
    private static readonly byte[] TitleEntryExpected = [0x20, 0xAF, 0xA8];
    private static readonly byte[] PrepareLevelExpected = [0x20, 0xFF, 0xB0];
    private readonly IRomCompiler _compiler;

    public DirectLevelTestBuilder(IRomCompiler? compiler = null) => _compiler = compiler ?? new RomCompiler();

    public OperationResult<DirectLevelTestArtifact> Build(ProjectDocumentV2 project, RomImage source, LevelLocation selectedLevel)
    {
        var compiled = _compiler.Compile(project, source);
        if (!compiled.IsSuccess)
        {
            return OperationResult<DirectLevelTestArtifact>.Failure(compiled.Diagnostics.ToArray());
        }

        return Build(compiled.Value!, source, selectedLevel);
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

        if (!TryGetPointers(source, compiledRom.RomBytes, verifiedLevel, out var layoutPointer, out var enemyPointer, out var reason))
        {
            return OperationResult<DirectLevelTestArtifact>.Failure(
                diagnostics.Append(Diagnostics.Error("PLAY_LEVEL_POINTER", reason!)).ToArray());
        }

        var entryStub = CreateEntryStub();
        var setup = CreateSetup(verifiedLevel.Tileset, layoutPointer, enemyPointer);
        // Keep the split on an instruction boundary.  The four pointer writes
        // are seven bytes each (LDA / STA zp / STA absolute), so the last safe
        // primary byte is the end of the third map-state clear at index 43.
        // Writing the bridge at index 45 cut LDA #$04 in half and returned
        // through stock PRG30 code instead of continuing direct setup.
        const int setupFirstSegmentLength = 44;
        if (entryStub.Length > EntryStubCapacity ||
            setup.Length > setupFirstSegmentLength + SetupContinuationCapacity - 1)
        {
            return OperationResult<DirectLevelTestArtifact>.Failure(
                diagnostics.Append(Diagnostics.Error("PLAY_LEVEL_HARNESS", "The verified PRG1 direct-entry payload exceeded its reserved temporary PRG30 area.")).ToArray());
        }

        var output = compiledRom.RomBytes.ToArray();
        if (!HasExpectedFill(output, SetupPrimaryOffset, SetupPrimaryCapacity, 0xFF) ||
            !HasExpectedFill(output, SetupContinuationOffset, SetupContinuationCapacity, 0xFF) ||
            !HasExpectedFill(output, EntryStubOffset, EntryStubCapacity, 0xFF))
        {
            return OperationResult<DirectLevelTestArtifact>.Failure(
                diagnostics.Append(Diagnostics.Error(
                    "PLAY_LEVEL_SPACE",
                    "The verified temporary PRG30 direct-entry space is unavailable in this compiled ROM."))
                    .ToArray());
        }

        // Do not alter fixed-bank patch runtime or retry/autoscroll hooks. The
        // selected-level injection lives only in unused PRG30 bytes. Quick Retry
        // keeps its verified $E94E prepare hook; direct setup raises its one-shot
        // flag, so that hook bypasses Map_PrepareLevel itself.
        WriteJump(output, TitleEntryOffset, EntryStubAddress);
        if (HasExpectedBytes(output, PrepareLevelCallOffset, PrepareLevelExpected))
        {
            var setupRtsAddress = (ushort)(SetupContinuationAddress + Math.Max(0, setup.Length - setupFirstSegmentLength));
            WriteJsr(output, PrepareLevelCallOffset, setupRtsAddress);
        }
        else if (!HasExpectedBytes(output, PrepareLevelCallOffset, [0x20, 0x4E, 0xE9]))
        {
            return OperationResult<DirectLevelTestArtifact>.Failure(
                diagnostics.Append(Diagnostics.Error(
                    "PLAY_LEVEL_PREPARE_HOOK",
                    "Play Level found an unverified level-preparation hook and will not replace it."))
                    .ToArray());
        }

        entryStub.CopyTo(output, EntryStubOffset);
        WriteSplitPayload(output, setup, setupFirstSegmentLength);

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
            !HasExpectedBytes(bytes, TitleEntryOffset, [0x4C, 0xB6, 0x9F]) ||
            !HasDirectPrepareCall(bytes) ||
            !HasExpectedBytes(bytes, EntryStubOffset, [0xA0, 0x06, 0x20, 0xCE, 0x96]) ||
            !HasExpectedBytes(bytes, SetupPrimaryOffset, [0xA9]) ||
            bytes[EntryStubOffset + EntryStubCapacity - 1] != 0xFF)
        {
            return OperationResult<string>.Failure(Diagnostics.Error("PLAY_LEVEL_VERIFY", "The temporary test ROM did not pass direct-level patch verification."));
        }

        return OperationResult<string>.Success("Temporary direct-level ROM verified.");
    }

    private static bool TryGetPointers(
        RomImage source,
        byte[] compiledBytes,
        LevelLocation level,
        out ushort layoutPointer,
        out ushort enemyPointer,
        out string? reason)
    {
        layoutPointer = 0;
        enemyPointer = 0;
        reason = null;
        var sourceGraph = Prg1ReferenceIndexBuilder.Build(source);
        var compiledGraph = Prg1ReferenceIndexBuilder.BuildCurrent(source, compiledBytes);
        if (!sourceGraph.IsSuccess || !compiledGraph.IsSuccess)
        {
            reason = "The compiled ROM's level-reference graph could not be verified for direct testing.";
            return false;
        }

        var originalLayout = new Prg1LayoutStreamId(level.LayoutOffset, level.Tileset);
        var originalEnemy = new Prg1EnemyStreamId(level.EnemyOffset);
        var matchingRoots = sourceGraph.Value!.Roots
            .Where(root => root.Layout == originalLayout && root.Enemy == originalEnemy)
            .ToArray();
        if (matchingRoots.Length == 0)
        {
            reason = $"{level.DisplayName} has no verified source root for direct testing.";
            return false;
        }

        var resolvedRoots = matchingRoots
            .Select(root => compiledGraph.Value!.Roots.SingleOrDefault(item => item.Ordinal == root.Ordinal))
            .Where(static root => root is not null)
            .Select(static root => root!)
            .Select(static root => (root.Layout, root.Enemy))
            .Distinct()
            .ToArray();
        if (resolvedRoots.Length != 1 || resolvedRoots[0].Enemy is not { } resolvedEnemy)
        {
            reason = $"{level.DisplayName}'s compiled roots do not resolve to one verified layout and sprite stream.";
            return false;
        }

        var resolvedLayout = resolvedRoots[0].Layout;
        var layoutBank = (resolvedLayout.FileOffset - source.PrgOffset) / 0x2000;
        var enemyBank = (resolvedEnemy.FileOffset - source.PrgOffset) / 0x2000;
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

        layoutPointer = (ushort)(0xA000 + ((resolvedLayout.FileOffset - source.PrgOffset) & 0x1FFF));
        enemyPointer = (ushort)(0xC000 + ((resolvedEnemy.FileOffset - source.PrgOffset) & 0x1FFF));
        return true;
    }

    private static byte[] CreateEntryStub() =>
    [
        // Retain page $07: its active map/bank context is required while this
        // temporary entry is still executing from switchable PRG30.
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
        0x4C, (byte)(SetupPrimaryAddress & 0xFF), (byte)(SetupPrimaryAddress >> 8) // JMP direct setup in unused PRG30 space
    ];

    private static byte[] CreateSetup(int tileset, ushort layoutPointer, ushort enemyPointer)
    {
        var code = new List<byte>();
        void LoadAndStore(byte value, ushort address)
        {
            code.Add(0xA9); code.Add(value);       // LDA #value
            code.Add(0x8D); code.Add((byte)address); code.Add((byte)(address >> 8)); // STA address
        }
        void LoadAndStorePointerByte(byte value, byte zeroPageAddress, ushort persistentAddress)
        {
            code.Add(0xA9); code.Add(value);       // LDA #value
            code.Add(0x85); code.Add(zeroPageAddress); // STA zero page
            code.Add(0x8D); code.Add((byte)persistentAddress); code.Add((byte)(persistentAddress >> 8)); // STA persistent pointer
        }

        LoadAndStore((byte)tileset, 0x070A); // Level_Tileset
        LoadAndStorePointerByte((byte)layoutPointer, 0x61, 0x7EB9);
        LoadAndStorePointerByte((byte)(layoutPointer >> 8), 0x62, 0x7EBA);
        LoadAndStorePointerByte((byte)enemyPointer, 0x65, 0x7EBB);
        LoadAndStorePointerByte((byte)(enemyPointer >> 8), 0x66, 0x7EBC);
        // Clear_RAM_thru_ZeroPage already zeroes suit and map-power RAM. The
        // following map-state values live above $06FF and need explicit reset.
        code.Add(0xA9); code.Add(0x00); // LDA #$00
        foreach (var address in new ushort[] { 0x0726, 0x0727, 0x072B })
        {
            code.Add(0x8D); code.Add((byte)address); code.Add((byte)(address >> 8));
        }
        LoadAndStore(4, 0x0736); // Player_Lives: normal one-player starting lives
        LoadAndStore(1, 0x7EF0);  // Let the compiled Quick Retry prepare hook retain these pointers.
        code.Add(0x4C); code.Add((byte)(LevelPreparationEntry & 0xFF)); code.Add((byte)(LevelPreparationEntry >> 8));
        return code.ToArray();
    }

    private static void WriteSplitPayload(byte[] output, ReadOnlySpan<byte> payload, int firstSegmentLength)
    {
        payload[..Math.Min(payload.Length, firstSegmentLength)].CopyTo(output.AsSpan(SetupPrimaryOffset));
        if (payload.Length <= firstSegmentLength)
        {
            output[SetupPrimaryOffset + payload.Length] = 0x60; // RTS for the stock prepare-call replacement.
            return;
        }

        WriteJump(output, SetupPrimaryOffset + firstSegmentLength, SetupContinuationAddress);
        payload[firstSegmentLength..].CopyTo(output.AsSpan(SetupContinuationOffset));
        output[SetupContinuationOffset + payload.Length - firstSegmentLength] = 0x60; // RTS for the stock prepare-call replacement.
    }

    private static bool HasExpectedBytes(ReadOnlySpan<byte> bytes, int offset, ReadOnlySpan<byte> expected) =>
        offset >= 0 && offset <= bytes.Length - expected.Length && bytes.Slice(offset, expected.Length).SequenceEqual(expected);

    private static bool HasDirectPrepareCall(ReadOnlySpan<byte> bytes)
    {
        if (HasExpectedBytes(bytes, PrepareLevelCallOffset, [0x20, 0x4E, 0xE9])) return true;
        if (PrepareLevelCallOffset < 0 || PrepareLevelCallOffset > bytes.Length - 3 || bytes[PrepareLevelCallOffset] != 0x20) return false;
        var target = (ushort)(bytes[PrepareLevelCallOffset + 1] | (bytes[PrepareLevelCallOffset + 2] << 8));
        if (target < SetupPrimaryAddress || target >= SetupContinuationAddress + SetupContinuationCapacity) return false;
        var offset = 0x3C010 + target - 0x8000;
        return offset >= 0 && offset < bytes.Length && bytes[offset] == 0x60;
    }

    private static bool HasExpectedFill(ReadOnlySpan<byte> bytes, int offset, int length, byte value) =>
        offset >= 0 && length >= 0 && offset <= bytes.Length - length && bytes.Slice(offset, length).ToArray().All(item => item == value);

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
