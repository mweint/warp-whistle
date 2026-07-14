namespace Smb3Editor.Core;

public sealed record PatchDefinition(
    string Id,
    string DisplayName,
    string Description,
    bool RecommendedDefault = false,
    bool SupportsLevelOverrides = true,
    IReadOnlyList<string>? SupportedProfiles = null);

/// <summary>Built-in patches. External executable plugins are intentionally not loaded.</summary>
public static class PatchRegistry
{
    public static IReadOnlyList<PatchDefinition> BuiltIns { get; } =
    [
        new("quick-retry", "Quick Retry", "After a normal death, restart the level without visiting the overworld.", true, true, ["us-prg1"]),
        new("start-select-map", "Start + Select: Return to Map", "While paused, Select leaves the level without completing it.", true, true, ["us-prg1"]),
        new("continuous-auto-scroll", "Full-Level Auto-Scroll", "Extends a stock horizontal Auto-Scroll controller to the authored level end, including after an early goal, then stops cleanly. Requires Auto-Scroll ($D3) with Y = 0x00-0x1F; X is its placement/trigger column.", false, true, ["us-prg1"])
    ];
}

/// <summary>
/// Applies a small PRG1-only patch in documented, verified unused MMC3 ROM
/// space. It deliberately retains mapper 4 and the original image size.
/// </summary>
public sealed class PatchCompiler
{
    private const int ExitHookOffset = 0x3CF9E;
    private const int PauseHookOffset = 0x3CE6D;
    private const int PrepareLevelCallOffset = 0x3C937;
    // PRG bank 9, CPU $BBFB: the stock horizontal auto-scroll stop sequence.
    private const int AutoScrollEndHookOffset = 0x13C0B;
    // Fixed bank 30, CPU $8F2E: JSR AutoScroll_Do.
    private const int AutoScrollCallHookOffset = 0x3CF3E;
    private const int RuntimeOffset = 0x3E250;
    private const int RuntimeCapacity = 128;
    private const int AutoScrollGoalHelperLength = 14;
    private const int AutoScrollGoalHelperOffset = RuntimeOffset + RuntimeCapacity - AutoScrollGoalHelperLength;
    private const int RetryEntryOffset = 0x3E921;
    // The verified fixed-bank temporary area is shared with the direct-level
    // harness and provides 111 bytes before the following non-fill data.
    private const int RetryEntryCapacity = 111;
    private const int RetryStubCapacity = 76;
    private const ushort RuntimeAddress = 0xE240;
    private const ushort AutoScrollGoalHelperAddress = RuntimeAddress + RuntimeCapacity - AutoScrollGoalHelperLength;
    private const ushort RetryEntryAddress = 0xE911;
    private const int AutoScrollCallWrapperOffset = RetryEntryOffset + RetryStubCapacity;
    private const ushort AutoScrollCallWrapperAddress = RetryEntryAddress + RetryStubCapacity;
    private const int AutoScrollCallWrapperCapacity = RetryEntryCapacity - RetryStubCapacity;
    private const int ConfigurationOffset = 0x3FF3A;
    private const int ConfigurationCapacity = 22;
    private const ushort ConfigurationAddress = 0xFF2A;
    // Continue after the three-byte instructions replaced by each hook.
    // Re-entering at the preceding bytes corrupts the gameplay loop.
    private const ushort ExitContinuation = 0x8F91;
    private const ushort PauseContinuation = 0x8E60;
    private const ushort LevelPreparationEntry = 0x88C8;
    private const ushort ClearRam = 0x96CE;
    private const ushort ExitHandling = 0x8F31;
    private const ushort PrepareLevelOriginal = 0xB0FF;
    private const ushort AutoScrollLoadEndLoop = 0xBC0C;
    private static readonly byte[] ExitHookExpected = [0xAE, 0x26, 0x07];
    private static readonly byte[] PauseHookExpected = [0xAD, 0xE7, 0x04];
    private static readonly byte[] PrepareLevelExpected = [0x20, 0xFF, 0xB0];
    private static readonly byte[] AutoScrollEndHookExpected = [0xA9, 0x00, 0x8D, 0x0E, 0x7A];
    private static readonly byte[] AutoScrollCallHookExpected = [0x20, 0x00, 0xB9];

