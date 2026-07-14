using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Smb3Editor.Core;

/// <summary>Runs the bundled ASM6f assembler without exposing a console window.</summary>
public sealed class Asm6fAssembler
{
    private readonly string _executablePath;

    public Asm6fAssembler(string? executablePath = null) =>
        _executablePath = executablePath ?? FindBundledAssembler();

    private static string FindBundledAssembler()
    {
        var besideApplication = Path.Combine(AppContext.BaseDirectory, "asm6f_64.exe");
        if (File.Exists(besideApplication)) return besideApplication;

        // Source-checkout fallback. Portable releases receive a sibling copy
        // from the package builder, so this path never changes release layout.
        return Path.Combine(AppContext.BaseDirectory, "tools", "asm6f", "asm6f_64.exe");
    }

    public OperationResult<byte[]> Assemble(string sourcePath)
    {
        if (!File.Exists(_executablePath))
        {
            return OperationResult<byte[]>.Failure(Diagnostics.Error(
                "ASM6F_MISSING",
                "The bundled ASM6f assembler is missing. Reinstall Warp Whistle or restore asm6f_64.exe."));
        }

        if (!File.Exists(sourcePath))
        {
            return OperationResult<byte[]>.Failure(Diagnostics.Error("ASM6F_SOURCE", $"Patch source '{sourcePath}' was not found."));
        }

        var outputPath = Path.Combine(Path.GetTempPath(), $"warp-whistle-{Guid.NewGuid():N}.bin");
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = _executablePath,
                Arguments = $"-q \"{sourcePath}\" \"{outputPath}\"",
                WorkingDirectory = Path.GetDirectoryName(sourcePath)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
            {
                return OperationResult<byte[]>.Failure(Diagnostics.Error("ASM6F_START", "ASM6f could not be started."));
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                var detail = string.Join(Environment.NewLine, new[] { stdout, stderr }.Where(static text => !string.IsNullOrWhiteSpace(text)));
                return OperationResult<byte[]>.Failure(Diagnostics.Error("ASM6F_ASSEMBLE", string.IsNullOrWhiteSpace(detail)
                    ? $"ASM6f failed while assembling '{Path.GetFileName(sourcePath)}'."
                    : detail.Trim()));
            }

            return OperationResult<byte[]>.Success(File.ReadAllBytes(outputPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return OperationResult<byte[]>.Failure(Diagnostics.Error("ASM6F_IO", ex.Message));
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}

public sealed record PatchRequirementManifest(
    string Kind,
    string Scope,
    string Message,
    int? EnemyId = null,
    int? YMin = null,
    int? YMax = null);

public sealed record PatchFeatureManifest(
    string Id,
    string DisplayName,
    string Description,
    bool RecommendedDefault = false,
    bool SupportsLevelOverrides = false,
    int Flag = 0,
    IReadOnlyList<PatchRequirementManifest>? Requirements = null);

public sealed record PatchConfigurationManifest(string Kind, int Offset, int Capacity);

public sealed record AsmPatchManifest(
    string Id,
    string DisplayName,
    string Source,
    int SchemaVersion = 1,
    string Description = "",
    bool RecommendedDefault = false,
    bool SupportsLevelOverrides = false,
    IReadOnlyList<string>? SupportedProfiles = null,
    IReadOnlyList<AsmPatchWrite>? Writes = null,
    IReadOnlyList<PatchFeatureManifest>? Features = null,
    PatchConfigurationManifest? Configuration = null);

public sealed record AsmPatchWrite(
    int Offset,
    string? ExpectedHex,
    string? ExpectedFill,
    int Capacity,
    int OutputOffset,
    int OutputLength,
    string? FeatureId = null);

public sealed record PatchDefinition(
    string Id,
    string PackageId,
    string DisplayName,
    string Description,
    bool RecommendedDefault,
    bool SupportsLevelOverrides,
    IReadOnlyList<string> SupportedProfiles,
    int Flag,
    IReadOnlyList<PatchRequirementManifest> Requirements);

public sealed record PatchPackage(string Directory, AsmPatchManifest Manifest, IReadOnlyList<PatchDefinition> Features);

public static class PatchCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string? FindRoot()
    {
        foreach (var root in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "patches");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public static OperationResult<IReadOnlyList<PatchPackage>> Discover(string? root = null)
    {
        root ??= FindRoot();
        if (root is null)
            return OperationResult<IReadOnlyList<PatchPackage>>.Failure(Diagnostics.Error("PATCH_CATALOG", "The bundled patches folder was not found."));

        var packages = new List<PatchPackage>();
        var diagnostics = new List<Diagnostic>();
        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(static path => path, StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(directory, "patch.json");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<AsmPatchManifest>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest is null || manifest.SchemaVersion != 1 || string.IsNullOrWhiteSpace(manifest.Id) ||
                    string.IsNullOrWhiteSpace(manifest.DisplayName) || string.IsNullOrWhiteSpace(manifest.Source))
                {
                    diagnostics.Add(Diagnostics.Error("PATCH_MANIFEST", $"{manifestPath} is missing required schemaVersion, id, displayName, or source fields."));
                    continue;
                }

                var profiles = manifest.SupportedProfiles ?? [];
                var featureManifests = manifest.Features is { Count: > 0 }
                    ? manifest.Features
                    : [new PatchFeatureManifest(
                        manifest.Id,
                        manifest.DisplayName,
                        manifest.Description,
                        manifest.RecommendedDefault,
                        manifest.SupportsLevelOverrides)];
                var features = featureManifests.Select(feature => new PatchDefinition(
                    feature.Id,
                    manifest.Id,
                    feature.DisplayName,
                    feature.Description,
                    feature.RecommendedDefault,
                    feature.SupportsLevelOverrides,
                    profiles,
                    feature.Flag,
                    feature.Requirements ?? [])).ToArray();
                packages.Add(new PatchPackage(directory, manifest, features));
            }
            catch (JsonException ex)
            {
                diagnostics.Add(Diagnostics.Error("PATCH_MANIFEST", $"{manifestPath}: {ex.Message}"));
            }
        }

        var duplicate = packages.SelectMany(static package => package.Features)
            .GroupBy(static feature => feature.Id, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
            diagnostics.Add(Diagnostics.Error("PATCH_MANIFEST", $"Patch id '{duplicate.Key}' is declared more than once."));
        if (diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error))
            return OperationResult<IReadOnlyList<PatchPackage>>.Failure(diagnostics.ToArray());
        return OperationResult<IReadOnlyList<PatchPackage>>.Success(packages, diagnostics);
    }
}

/// <summary>Applies one ASM6f source file whose output is mapped to verified ROM writes.</summary>
public sealed class AsmPatchCompiler
{
    private static readonly Regex ProfileDirective = new(@"^\s*;\s*@ww\s+profile\s+(?<profile>[a-z0-9-]+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HookDirective = new(@"^\s*;\s*@ww\s+hook\s+\$(?<offset>[0-9a-f]+)\s+expect\s+(?<expected>[0-9a-f]+)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FreeDirective = new(@"^\s*;\s*@ww\s+free\s+\$(?<offset>[0-9a-f]+)\s+size\s+(?<size>\d+)\s+fill\s+(?<fill>[0-9a-f]{2})\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly Asm6fAssembler _assembler;

    public AsmPatchCompiler(Asm6fAssembler? assembler = null) => _assembler = assembler ?? new Asm6fAssembler();

    public OperationResult<byte[]> Apply(
        string packageDirectory,
        RomImage source,
        ReadOnlySpan<byte> compiledRom,
        IReadOnlySet<string>? enabledFeatures = null)
    {
        var manifestPath = Path.Combine(packageDirectory, "patch.json");
        if (!File.Exists(manifestPath)) return OperationResult<byte[]>.Failure(Diagnostics.Error("ASM_PATCH_MANIFEST", "patch.json was not found."));

        AsmPatchManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AsmPatchManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            return OperationResult<byte[]>.Failure(Diagnostics.Error("ASM_PATCH_MANIFEST", ex.Message));
        }
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Source))
        {
            return OperationResult<byte[]>.Failure(Diagnostics.Error("ASM_PATCH_MANIFEST", "Patch manifest needs an id and source file."));
        }

