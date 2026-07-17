namespace Smb3Editor.Core;

public sealed record BuildArtifact(byte[] RomBytes, IReadOnlyList<Diagnostic> Diagnostics);

public interface IRomCompiler
{
    OperationResult<BuildArtifact> Compile(ProjectDocumentV2 project, RomImage source);
    Prg1VanillaCapacityReport AnalyzeVanillaCapacity(ProjectDocumentV2 project, RomImage source);
}

public sealed class RomCompiler : IRomCompiler
{
    private readonly PatchCompiler _patchCompiler = new();
    private readonly AsmPatchCompiler _asmPatchCompiler = new();
    private readonly EnhancedMmc3RomBuilder _enhancedBuilder = new();

    public OperationResult<BuildArtifact> Compile(ProjectDocumentV2 project, RomImage source)
    {
        var diagnostics = new List<Diagnostic>();
        if (!string.Equals(project.Source.Sha1, source.Sha1, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(project.Source.ProfileId, source.Profile.Id, StringComparison.Ordinal))
        {
            return OperationResult<BuildArtifact>.Failure(
                Diagnostics.Error("BUILD_SOURCE", "The selected source ROM does not match this project's verified source."));
        }

        var prepared = PrepareEditorOwnedBytes(project, source);
        diagnostics.AddRange(prepared.Diagnostics);
        if (!prepared.IsSuccess)
            return OperationResult<BuildArtifact>.Failure(diagnostics.ToArray());
        var output = prepared.Value!;

        Prg1RelocationBuild? relocationBuild = null;
        if (string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal) &&
            project.StorageMode == RomStorageMode.ManagedVanilla)
        {
            var relocated = Prg1VanillaStreamRelocator.Compile(project, source, output);
            diagnostics.AddRange(relocated.Diagnostics);
            if (relocated.IsSuccess)
            {
                relocationBuild = relocated.Value!;
                output = relocationBuild.RomBytes;
            }
        }
        else
        {
            var sharedFixedLayouts = new HashSet<Prg1LayoutStreamId>();
            if (string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal) && project.ModifiedAreas.Count > 0)
            {
                var graph = Prg1ReferenceIndexBuilder.Build(source);
                diagnostics.AddRange(graph.Diagnostics);
                if (graph.IsSuccess)
                {
                    foreach (var stream in graph.Value!.Layouts.Where(static stream => stream.IsRelocatable))
                    {
                        if (graph.Value.Layouts.Any(other => other.Id != stream.Id && other.IsRelocatable &&
                            stream.Id.FileOffset < other.Id.FileOffset + other.Length &&
                            stream.Id.FileOffset + stream.Length > other.Id.FileOffset))
                            sharedFixedLayouts.Add(stream.Id);
                    }
                }
            }
            // Unsupported revisions retain the original-slot-only compiler path.
            foreach (var pair in project.ModifiedAreas.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                if (!source.Profile.Levels.TryGetValue(pair.Key, out var location))
                {
                    diagnostics.Add(Diagnostics.Error("BUILD_AREA", $"Area '{pair.Key}' is not present in the source profile."));
                    continue;
                }

                var layout = Smb3LevelCodec.EncodeLayout(pair.Value);
                var enemies = Smb3LevelCodec.EncodeEnemies(pair.Value);
                diagnostics.AddRange(layout.Diagnostics);
                diagnostics.AddRange(enemies.Diagnostics);
                if (!layout.IsSuccess || !enemies.IsSuccess) continue;

                var layoutId = new Prg1LayoutStreamId(location.LayoutOffset, location.Tileset);
                if (sharedFixedLayouts.Contains(layoutId))
                {
                    diagnostics.Add(Diagnostics.Error("BUILD_SHARED_SLOT",
                        $"{pair.Value.DisplayName} shares stock ROM bytes with another layout interpretation. Enable Managed vanilla level storage before editing this area."));
                    continue;
                }

                if (layout.Value!.Length > pair.Value.OriginalLayoutLength)
                {
                    diagnostics.Add(Diagnostics.Error("BUILD_LAYOUT_SPACE",
                        $"{pair.Value.DisplayName} needs {layout.Value.Length} layout bytes but its verified slot has {pair.Value.OriginalLayoutLength}."));
                    continue;
                }

                if (enemies.Value!.Length > pair.Value.OriginalEnemyLength)
                {
                    diagnostics.Add(Diagnostics.Error("BUILD_ENEMY_SPACE",
                        $"{pair.Value.DisplayName} needs {enemies.Value.Length} enemy bytes but its verified slot has {pair.Value.OriginalEnemyLength}."));
                    continue;
                }

                layout.Value.CopyTo(output, location.LayoutOffset);
                enemies.Value.CopyTo(output, location.EnemyOffset);
                diagnostics.Add(Diagnostics.Info(
                    "BUILD_AREA_OK",
                    $"Compiled {pair.Value.DisplayName}: {layout.Value.Length}/{pair.Value.OriginalLayoutLength} layout bytes, {enemies.Value.Length}/{pair.Value.OriginalEnemyLength} sprite bytes."));
            }
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return OperationResult<BuildArtifact>.Failure(diagnostics.ToArray());
        }