    private const byte FlagQuickRetry = 0x01;
    private const byte FlagQuitToMap = 0x02;
    private const byte FlagContinuousAutoScroll = 0x04;

    public OperationResult<byte[]> Apply(ProjectDocumentV2 project, RomImage source, byte[] compiledBytes)
    {
        var settings = project.Patches ?? PatchSettings.None;
        var hasContinuousAutoScroll = (settings.ContinuousAutoScroll ?? new()).EnabledByDefault ||
            source.Profile.Levels.Keys.Any(areaId => (settings.ContinuousAutoScroll ?? new()).IsEnabledFor(areaId));
        var hasExitOrQuitHooks = (settings.QuickRetry ?? new()).EnabledByDefault ||
            (settings.StartSelectReturnToMap ?? new()).EnabledByDefault ||
            source.Profile.Levels.Keys.Any(areaId => (settings.QuickRetry ?? new()).IsEnabledFor(areaId) || (settings.StartSelectReturnToMap ?? new()).IsEnabledFor(areaId));
        if (!settings.HasEnabledOptions(source.Profile.Levels.Keys))
        {
            return OperationResult<byte[]>.Success(compiledBytes);
        }


        if (!string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal))
        {
            return OperationResult<byte[]>.Failure(Diagnostics.Error(
                "PATCH_PROFILE",
                "Enhanced patches are currently verified only for Super Mario Bros. 3 (USA, PRG1 / Rev A)."));
        }

        if (compiledBytes.Length != source.Bytes.Length ||
            (hasExitOrQuitHooks && (!HasExpectedBytes(compiledBytes, ExitHookOffset, ExitHookExpected) ||
                                    !HasExpectedBytes(compiledBytes, PauseHookOffset, PauseHookExpected) ||
                                    !HasExpectedBytes(compiledBytes, PrepareLevelCallOffset, PrepareLevelExpected))) ||
            (hasContinuousAutoScroll && (!HasExpectedBytes(compiledBytes, AutoScrollEndHookOffset, AutoScrollEndHookExpected) ||
                                         !HasExpectedBytes(compiledBytes, AutoScrollCallHookOffset, AutoScrollCallHookExpected))) ||
            !HasExpectedFill(compiledBytes, RuntimeOffset, RuntimeCapacity, 0xFF) ||
            !HasExpectedFill(compiledBytes, RetryEntryOffset, RetryEntryCapacity, 0xFF) ||
            !HasExpectedFill(compiledBytes, ConfigurationOffset, ConfigurationCapacity, 0xFF))
        {
            return OperationResult<byte[]>.Failure(Diagnostics.Error(
                "PATCH_SIGNATURE",
                "The verified PRG1 patch sites do not match this ROM; no enhanced ROM was created."));
        }

        var controllerValidation = ValidateContinuousAutoScrollControllers(project, source, settings);
        if (!controllerValidation.IsSuccess) return OperationResult<byte[]>.Failure([.. controllerValidation.Diagnostics]);

        var runtime = BuildRuntime(project, source);
        var retryEntry = CreateRetryEntryStub();
        var autoScrollCallWrapper = CreateAutoScrollCallWrapper();
        var autoScrollGoalHelper = CreateAutoScrollGoalHelper();
        if (runtime.Code.Length > RuntimeCapacity - (hasContinuousAutoScroll ? autoScrollGoalHelper.Length : 0) ||
            runtime.Configuration.Length > ConfigurationCapacity ||
            (runtime.HasExitOrQuitHooks && retryEntry.Length > RetryStubCapacity) ||
            (hasContinuousAutoScroll && autoScrollCallWrapper.Length > AutoScrollCallWrapperCapacity))
        {
            return OperationResult<byte[]>.Failure(Diagnostics.Error(
                "PATCH_SPACE",
                $"The enabled patches need {runtime.Code.Length} code bytes and {runtime.Configuration.Length} configuration bytes, exceeding their verified MMC3 space. This unchanged-size build supports at most seven explicit level overrides."));
        }

