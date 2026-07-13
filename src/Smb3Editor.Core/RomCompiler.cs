namespace Smb3Editor.Core;

public sealed record BuildArtifact(byte[] RomBytes, IReadOnlyList<Diagnostic> Diagnostics);

public interface IRomCompiler
{
    OperationResult<BuildArtifact> Compile(ProjectDocumentV2 project, RomImage source);
}

public sealed class RomCompiler : IRomCompiler
{
    private readonly PatchCompiler _patchCompiler = new();

    public OperationResult<BuildArtifact> Compile(ProjectDocumentV2 project, RomImage source)
    {
        var diagnostics = new List<Diagnostic>();
        if (!string.Equals(project.Source.Sha1, source.Sha1, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(project.Source.ProfileId, source.Profile.Id, StringComparison.Ordinal))
        {
            return OperationResult<BuildArtifact>.Failure(
                Diagnostics.Error("BUILD_SOURCE", "The selected source ROM does not match this project's verified source."));
        }

        var output = source.Bytes.ToArray();
        foreach (var palette in project.PaletteOverrides ?? [])
        {
            if (palette.Tileset < 0 || palette.Tileset >= 19 || palette.Slot < 0 || palette.Slot >= (palette.Objects ? 4 : 8) || palette.Colors.Count != 16)
            {
                diagnostics.Add(Diagnostics.Error("BUILD_PALETTE_SLOT", $"Palette slot {palette.Slot} is outside the vanilla {(palette.Objects ? "object" : "background")} palette range."));
                continue;
            }
            var bank = 27;
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
            if (!layout.IsSuccess || !enemies.IsSuccess)
            {
                continue;
            }

            if (layout.Value!.Length > pair.Value.OriginalLayoutLength)
            {
                diagnostics.Add(Diagnostics.Error(
                    "BUILD_LAYOUT_SPACE",
                    $"{pair.Value.DisplayName} needs {layout.Value.Length} layout bytes but its verified slot has {pair.Value.OriginalLayoutLength}."));
                continue;
            }

            if (enemies.Value!.Length > pair.Value.OriginalEnemyLength)
            {
                diagnostics.Add(Diagnostics.Error(
                    "BUILD_ENEMY_SPACE",
                    $"{pair.Value.DisplayName} needs {enemies.Value.Length} enemy bytes but its verified slot has {pair.Value.OriginalEnemyLength}."));
                continue;
            }

            layout.Value.CopyTo(output, location.LayoutOffset);
            enemies.Value.CopyTo(output, location.EnemyOffset);
            diagnostics.Add(Diagnostics.Info(
                "BUILD_AREA_OK",
                $"Compiled {pair.Value.DisplayName}: {layout.Value.Length}/{pair.Value.OriginalLayoutLength} layout bytes, {enemies.Value.Length}/{pair.Value.OriginalEnemyLength} enemy bytes."));
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

        var patches = _patchCompiler.Apply(project, source, output);
        diagnostics.AddRange(patches.Diagnostics);
        if (!patches.IsSuccess)
        {
            return OperationResult<BuildArtifact>.Failure(diagnostics.ToArray());
        }
        output = patches.Value!;

        diagnostics.Add(Diagnostics.Info("BUILD_VERIFIED", "The compiled ROM preserves the source mapper and declared ROM size."));
        return OperationResult<BuildArtifact>.Success(new BuildArtifact(output, diagnostics), diagnostics);
    }
}