        if (output.Length != source.Bytes.Length || output[0] != (byte)'N' || output[1] != (byte)'E' || output[2] != (byte)'S')
        {
            return OperationResult<BuildArtifact>.Failure(
                Diagnostics.Error("BUILD_VERIFY", "The compiled image failed structural verification."));
        }

        IReadOnlyDictionary<string, int>? layoutOffsetsByArea = relocationBuild is null
            ? null
            : source.Profile.Levels.ToDictionary(
                static pair => pair.Key,
                pair => relocationBuild.LayoutDestinations.TryGetValue(
                    new Prg1LayoutStreamId(pair.Value.LayoutOffset, pair.Value.Tileset),
                    out var destination)
                        ? destination.FileOffset
                        : pair.Value.LayoutOffset,
                StringComparer.Ordinal);
        var patchSettings = project.Patches ?? PatchSettings.None;
        var saveStorage = IsPatchEnabled(patchSettings, "enhanced-autosave-storage", source.Profile.Levels.Keys);
        if (saveStorage && project.OutputMode != RomOutputMode.EnhancedMmc3)
        {
            diagnostics.Add(Diagnostics.Error(
                "ENHANCED_SAVE_MODE",
                "Enhanced Save Storage requires Enhanced MMC3 output. Enable Enhanced output before building."));
            return OperationResult<BuildArtifact>.Failure(diagnostics.ToArray());
        }

        // Save storage owns the same fixed-bank runtime blocks as the retry
        // package. Do not let an assembler fill-check obscure this real
        // incompatibility.
        var conflictingRuntimePatches = new[] { "quick-retry", "start-select-map", "continuous-auto-scroll" }
            .Where(id => IsPatchEnabled(patchSettings, id, source.Profile.Levels.Keys))
            .ToArray();
        if (saveStorage && conflictingRuntimePatches.Length > 0)
        {
            diagnostics.Add(Diagnostics.Error(
                "PATCH_RUNTIME_CONFLICT",
                $"Enhanced Save Storage cannot currently be combined with {string.Join(", ", conflictingRuntimePatches)} because they use the same verified fixed-bank runtime space."));
            return OperationResult<BuildArtifact>.Failure(diagnostics.ToArray());
        }

        // Save storage is a user-selectable enhanced foundation, but it is
        // installed after ordinary selected patches and immediately before
        // PRG expansion. The general patch compiler must not install it in
        // its normal package pass.
        var ordinaryPatchProject = project with
        {
            Patches = patchSettings.With("enhanced-autosave-storage", null)
        };
        var patches = _patchCompiler.Apply(ordinaryPatchProject, source, output, layoutOffsetsByArea);
        diagnostics.AddRange(patches.Diagnostics);
        if (!patches.IsSuccess)
        {
            return OperationResult<BuildArtifact>.Failure(diagnostics.ToArray());
        }
        output = patches.Value!;