        var output = compiledBytes.ToArray();
        if (runtime.HasExitOrQuitHooks)
        {
            WriteJump(output, ExitHookOffset, runtime.ExitAddress);
            WriteJump(output, PauseHookOffset, runtime.QuitAddress);
            WriteJsr(output, PrepareLevelCallOffset, runtime.PrepareAddress);
            retryEntry.CopyTo(output, RetryEntryOffset);
        }
        if (hasContinuousAutoScroll)
        {
            WriteAutoScrollEndHook(output, runtime.AutoScrollEndAddress);
            WriteJsr(output, AutoScrollCallHookOffset, AutoScrollCallWrapperAddress);
            autoScrollCallWrapper.CopyTo(output, AutoScrollCallWrapperOffset);
            autoScrollGoalHelper.CopyTo(output, AutoScrollGoalHelperOffset);
        }
        runtime.Code.CopyTo(output, RuntimeOffset);
        runtime.Configuration.CopyTo(output, ConfigurationOffset);

        if ((runtime.HasExitOrQuitHooks && (!HasJumpTarget(output, ExitHookOffset, runtime.ExitAddress) ||
                                            !HasJumpTarget(output, PauseHookOffset, runtime.QuitAddress) ||
                                            !HasJsrTarget(output, PrepareLevelCallOffset, runtime.PrepareAddress) ||
                                            !output.AsSpan(RetryEntryOffset, retryEntry.Length).SequenceEqual(retryEntry))) ||
            (hasContinuousAutoScroll && (!HasAutoScrollEndHook(output, runtime.AutoScrollEndAddress) ||
                                         !HasJsrTarget(output, AutoScrollCallHookOffset, AutoScrollCallWrapperAddress) ||
                                         !output.AsSpan(AutoScrollCallWrapperOffset, autoScrollCallWrapper.Length).SequenceEqual(autoScrollCallWrapper) ||
                                         !output.AsSpan(AutoScrollGoalHelperOffset, autoScrollGoalHelper.Length).SequenceEqual(autoScrollGoalHelper))) ||
            !output.AsSpan(RuntimeOffset, runtime.Code.Length).SequenceEqual(runtime.Code) ||
            !output.AsSpan(ConfigurationOffset, runtime.Configuration.Length).SequenceEqual(runtime.Configuration) ||
            output.Length != source.Bytes.Length)
        {
            return OperationResult<byte[]>.Failure(Diagnostics.Error(
                "PATCH_VERIFY",
                "The enhanced ROM patch did not verify after writing."));
        }

