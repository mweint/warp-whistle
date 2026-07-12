namespace Smb3Editor.Core.Tests;

public sealed class Smb3LevelRendererTests
{
    [Fact]
    public void OptionalVerifiedPrg1RendersWorldOneOneFromRomData()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
        var rom = loaded.Value!;
        var decoded = Smb3LevelCodec.Decode(rom, rom.Profile.Levels["W1-1"]);
        Assert.True(decoded.IsSuccess, string.Join(Environment.NewLine, decoded.Diagnostics));

        var rendered = new Smb3LevelRenderer().Render(rom, decoded.Value!);

        Assert.True(rendered.IsSuccess, string.Join(Environment.NewLine, rendered.Diagnostics));
        var document = decoded.Value!;
        var snapshot = rendered.Value!;
        Assert.Equal(176, snapshot.WidthInTiles);
        Assert.Equal(27, snapshot.HeightInTiles);
        Assert.True(snapshot.Metatiles.Distinct().Count() > 20);
        Assert.True(snapshot.ArgbPixels.Distinct().Count() > 8);
        Assert.Equal((byte)0x80, snapshot.Metatiles[1]);
        var skyColors = Enumerable.Range(0, 16)
            .SelectMany(y => Enumerable.Range(16, 16).Select(x => snapshot.ArgbPixels[(y * snapshot.PixelWidth) + x]))
            .Distinct()
            .ToArray();
        Assert.Single(skyColors);

        Assert.NotEmpty(snapshot.EnemySprites);
        Assert.Contains(snapshot.EnemySprites.Values, static preview =>
            preview.PixelWidth > 0 &&
            preview.PixelHeight > 0 &&
            preview.ArgbPixels.Any(static pixel => (pixel >> 24) != 0));
        Assert.Equal(document.Elements.Count, snapshot.ElementBounds.Count);
        Assert.Equal(document.Elements.Count, snapshot.ElementAnchors.Count);
        Assert.Contains(document.Elements, element =>
        {
            var bounds = snapshot.ElementBounds[element.Index];
            return bounds.Width > 1 || bounds.Height > 1;
        });
        var anchored = document.Elements.First(static element => element.GeneratorId == 16);
        var anchor = snapshot.ElementAnchors[anchored.Index];
        var anchorBounds = snapshot.ElementBounds[anchored.Index];
        Assert.InRange(anchor.X, anchorBounds.Left, anchorBounds.Right - 1);
        Assert.InRange(anchor.Y, anchorBounds.Top, anchorBounds.Bottom - 1);

        var movable = anchored;
        var edited = document.MoveElement(movable.Index, movable.X + 1, movable.Y);
        var editedRender = new Smb3LevelRenderer().Render(rom, edited);
        Assert.True(editedRender.IsSuccess, string.Join(Environment.NewLine, editedRender.Diagnostics));
        Assert.False(snapshot.Metatiles.SequenceEqual(editedRender.Value!.Metatiles));
    }

    [Fact]
    public void OptionalVerifiedPrg1RendersEveryCatalogStageWithoutEscapingSandbox()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
        var rom = loaded.Value!;
        var renderer = new Smb3LevelRenderer();
        var failures = new List<string>();
        foreach (var location in rom.Profile.Levels.Values)
        {
            var decoded = Smb3LevelCodec.Decode(rom, location);
            if (!decoded.IsSuccess)
            {
                failures.Add($"{location.DisplayName}: decode failed");
                continue;
            }

            var rendered = renderer.Render(rom, decoded.Value!);
            if (!rendered.IsSuccess)
            {
                failures.Add($"{location.DisplayName}: {string.Join("; ", rendered.Diagnostics)}");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void OptionalVerifiedPrg1CatalogHasNamedPreviewForEveryStockEnemy()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess);
        var rom = loaded.Value!;
        var ids = rom.Profile.Levels.Values
            .Select(location => Smb3LevelCodec.Decode(rom, location))
            .Where(static result => result.IsSuccess)
            .SelectMany(static result => result.Value!.Enemies)
            .Select(static enemy => enemy.Id)
            .Distinct()
            .Order()
            .ToArray();
        Assert.NotEmpty(ids);
        Assert.True(Smb3LevelRenderer.HasEnemyPreview(0x3F)); // Dry Bones
        Assert.True(Smb3LevelRenderer.HasEnemyPreview(0x4B)); // Boom Boom
        Assert.True(Smb3LevelRenderer.HasEnemyPreview(0x8A)); // Thwomp
    }

    [Fact]
    public void OptionalWorldOneOneLargePlatformRejectsUnsafeWidthAtBoundary()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess);
        var rom = loaded.Value!;
        var decoded = Smb3LevelCodec.Decode(rom, rom.Profile.Levels["W1-1"]);
        Assert.True(decoded.IsSuccess);
        var document = decoded.Value!;
        var renderer = new Smb3LevelRenderer();
        var element = document.Elements.Single(static item => item.Index == 12 && item.GeneratorId == 0);
        var originalRender = renderer.Render(rom, document);
        Assert.True(originalRender.IsSuccess);
        var moved = document.MoveElement(element.Index, 35, element.Y);
        var unsafeRender = renderer.Render(rom, moved);
        Assert.False(unsafeRender.IsSuccess);
        Assert.Contains(unsafeRender.Diagnostics, item =>
            item.Code == $"GENERATOR_UNSAFE:ELEMENT:{element.Index}" &&
            item.Message.Contains("layer", StringComparison.OrdinalIgnoreCase));
        Assert.True(renderer.Render(rom, moved.ResizeElement(element.Index, parameter: 2)).IsSuccess);
    }

}