        foreach (var patchId in project.ExternalPatches ?? [])
        {
            if (string.IsNullOrWhiteSpace(patchId) || patchId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !string.Equals(patchId, Path.GetFileName(patchId), StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostics.Error("ASM_PATCH_ID", $"'{patchId}' is not a valid bundled patch id."));
                continue;
            }

            var packageDirectory = Path.Combine(AppContext.BaseDirectory, "patches", patchId);
            var externalPatch = _asmPatchCompiler.Apply(packageDirectory, source, output);
            diagnostics.AddRange(externalPatch.Diagnostics);
            if (externalPatch.IsSuccess) output = externalPatch.Value!;
        }

        if (project.OutputMode == RomOutputMode.EnhancedMmc3)
        {
            if (saveStorage)
            {
                var storageRoot = PatchCatalog.FindRoot();
                var storagePackage = storageRoot is null
                    ? string.Empty
                    : Path.Combine(storageRoot, "enhanced-autosave-storage");
                var storageFoundation = _asmPatchCompiler.Apply(storagePackage, source, output);
                diagnostics.AddRange(storageFoundation.Diagnostics);
                if (!storageFoundation.IsSuccess)
                {
                    diagnostics.Add(Diagnostics.Error(
                        "ENHANCED_SAVE_STORAGE",
                        "Enhanced save storage could not reserve its verified PRG1 WRAM range."));
                }
                else
                {
                    output = storageFoundation.Value!;
                }
            }

            var expanded = _enhancedBuilder.Build(project, source, output);
            diagnostics.AddRange(expanded.Diagnostics);
            if (expanded.IsSuccess)
            {
                output = expanded.Value!.RomBytes;
            }
        }

