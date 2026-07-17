namespace Smb3Editor.Core;

public sealed record EnhancedMmc3Allocation(
    BankRange Code,
    BankRange Configuration,
    BankRange Layout,
    BankRange Palettes,
    BankRange Music,
    BankRange Free)
{
    public int UsedBytes => Code.Length + Configuration.Length + Layout.Length + Palettes.Length + Music.Length;
}

public sealed record EnhancedMmc3Build(byte[] RomBytes, EnhancedMmc3Allocation Allocation);

/// <summary>
/// Builds the first Enhanced MMC3 storage foundation. It expands a clean
/// 256 KiB PRG image to 512 KiB while keeping the original two fixed banks at
/// the end of PRG, matching the verified Foundry address-normalization rule.
/// Data relocation and runtime loader changes belong to later backlog issues.
/// </summary>
public sealed class EnhancedMmc3RomBuilder
{
    private const int HeaderSize = 16;
    private const int PrgBank8K = 0x2000;
    private const int OriginalPrgBytes = 0x40000;
    private const int ExpandedPrgBytes = 0x80000;
    private const int OriginalSwitchableBanks = 30;
    private const int InsertedRegionStart = OriginalSwitchableBanks * PrgBank8K;
    private const int InsertedRegionLength = 32 * PrgBank8K;
    private const int FixedBankSourceOffset = OriginalSwitchableBanks * PrgBank8K;
    private const int FixedBankTargetOffset = 62 * PrgBank8K;

    public OperationResult<EnhancedMmc3Build> Build(ProjectDocumentV2 project, RomImage source, ReadOnlySpan<byte> compiledVanilla)
    {
        if (project.OutputMode != RomOutputMode.EnhancedMmc3)
        {
            return OperationResult<EnhancedMmc3Build>.Failure(
                Diagnostics.Error("ENHANCED_MODE", "The Enhanced MMC3 builder requires Enhanced MMC3 output."));
        }

        if (!string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal) || source.Mapper != 4 || source.PrgLength != OriginalPrgBytes)
        {
            return OperationResult<EnhancedMmc3Build>.Failure(
                Diagnostics.Error("ENHANCED_PROFILE", "Expanded MMC3 output is currently verified only for the clean US PRG1 mapper-4 profile."));
        }

        if (compiledVanilla.Length != source.Bytes.Length || compiledVanilla.Length < source.ChrOffset + source.ChrLength)
        {
            return OperationResult<EnhancedMmc3Build>.Failure(
                Diagnostics.Error("ENHANCED_INPUT", "The enhanced builder requires a verified vanilla-size compiled image."));
        }

        var allocation = AllocateRegions();
        if (!allocation.IsSuccess)
        {
            return OperationResult<EnhancedMmc3Build>.Failure(allocation.Diagnostics.ToArray());
        }

        var output = new byte[HeaderSize + ExpandedPrgBytes + source.ChrLength];
        Array.Fill(output, (byte)0xFF);
        compiledVanilla[..HeaderSize].CopyTo(output);
        output[4] = 32; // 512 KiB PRG in 16 KiB iNES units.
        output[6] |= 0x02; // Battery-backed WRAM: Enhanced save foundation.
        output[8] = 1; // iNES 1.0: one 8 KiB PRG-RAM unit, explicitly battery-backed.

        var sourcePrg = compiledVanilla.Slice(source.PrgOffset, OriginalPrgBytes);
        var targetPrg = output.AsSpan(HeaderSize, ExpandedPrgBytes);
        sourcePrg[..FixedBankSourceOffset].CopyTo(targetPrg);
        sourcePrg[FixedBankSourceOffset..].CopyTo(targetPrg[FixedBankTargetOffset..]);
        compiledVanilla.Slice(source.ChrOffset, source.ChrLength).CopyTo(output.AsSpan(HeaderSize + ExpandedPrgBytes));

        if (output[4] != 32 || (output[6] & 0x02) == 0 || output[8] != 1 || output.Length != HeaderSize + ExpandedPrgBytes + source.ChrLength ||
            !targetPrg[..FixedBankSourceOffset].SequenceEqual(sourcePrg[..FixedBankSourceOffset]) ||
            !targetPrg[FixedBankTargetOffset..].SequenceEqual(sourcePrg[FixedBankSourceOffset..]))
        {
            return OperationResult<EnhancedMmc3Build>.Failure(
                Diagnostics.Error("ENHANCED_VERIFY", "The expanded MMC3 image failed header, size, or fixed-bank verification."));
        }

        return OperationResult<EnhancedMmc3Build>.Success(
            new EnhancedMmc3Build(output, allocation.Value!),
            [Diagnostics.Info("ENHANCED_READY", "Built a 512 KiB PRG Enhanced MMC3 foundation; level/runtime relocation is reserved for later patches.")]);
    }

    private static OperationResult<EnhancedMmc3Allocation> AllocateRegions()
    {
        var allocator = new BankAllocator(InsertedRegionStart, InsertedRegionLength, []);
        var code = allocator.Allocate(0x4000);
        var configuration = allocator.Allocate(0x2000);
        var layout = allocator.Allocate(0x20000);
        var palettes = allocator.Allocate(0x4000);
        var music = allocator.Allocate(0x8000);
        if (!code.IsSuccess || !configuration.IsSuccess || !layout.IsSuccess || !palettes.IsSuccess || !music.IsSuccess)
        {
            return OperationResult<EnhancedMmc3Allocation>.Failure(
                [.. code.Diagnostics, .. configuration.Diagnostics, .. layout.Diagnostics, .. palettes.Diagnostics, .. music.Diagnostics]);
        }

        var usedEnd = music.Value!.End;
        return OperationResult<EnhancedMmc3Allocation>.Success(
            new EnhancedMmc3Allocation(code.Value!, configuration.Value!, layout.Value!, palettes.Value!, music.Value!,
                new BankRange(usedEnd, InsertedRegionStart + InsertedRegionLength - usedEnd)));
    }
}