        return OperationResult<byte[]>.Success(output,
        [
            Diagnostics.Info("PATCH_READY", "Enhanced MMC3 patches were applied. This output is for compatible flash carts and repro boards."),
            Diagnostics.Info("PATCH_SCOPE", "Quick Retry, Start + Select, and Full-Level Auto-Scroll use their global defaults with any per-level overrides.")
        ]);
    }

    private sealed record PatchRuntime(
        byte[] Code,
        byte[] Configuration,
        ushort ExitAddress,
        ushort QuitAddress,
        ushort PrepareAddress,
        ushort AutoScrollEndAddress,
        bool HasExitOrQuitHooks);

    private static PatchRuntime BuildRuntime(ProjectDocumentV2 project, RomImage source)
    {
        var settings = project.Patches ?? PatchSettings.None;
        var hasContinuousAutoScroll = (settings.ContinuousAutoScroll ?? new()).EnabledByDefault ||
            source.Profile.Levels.Keys.Any(areaId => (settings.ContinuousAutoScroll ?? new()).IsEnabledFor(areaId));
        // Store only explicit overrides. Unlisted areas use the compact global
        // default, leaving the fixed-bank code path and vanilla ROM size intact.
        var overrideIds = (settings.QuickRetry?.LevelOverrides?.Keys ?? [])
            .Concat(settings.StartSelectReturnToMap?.LevelOverrides?.Keys ?? [])
            .Concat(settings.ContinuousAutoScroll?.LevelOverrides?.Keys ?? [])
            .Distinct(StringComparer.Ordinal)
            .Where(source.Profile.Levels.ContainsKey)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var flagsByArea = overrideIds
            .Select(areaId => (Level: source.Profile.Levels[areaId], Flags: GetFlags(settings, areaId)))
            .ToArray();

        var body = new List<byte>(RuntimeCapacity);
        var fixups = new List<(int Offset, string Label)>();
        var labels = new Dictionary<string, int>(StringComparer.Ordinal);
        var tableOperands = new List<(int Offset, int Addend)>();
        int globalOperand = -1;
        int autoScrollEndResolver = -1;
        int exitResolver = -1;
        int quitResolver = -1;
        var hasLevelOverrides = flagsByArea.Length != 0;
        var hasExitOrQuitOverrides = (settings.QuickRetry?.LevelOverrides?.Count ?? 0) != 0 ||
            (settings.StartSelectReturnToMap?.LevelOverrides?.Count ?? 0) != 0;
        var hasExitOrQuitHooks = (settings.QuickRetry ?? new()).EnabledByDefault ||
            (settings.StartSelectReturnToMap ?? new()).EnabledByDefault ||
            hasExitOrQuitOverrides;

        void Mark(string name) => labels[name] = body.Count;
        void Byte(params byte[] bytes) => body.AddRange(bytes);
        void Branch(byte opcode, string label)
        {
            Byte(opcode, 0);
            fixups.Add((body.Count - 1, label));
        }
        void AbsoluteTable(int addend)
        {
            Byte(0, 0);
            tableOperands.Add((body.Count - 2, addend));
        }

        if (hasExitOrQuitHooks)
        {
        // The exit hook is reached only after SMB3 has decided to return to the map.
        Mark("exit");
        // Map_ReturnStatus is still stale at this hook.  The gameplay death
        // state is the authoritative discriminator here; completion leaves it
        // at zero, while ordinary deaths use 1/2 (time-up is 3 and remains
        // vanilla game-over behavior).
        Byte(0xA5, 0xF1);                       // LDA Player_IsDying
        Branch(0xF0, "exitOriginal");          // BEQ: ordinary completion / exit
        Byte(0xC9, 0x03);                       // CMP #TIME_UP_DEATH
        Branch(0xF0, "exitOriginal");
        if (hasExitOrQuitOverrides)
        {
            Byte(0x20, 0x00, 0x00);             // JSR ResolveFlags (patched below)
            exitResolver = body.Count - 2;
        }
        else
        {
            Byte(0xA9, GetGlobalFlags(settings)); // LDA #global flags
        }
        Byte(0x29, FlagQuickRetry);             // AND #QuickRetry
        Branch(0xF0, "exitOriginal");
        Byte(0xAE, 0x26, 0x07);                 // LDX Player_Current
        Byte(0xBD, 0x36, 0x07);                 // LDA Player_Lives,X
        Branch(0xF0, "exitOriginal");          // Let stock code handle game over.
        Byte(0xDE, 0x36, 0x07);                 // DEC Player_Lives,X
        // Re-enter through the same verified level-preparation entry used by
        // Play Level; the patch stub restores pointers before jumping there.
        Byte(0x4C, (byte)(RetryEntryAddress & 0xFF), (byte)(RetryEntryAddress >> 8));
        Mark("exitOriginal");
        Byte(0xAE, 0x26, 0x07);                 // Reproduce replaced stock LDX.
        Byte(0x4C, (byte)(ExitContinuation & 0xFF), (byte)(ExitContinuation >> 8));

        // Select exits only from the already-paused level. Start remains the
        // stock pause toggle and does not need to be held with Select.
        Mark("quit");
        Byte(0xAD, 0x76, 0x03);                 // LDA Level_PauseFlag
        Branch(0xF0, "pauseOriginal");
        Byte(0xA5, 0x18, 0x05, 0x17, 0x29, 0x20); // LDA Pad_Input; ORA Pad_Holding; AND #PAD_SELECT
        Branch(0xF0, "pauseOriginal");
        if (hasExitOrQuitOverrides)
        {
            Byte(0x20, 0x00, 0x00);             // JSR ResolveFlags
            quitResolver = body.Count - 2;
        }
        else
        {
            Byte(0xA9, GetGlobalFlags(settings)); // LDA #global flags
        }
        Byte(0x29, FlagQuitToMap);
        Branch(0xF0, "quitDisabled");
        Byte(0xA9, 0x00, 0x85, 0xF1);           // Suppress retry when quitting during death.
        Byte(0xA9, 0x01, 0x8D, 0x13, 0x07);     // Return without marking the level complete.
        Byte(0xA9, 0x01, 0x85, 0x14);           // Request normal map exit.
        Byte(0x4C, (byte)(ExitHandling & 0xFF), (byte)(ExitHandling >> 8));
        Mark("quitDisabled");
        Mark("pauseOriginal");
        Byte(0xAD, 0xE7, 0x04);                 // Reproduce LDA SndCur_Pause.
        Byte(0x4C, (byte)(PauseContinuation & 0xFF), (byte)(PauseContinuation >> 8));

        // The normal level path calls Map_PrepareLevel before the loader.
        // Retry already has the active level pointers, so it skips that call
        // for one pass only. Vanilla startup continues through the original.
        Mark("prepare");
        Byte(0xAD, 0xF0, 0x7E);                 // LDA retry-preparation flag
        Branch(0xF0, "prepareOriginal");
        Byte(0xA9, 0x00, 0x8D, 0xF0, 0x7E);    // clear one-shot flag
        Byte(0x60);                             // RTS
        Mark("prepareOriginal");
        Byte(0x20, (byte)(PrepareLevelOriginal & 0xFF), (byte)(PrepareLevelOriginal >> 8));
        Byte(0x60);
        }

        if (hasContinuousAutoScroll)
        {
            // $BBFB runs only after SMB3's selected horizontal controller has
            // fully initialized and exhausted its authored script. Do not hook
            // the gameplay loop: its setup is required for camera, player,
            // death, and level-transition correctness.
            Mark("autoScrollEnd");
            if (hasLevelOverrides)
            {
                Byte(0x20, 0x00, 0x00);         // JSR ResolveFlags
                autoScrollEndResolver = body.Count - 2;
            }
            else
            {
                Byte(0xA9, GetGlobalFlags(settings));
            }
            Byte(0x29, FlagContinuousAutoScroll); // AND #ContinuousAutoScroll
            Branch(0xF0, "autoScrollStock");
            // Mark this controller as chocolate-extended. SMB3 treats any
            // nonzero HAuto value as active, and level initialization clears
            // it, so bit 7 is a compact area-scoped state flag for the goal
            // wrapper below.
            Byte(0xAD, 0x80, 0x05, 0x09, 0x80, 0x8D, 0x80, 0x05);
            // Level_Width is loaded from the active area's header.  Once the
            // auto-scroll camera reaches its final authored screen, let the
            // exact stock stop path run rather than reading beyond the level.
            Byte(0xAD, 0x0A, 0x7A, 0xC5, 0x22); // LDA AScrlPosHHi / CMP Level_Width
            Branch(0xB0, "autoScrollBoundary"); // BCS: at/past final screen
            // Use SMB3's observed terminal scroll speed ($08 = half a pixel
            // per frame).  $10 doubled it in W1-1, while retaining a zero
            // terminal velocity can stop a controller outright.
            Byte(0xA9, 0x08, 0x8D, 0x0E, 0x7A);
            // Preserve horizontal fraction/carry: at $08 they accumulate the
            // half-pixel steps into whole camera pixels.  Reset only vertical
            // motion left by the finished authored sequence.
            Byte(0xA9, 0x00, 0x8D, 0x0F, 0x7A, 0x8D, 0x11, 0x7A, 0x8D, 0x13, 0x7A);
            // The original tail at $BC00 clears horizontal fraction again.
            // Discard this hook's return address and RTS from the stock
            // horizontal controller, preserving the accumulated motion.
            Byte(0x68, 0x68, 0x60);
            Mark("autoScrollBoundary");
            // Freeze the auto camera at the authored boundary. Clearing its
            // mode here switches immediately to the normal follow camera and
            // creates a visible jump.
            Byte(0xA9, 0x00, 0x8D, 0x0E, 0x7A); // horizontal velocity = 0
            Byte(0x68, 0x68, 0x60);             // return from stock controller
            Mark("autoScrollStock");
            // Exact $BBFB behavior displaced by the five-byte JSR/NOP hook.
            Byte(0xA9, 0x00, 0x8D, 0x0E, 0x7A);
            Byte(0xAC, 0x02, 0x7A, 0xB9, 0x34, 0xB9);
            Branch(0xD0, "autoScrollEndLoop");
            Byte(0x8D, 0x0F, 0x7A, 0x8D, 0x10, 0x7A, 0x60);
            Mark("autoScrollEndLoop");
            Byte(0x4C, (byte)(AutoScrollLoadEndLoop & 0xFF), (byte)(AutoScrollLoadEndLoop >> 8));
        }

        Mark("resolve");
        var globalFlags = GetGlobalFlags(settings);
        if (flagsByArea.Length == 0)
        {
            // There is no table to scan.  Reading the global flag as a table
            // entry would never reach the zero-length loop limit.
            Byte(0xA9, globalFlags, 0x60);       // LDA #global; RTS
        }
        else
        {
            Byte(0xA2, 0x00);                    // LDX #0
            Mark("lookup");
            Byte(0xBD); AbsoluteTable(0);         // LDA Table,X (layout low)
            Byte(0xCD, 0xB9, 0x7E);              // CMP Level_LayPtrOrig_AddrL
            Branch(0xD0, "next");
            Byte(0xBD); AbsoluteTable(1);         // LDA Table+1,X (layout high)
            Byte(0xCD, 0xBA, 0x7E);              // CMP Level_LayPtrOrig_AddrH
            Branch(0xF0, "found");
            Mark("next");
            Byte(0xE8, 0xE8, 0xE8, 0xE0, (byte)(flagsByArea.Length * 3));
            Branch(0xD0, "lookup");
            Byte(0xA9, 0x00, 0x60);              // LDA #global; RTS
            globalOperand = body.Count - 2;
            Mark("found");
            Byte(0xBD); AbsoluteTable(2);         // LDA Table+2,X; RTS
            Byte(0x60);
        }

        var configuration = new List<byte>(ConfigurationCapacity);
        foreach (var (level, flags) in flagsByArea)
        {
            var pointer = (ushort)(0xA000 + ((level.LayoutOffset - source.PrgOffset) & 0x1FFF));
            configuration.Add((byte)pointer);
            configuration.Add((byte)(pointer >> 8));
            configuration.Add(flags);
        }
        configuration.Add(globalFlags);

        var resolveAddress = RuntimeAddress + labels["resolve"];
        if (exitResolver >= 0 || quitResolver >= 0 || autoScrollEndResolver >= 0)
        {
            if (exitResolver >= 0)
            {
                body[exitResolver] = (byte)resolveAddress;
                body[exitResolver + 1] = (byte)(resolveAddress >> 8);
            }
            if (quitResolver >= 0)
            {
                body[quitResolver] = (byte)resolveAddress;
                body[quitResolver + 1] = (byte)(resolveAddress >> 8);
            }
            if (autoScrollEndResolver >= 0)
            {
                body[autoScrollEndResolver] = (byte)resolveAddress;
                body[autoScrollEndResolver + 1] = (byte)(resolveAddress >> 8);
            }
        }
        var tableAddress = ConfigurationAddress;
        foreach (var (offset, addend) in tableOperands)
        {
            var address = (ushort)(tableAddress + addend);
            body[offset] = (byte)address;
            body[offset + 1] = (byte)(address >> 8);
        }
        if (globalOperand >= 0) body[globalOperand] = globalFlags;

        foreach (var (offset, label) in fixups)
        {
            var delta = labels[label] - (offset + 1);
            if (delta is < sbyte.MinValue or > sbyte.MaxValue) throw new InvalidOperationException("Patch branch exceeded its verified code region.");
            body[offset] = unchecked((byte)(sbyte)delta);
        }
        return new PatchRuntime(
            body.ToArray(),
            configuration.ToArray(),
            hasExitOrQuitHooks ? (ushort)(RuntimeAddress + labels["exit"]) : (ushort)0,
            hasExitOrQuitHooks ? (ushort)(RuntimeAddress + labels["quit"]) : (ushort)0,
            hasExitOrQuitHooks ? (ushort)(RuntimeAddress + labels["prepare"]) : (ushort)0,
            hasContinuousAutoScroll ? (ushort)(RuntimeAddress + labels["autoScrollEnd"]) : (ushort)0,
            hasExitOrQuitHooks);
    }

    private static byte[] CreateRetryEntryStub() =>
    [
        // Unlike the title-screen direct-test harness, retry starts from an
        // active level.  Preserve zero-page level-selection state so $88C8
        // follows the current level path instead of the map/default branch.
        0xA2, 0xFF, 0x9A,             // LDX #$FF / TXS: discard death-path stack
        // Re-entering level preparation from the map path requires the same
        // video/NMI handoff as the verified direct-level harness.  Without it
        // the title/map update handler can remain active during the handoff.
        0xEE, 0x55, 0x79,             // INC UpdSel_Disable
        0xA9, 0x28, 0x8D, 0x00, 0x20, 0x85, 0xFF,
        0xA9, 0x00, 0x8D, 0x01, 0x20,
        0xA9, 0x04, 0x8D, 0xEE, 0x05, // reset level timer display state
        // Restore pointers from SMB3's original-pointer shadow in case the
        // death path switched to an alternate area.
        0xAD, 0xB9, 0x7E, 0x85, 0x61,
        0xAD, 0xBA, 0x7E, 0x85, 0x62,
        0xAD, 0xBB, 0x7E, 0x85, 0x65,
        0xAD, 0xBC, 0x7E, 0x85, 0x66,
        0xA9, 0x00, 0x8D, 0x13, 0x07,
        0xA9, 0x00, 0x85, 0x14,             // clear Level_ExitToMap from death path
        0xA9, 0x00, 0x85, 0xF1,             // clear Player_IsDying before restart
        0x8D, 0x76, 0x03,                   // clear Level_PauseFlag
        0xA2, 0x17, 0x9D, 0xE0, 0x04,       // clear sound state/queues $04E0-$04F7
        0xCA, 0x10, 0xFB,
        0xA9, 0x01, 0x8D, 0xF0, 0x7E,     // one-shot retry preparation flag
        0x4C, (byte)(LevelPreparationEntry & 0xFF), (byte)(LevelPreparationEntry >> 8)
    ];

    private static byte[] CreateAutoScrollCallWrapper() =>
    [
        0xAD, 0x80, 0x05,
        0x2D, 0x74, 0x79, 0x10, 0x18, // only extended controllers after goal collection
        0xA9, 0x01,
        0x8D, 0xFC, 0x05,             // the goal card clears auto-camera mode; restore it
        0xAD, 0x0A, 0x7A, 0xC5, 0x22, // compare camera screen against Level_Width
        0xA9, 0x00, 0xE9, 0x00, 0x29, 0x08, // $08 before the end; zero at/past it
        0x8D, 0x0E, 0x7A,
        0xA2, 0x00,
        0x4C, (byte)(AutoScrollGoalHelperAddress & 0xFF), (byte)(AutoScrollGoalHelperAddress >> 8),
        0x4C, 0x00, 0xB9              // otherwise tail-call AutoScroll_Do
    ];

    private static byte[] CreateAutoScrollGoalHelper() =>
    [
        0x20, 0x22, 0xBF,             // apply horizontal auto-scroll velocity
        0xAD, 0x0C, 0x7A, 0x85, 0xFD, // copy position to the visible camera
        0xAD, 0x0A, 0x7A, 0x85, 0x12,
        0x60
    ];


    private static byte GetFlags(PatchSettings settings, string areaId) =>
        (byte)(((settings.QuickRetry ?? new()).IsEnabledFor(areaId) ? FlagQuickRetry : 0) |
               ((settings.StartSelectReturnToMap ?? new()).IsEnabledFor(areaId) ? FlagQuitToMap : 0) |
               ((settings.ContinuousAutoScroll ?? new()).IsEnabledFor(areaId) ? FlagContinuousAutoScroll : 0));

    private static byte GetGlobalFlags(PatchSettings settings) =>
        (byte)(((settings.QuickRetry ?? new()).EnabledByDefault ? FlagQuickRetry : 0) |
               ((settings.StartSelectReturnToMap ?? new()).EnabledByDefault ? FlagQuitToMap : 0) |
               ((settings.ContinuousAutoScroll ?? new()).EnabledByDefault ? FlagContinuousAutoScroll : 0));

    private static OperationResult<bool> ValidateContinuousAutoScrollControllers(ProjectDocumentV2 project, RomImage source, PatchSettings settings)
    {
        foreach (var areaId in source.Profile.Levels.Keys.Where(areaId => (settings.ContinuousAutoScroll ?? new()).IsEnabledFor(areaId)))
        {
            LevelDocument? document = null;
            if (!project.ModifiedAreas.TryGetValue(areaId, out document))
            {
                var decoded = Smb3LevelCodec.Decode(source, source.Profile.Levels[areaId]);
                if (!decoded.IsSuccess) return OperationResult<bool>.Failure([.. decoded.Diagnostics]);
                document = decoded.Value;
            }

            // $D3's row byte chooses the stock controller: its upper nibble
            // selects the routine and its lower nibble selects the movement
            // table.  Rows $00-$1F use the horizontal path reaching $BBFB;
            // X is only the placement/trigger column.
            if (document!.Enemies.Any(enemy => enemy.Id == 211 && enemy.Y <= 0x1F)) continue;
            var scope = (settings.ContinuousAutoScroll ?? new()).EnabledByDefault
                ? " It is enabled globally, so every level needs a controller unless you set this patch to a level-only override."
                : string.Empty;
            return OperationResult<bool>.Failure(Diagnostics.Error(
                "CONTINUOUS_SCROLL_CONTROLLER",
                $"{areaId} needs a horizontal Auto-Scroll controller for Full-Level Auto-Scroll. Use Auto-Scroll with Y from 0x00 through 0x1F; X is its placement/trigger column.{scope}"));
        }

        return OperationResult<bool>.Success(true);
    }

    private static bool HasExpectedBytes(ReadOnlySpan<byte> bytes, int offset, ReadOnlySpan<byte> expected) =>
        offset >= 0 && offset <= bytes.Length - expected.Length && bytes.Slice(offset, expected.Length).SequenceEqual(expected);

    private static bool HasExpectedFill(ReadOnlySpan<byte> bytes, int offset, int length, byte value) =>
        offset >= 0 && length >= 0 && offset <= bytes.Length - length && bytes.Slice(offset, length).ToArray().All(item => item == value);

    private static bool HasJumpTarget(ReadOnlySpan<byte> bytes, int offset, ushort address) =>
        HasExpectedBytes(bytes, offset, [0x4C, (byte)address, (byte)(address >> 8)]);

    private static bool HasJsrTarget(ReadOnlySpan<byte> bytes, int offset, ushort address) =>
        HasExpectedBytes(bytes, offset, [0x20, (byte)address, (byte)(address >> 8)]);

    private static bool HasAutoScrollEndHook(ReadOnlySpan<byte> bytes, ushort address) =>
        HasExpectedBytes(bytes, AutoScrollEndHookOffset, [0x20, (byte)address, (byte)(address >> 8), 0xEA, 0xEA]);


    private static void WriteJump(byte[] output, int offset, ushort address)
    {
        output[offset] = 0x4C;
        output[offset + 1] = (byte)address;
        output[offset + 2] = (byte)(address >> 8);
    }

    private static void WriteJsr(byte[] output, int offset, ushort address)
    {
        output[offset] = 0x20;
        output[offset + 1] = (byte)address;
        output[offset + 2] = (byte)(address >> 8);
    }

    private static void WriteAutoScrollEndHook(byte[] output, ushort address)
    {
        WriteJsr(output, AutoScrollEndHookOffset, address);
        // Consume the displaced STA $7A0E. Returning into it would clear the
        // velocity the enabled helper just installed.
        output[AutoScrollEndHookOffset + 3] = 0xEA;
        output[AutoScrollEndHookOffset + 4] = 0xEA;
    }
}