        diagnostics.Add(project.OutputMode == RomOutputMode.EnhancedMmc3
            ? Diagnostics.Info("BUILD_VERIFIED", "The enhanced build preserves mapper 4 and verified fixed-bank placement; PRG size is explicitly expanded.")
            : Diagnostics.Info("BUILD_VERIFIED", "The compiled ROM preserves the source mapper and declared ROM size."));
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return OperationResult<BuildArtifact>.Failure(diagnostics.ToArray());
        }
        return OperationResult<BuildArtifact>.Success(new BuildArtifact(output, diagnostics), diagnostics);
    }

    public Prg1VanillaCapacityReport AnalyzeVanillaCapacity(ProjectDocumentV2 project, RomImage source)
    {
        if (!string.Equals(project.Source.Sha1, source.Sha1, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(project.Source.ProfileId, source.Profile.Id, StringComparison.Ordinal))
            return new Prg1VanillaCapacityReport(
                new Dictionary<string, Prg1AreaCapacity>(),
                [Diagnostics.Error("BUILD_SOURCE", "The selected source ROM does not match this project's verified source.")]);
        if (!string.Equals(source.Profile.Id, "us-prg1", StringComparison.Ordinal))
            return new Prg1VanillaCapacityReport(
                new Dictionary<string, Prg1AreaCapacity>(),
                [Diagnostics.Info("RELOC_PROFILE", "Shared vanilla relocation is currently verified for US PRG1 only.")]);

        var prepared = PrepareEditorOwnedBytes(project, source);
        if (!prepared.IsSuccess)
            return new Prg1VanillaCapacityReport(new Dictionary<string, Prg1AreaCapacity>(), prepared.Diagnostics);
        return project.StorageMode == RomStorageMode.ManagedVanilla
            ? Prg1VanillaStreamRelocator.Analyze(project, source, prepared.Value!)
            : AnalyzeFixedSlotCapacity(project, source);
    }

    private static Prg1VanillaCapacityReport AnalyzeFixedSlotCapacity(ProjectDocumentV2 project, RomImage source)
    {
        var areas = new Dictionary<string, Prg1AreaCapacity>(StringComparer.Ordinal);
        var diagnostics = new List<Diagnostic>();
        foreach (var pair in project.ModifiedAreas.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            if (!source.Profile.Levels.TryGetValue(pair.Key, out var location)) continue;
            var layout = Smb3LevelCodec.EncodeLayout(pair.Value);
            var sprites = Smb3LevelCodec.EncodeEnemies(pair.Value);
            diagnostics.AddRange(layout.Diagnostics);
            diagnostics.AddRange(sprites.Diagnostics);
            if (!layout.IsSuccess || !sprites.IsSuccess) continue;
            var layoutFits = layout.Value!.Length <= pair.Value.OriginalLayoutLength;
            var spriteFits = sprites.Value!.Length <= pair.Value.OriginalEnemyLength;
            areas[pair.Key] = new Prg1AreaCapacity(
                pair.Key, pair.Value.DisplayName, location.Tileset,
                new Prg1StreamCapacity(layout.Value.Length, pair.Value.OriginalLayoutLength, pair.Value.OriginalLayoutLength,
                    layout.Value.Length, pair.Value.OriginalLayoutLength, false, layoutFits),
                new Prg1StreamCapacity(sprites.Value.Length, pair.Value.OriginalEnemyLength, pair.Value.OriginalEnemyLength,
                    sprites.Value.Length, pair.Value.OriginalEnemyLength, false, spriteFits));
        }
        return new Prg1VanillaCapacityReport(areas, diagnostics);
    }

    private static bool IsPatchEnabled(PatchSettings settings, string id, IEnumerable<string> areaIds) =>
        settings.Get(id) is { } setting && (setting.EnabledByDefault || areaIds.Any(setting.IsEnabledFor));

    private static OperationResult<byte[]> PrepareEditorOwnedBytes(ProjectDocumentV2 project, RomImage source)
    {
        var diagnostics = new List<Diagnostic>();
        var output = source.Bytes.ToArray();
        foreach (var palette in project.PaletteOverrides ?? [])
        {
            if (palette.Tileset < 0 || palette.Tileset >= 19 || palette.Slot < 0 || palette.Slot >= (palette.Objects ? 4 : 8) || palette.Colors.Count != 16)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_PALETTE_SLOT", $"Palette slot {palette.Slot} is outside the vanilla {(palette.Objects ? "object" : "background")} palette range."));
                continue;
            }
            const int bank = 27;
            var pointerOffset = 16 + (bank * 0x2000) + 0x17D2 + (palette.Tileset * 2);
            var pointer = output[pointerOffset] | (output[pointerOffset + 1] << 8);
            var paletteOffset = pointer - 0xA000 + ((palette.Objects ? 8 : 0) + palette.Slot) * 16;
            var romOffset = 16 + (bank * 0x2000) + paletteOffset;
            if (paletteOffset < 0 || paletteOffset > 0x2000 - 16 || romOffset < 0 || romOffset + 16 > output.Length)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_PALETTE_RANGE", $"Palette slot {palette.Slot} for tileset {palette.Tileset} points outside the verified palette bank."));
                continue;
            }
            palette.Colors.ToArray().CopyTo(output, romOffset);
        }

        diagnostics.AddRange(Smb3OverworldSerializer.ApplyTileOverrides(project, source, output));
        diagnostics.AddRange(Smb3OverworldSerializer.ApplyNodeSetOverrides(project, source, output));
        diagnostics.AddRange(Smb3OverworldSerializer.ApplyLevelPointerOverrides(project, source, output));
        diagnostics.AddRange(Smb3OverworldSerializer.ApplyLockBridgeOverrides(project, source, output));
        diagnostics.AddRange(Smb3OverworldSerializer.ApplyMapSpriteOverrides(project, source, output));
        diagnostics.AddRange(Smb3OverworldSerializer.ApplyPaletteOverrides(project, source, output));
        return diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error)
            ? OperationResult<byte[]>.Failure(diagnostics.ToArray())
            : OperationResult<byte[]>.Success(output, diagnostics);
    }

}