        var sourcePath = Path.Combine(packageDirectory, manifest.Source);
        var directives = ReadDirectives(sourcePath, manifest);
        if (!directives.IsSuccess) return OperationResult<byte[]>.Failure([.. directives.Diagnostics]);
        var (profiles, writes) = directives.Value!;
        if (!profiles.Contains(source.Profile.Id, StringComparer.Ordinal))
        {
            return OperationResult<byte[]>.Failure(Diagnostics.Error("ASM_PATCH_PROFILE", $"{manifest.DisplayName} is not verified for {source.Profile.Id}."));
        }

        var assembled = _assembler.Assemble(sourcePath);
        if (!assembled.IsSuccess) return OperationResult<byte[]>.Failure([.. assembled.Diagnostics]);

        var output = compiledRom.ToArray();
        foreach (var write in writes)
        {
            if (!string.IsNullOrWhiteSpace(write.FeatureId) && enabledFeatures is not null && !enabledFeatures.Contains(write.FeatureId))
                continue;
            var outputLength = write.OutputLength == 0 ? assembled.Value!.Length - write.OutputOffset : write.OutputLength;
            if (write.Offset < 0 || write.Capacity <= 0 || write.Offset > output.Length - write.Capacity ||
                write.OutputOffset < 0 || outputLength < 0 || write.OutputOffset > assembled.Value!.Length - outputLength || outputLength > write.Capacity)
            {
                return OperationResult<byte[]>.Failure(Diagnostics.Error("ASM_PATCH_MANIFEST", $"{manifest.Id} has an invalid output range."));
            }
            if (!string.IsNullOrWhiteSpace(write.ExpectedHex))
            {
                byte[] expected;
                try { expected = Convert.FromHexString(write.ExpectedHex); }
                catch (FormatException) { return OperationResult<byte[]>.Failure(Diagnostics.Error("ASM_PATCH_MANIFEST", $"{manifest.Id} has an invalid expectedHex value.")); }
                if (expected.Length > write.Capacity || !output.AsSpan(write.Offset, expected.Length).SequenceEqual(expected))
                {
                    return OperationResult<byte[]>.Failure(Diagnostics.Error("ASM_PATCH_SIGNATURE", $"{manifest.DisplayName} does not match its verified ROM site at ${write.Offset:X}."));
                }
            }
            else if (!string.IsNullOrWhiteSpace(write.ExpectedFill) &&
                     (!byte.TryParse(write.ExpectedFill, System.Globalization.NumberStyles.HexNumber, null, out var fill) ||
                      !output.AsSpan(write.Offset, write.Capacity).ToArray().All(value => value == fill)))
            {
                return OperationResult<byte[]>.Failure(Diagnostics.Error("ASM_PATCH_SIGNATURE", $"{manifest.DisplayName} needs ${write.Offset:X}-${write.Offset + write.Capacity - 1:X} to be filled with ${write.ExpectedFill}."));
            }
            else if (string.IsNullOrWhiteSpace(write.ExpectedHex) && string.IsNullOrWhiteSpace(write.ExpectedFill))
            {
                return OperationResult<byte[]>.Failure(Diagnostics.Error("ASM_PATCH_MANIFEST", $"{manifest.Id} needs expectedHex or expectedFill for every write."));
            }
            assembled.Value.AsSpan(write.OutputOffset, outputLength).CopyTo(output.AsSpan(write.Offset));
        }

