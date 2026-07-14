namespace Smb3Editor.Core;

/// <summary>Applies manifest-discovered ASM patches selected by the project.</summary>
public sealed class PatchCompiler
{
    internal const int AutoScrollCallWrapperLength = 35;
    private readonly AsmPatchCompiler _asmPatchCompiler;
    private readonly string? _packageDirectory;

    public PatchCompiler(AsmPatchCompiler? asmPatchCompiler = null, string? packageDirectory = null)
    {
        _asmPatchCompiler = asmPatchCompiler ?? new AsmPatchCompiler();
        _packageDirectory = packageDirectory;
    }

    public OperationResult<byte[]> Apply(ProjectDocumentV2 project, RomImage source, byte[] compiledBytes)
    {
        var settings = project.Patches ?? PatchSettings.None;
        if (!settings.HasEnabledOptions(source.Profile.Levels.Keys))
            return OperationResult<byte[]>.Success(compiledBytes);

        var catalog = PatchCatalog.Discover(_packageDirectory is null ? null : Directory.GetParent(_packageDirectory)?.FullName);
        if (!catalog.IsSuccess) return OperationResult<byte[]>.Failure([.. catalog.Diagnostics]);
        var packages = _packageDirectory is null
            ? catalog.Value!
            : catalog.Value!.Where(package => Path.GetFullPath(package.Directory) == Path.GetFullPath(_packageDirectory)).ToArray();
        var knownIds = packages.SelectMany(static package => package.Features).Select(static feature => feature.Id).ToHashSet(StringComparer.Ordinal);
        var missing = settings.Enumerate().FirstOrDefault(pair => IsEnabled(pair.Value, source.Profile.Levels.Keys) && !knownIds.Contains(pair.Key));
        if (!string.IsNullOrEmpty(missing.Key))
            return OperationResult<byte[]>.Failure(Diagnostics.Error("PATCH_MISSING", $"Enabled patch '{missing.Key}' is not installed in the patches folder."));

        var output = compiledBytes;
        var diagnostics = new List<Diagnostic>();
        foreach (var package in packages)
        {
            var enabled = package.Features
                .Where(feature => IsEnabled(settings.Get(feature.Id), source.Profile.Levels.Keys))
                .ToArray();
            if (enabled.Length == 0) continue;
            var unsupported = enabled.FirstOrDefault(feature => !feature.SupportedProfiles.Contains(source.Profile.Id, StringComparer.Ordinal));
            if (unsupported is not null)
                return OperationResult<byte[]>.Failure(Diagnostics.Error("PATCH_PROFILE", $"{unsupported.DisplayName} is not verified for {source.Profile.Id}."));

            var requirements = ValidateRequirements(project, source, settings, enabled);
            if (!requirements.IsSuccess) return OperationResult<byte[]>.Failure([.. requirements.Diagnostics]);

            var applied = _asmPatchCompiler.Apply(package.Directory, source, output, enabled.Select(static feature => feature.Id).ToHashSet(StringComparer.Ordinal));
            if (!applied.IsSuccess) return OperationResult<byte[]>.Failure([.. applied.Diagnostics]);
            output = applied.Value!;
            diagnostics.AddRange(applied.Diagnostics);

            if (package.Manifest.Configuration is { Kind: "levelFlagsV1" } configuration)
            {
                var configured = WriteLevelFlags(configuration, package.Features, settings, source, output);
                if (!configured.IsSuccess) return OperationResult<byte[]>.Failure([.. configured.Diagnostics]);
                output = configured.Value!;
            }
        }

        diagnostics.Add(Diagnostics.Info("PATCH_READY", "Applied the enabled manifest-discovered ASM patches."));
        return OperationResult<byte[]>.Success(output, diagnostics);
    }

    private static bool IsEnabled(PatchSetting? setting, IEnumerable<string> areaIds) =>
        setting is not null && (setting.EnabledByDefault || areaIds.Any(setting.IsEnabledFor));

    private static OperationResult<byte[]> WriteLevelFlags(
        PatchConfigurationManifest configuration,
        IReadOnlyList<PatchDefinition> features,
        PatchSettings settings,
        RomImage source,
        byte[] output)
    {
        if (configuration.Capacity < 1 || configuration.Offset < 0 || configuration.Offset > output.Length - configuration.Capacity)
            return OperationResult<byte[]>.Failure(Diagnostics.Error("PATCH_MANIFEST", "The levelFlagsV1 configuration range is invalid."));
        var maximumOverrides = (configuration.Capacity - 1) / 3;
        var overrideIds = features
            .SelectMany(feature => settings.Get(feature.Id)?.LevelOverrides?.Keys ?? [])
            .Distinct(StringComparer.Ordinal)
            .Where(source.Profile.Levels.ContainsKey)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        if (overrideIds.Length > maximumOverrides)
            return OperationResult<byte[]>.Failure(Diagnostics.Error("PATCH_SPACE", $"The enabled patches use {overrideIds.Length} explicit level overrides; this patch package supports at most {maximumOverrides}."));

        byte Flags(string? areaId) => (byte)features.Aggregate(0, (flags, feature) =>
            flags | ((areaId is null ? settings.Get(feature.Id)?.EnabledByDefault == true : (settings.Get(feature.Id) ?? new()).IsEnabledFor(areaId)) ? feature.Flag : 0));
        var bytes = Enumerable.Repeat((byte)0xFF, configuration.Capacity).ToArray();
        for (var index = 0; index < overrideIds.Length; index++)
        {
            var level = source.Profile.Levels[overrideIds[index]];
            var pointer = (ushort)(0xA000 + ((level.LayoutOffset - source.PrgOffset) & 0x1FFF));
            bytes[index * 3] = (byte)pointer;
            bytes[index * 3 + 1] = (byte)(pointer >> 8);
            bytes[index * 3 + 2] = Flags(overrideIds[index]);
        }
        bytes[^1] = (byte)((overrideIds.Length << 4) | Flags(null));
        bytes.CopyTo(output, configuration.Offset);
        return OperationResult<byte[]>.Success(output);
    }

    private static OperationResult<bool> ValidateRequirements(
        ProjectDocumentV2 project,
        RomImage source,
        PatchSettings settings,
        IReadOnlyList<PatchDefinition> features)
    {
        foreach (var feature in features)
        foreach (var requirement in feature.Requirements)
        {
            if (!string.Equals(requirement.Kind, "enemy", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(requirement.Scope, "enabledLevels", StringComparison.OrdinalIgnoreCase) ||
                requirement.EnemyId is null)
                return OperationResult<bool>.Failure(Diagnostics.Error("PATCH_MANIFEST", $"{feature.Id} declares an unsupported requirement."));
            foreach (var areaId in source.Profile.Levels.Keys.Where(id => (settings.Get(feature.Id) ?? new()).IsEnabledFor(id)))
            {
                if (!project.ModifiedAreas.TryGetValue(areaId, out var document))
                {
                    var decoded = Smb3LevelCodec.Decode(source, source.Profile.Levels[areaId]);
                    if (!decoded.IsSuccess) return OperationResult<bool>.Failure([.. decoded.Diagnostics]);
                    document = decoded.Value!;
                }
                if (document.Enemies.Any(enemy => enemy.Id == requirement.EnemyId &&
                    (requirement.YMin is null || enemy.Y >= requirement.YMin) &&
                    (requirement.YMax is null || enemy.Y <= requirement.YMax))) continue;
                return OperationResult<bool>.Failure(Diagnostics.Error("PATCH_REQUIREMENT", $"{areaId}: {requirement.Message}"));
            }
        }
        return OperationResult<bool>.Success(true);
    }
}