        return OperationResult<byte[]>.Success(output, [Diagnostics.Info("ASM_PATCH_READY", $"Applied {manifest.DisplayName}.")]);
    }

    private static OperationResult<(IReadOnlyList<string> Profiles, IReadOnlyList<AsmPatchWrite> Writes)> ReadDirectives(string sourcePath, AsmPatchManifest manifest)
    {
        if (!File.Exists(sourcePath)) return OperationResult<(IReadOnlyList<string>, IReadOnlyList<AsmPatchWrite>)>.Failure(Diagnostics.Error("ASM6F_SOURCE", $"Patch source '{sourcePath}' was not found."));

        var profiles = new List<string>();
        var writes = new List<AsmPatchWrite>();
        var outputOffset = 0;
        foreach (var line in File.ReadLines(sourcePath))
        {
            var profile = ProfileDirective.Match(line);
            if (profile.Success)
            {
                profiles.Add(profile.Groups["profile"].Value);
                continue;
            }

            var hook = HookDirective.Match(line);
            if (hook.Success)
            {
                var expected = hook.Groups["expected"].Value;
                if (expected.Length % 2 != 0 || !int.TryParse(hook.Groups["offset"].Value, System.Globalization.NumberStyles.HexNumber, null, out var offset))
                {
                    return OperationResult<(IReadOnlyList<string>, IReadOnlyList<AsmPatchWrite>)>.Failure(Diagnostics.Error("ASM_PATCH_DIRECTIVE", $"{manifest.Id} has an invalid @ww hook directive."));
                }
                var length = expected.Length / 2;
                writes.Add(new AsmPatchWrite(offset, expected, null, length, outputOffset, length));
                outputOffset += length;
                continue;
            }

            var free = FreeDirective.Match(line);
            if (free.Success)
            {
                if (!int.TryParse(free.Groups["offset"].Value, System.Globalization.NumberStyles.HexNumber, null, out var offset) ||
                    !int.TryParse(free.Groups["size"].Value, out var size) || size <= 0)
                {
                    return OperationResult<(IReadOnlyList<string>, IReadOnlyList<AsmPatchWrite>)>.Failure(Diagnostics.Error("ASM_PATCH_DIRECTIVE", $"{manifest.Id} has an invalid @ww free directive."));
                }
                writes.Add(new AsmPatchWrite(offset, null, free.Groups["fill"].Value, size, outputOffset, 0));
            }
        }

        if (profiles.Count > 0 && writes.Count > 0) return OperationResult<(IReadOnlyList<string>, IReadOnlyList<AsmPatchWrite>)>.Success((profiles, writes));
        if (manifest.SupportedProfiles is { Count: > 0 } && manifest.Writes is { Count: > 0 }) return OperationResult<(IReadOnlyList<string>, IReadOnlyList<AsmPatchWrite>)>.Success((manifest.SupportedProfiles, manifest.Writes));
        return OperationResult<(IReadOnlyList<string>, IReadOnlyList<AsmPatchWrite>)>.Failure(Diagnostics.Error("ASM_PATCH_DIRECTIVE", $"{manifest.Id} needs @ww profile plus @ww hook/free directives."));
    }
}
