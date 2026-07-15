namespace Smb3Editor.Core.Tests;

public sealed class RomImageTests
{
    [Fact]
    public void VerifiedPrg0Nes20HeaderFingerprintMapsToPrg0Profile()
    {
        var profile = Smb3Profiles.FindBySha1("5f9019040fe23cb412a484e1ef430e59e589f9b4");

        Assert.NotNull(profile);
        Assert.Equal("us-prg0", profile.Id);
    }

    [Fact]
    public void OptionalVerifiedPrg0Nes20RomLoadsRendersAndCompilesAsPrg0()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_PRG0_NES20_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var result = RomImage.Load(path);

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Diagnostics));
        Assert.Equal("us-prg0", result.Value!.Profile.Id);
        Assert.Equal("5f9019040fe23cb412a484e1ef430e59e589f9b4", result.Value.Sha1);
        Assert.Equal(80, result.Value.Profile.Levels.Count);

        var location = result.Value.Profile.Levels["W1-1"];
        var decoded = Smb3LevelCodec.Decode(result.Value, location);
        Assert.True(decoded.IsSuccess, string.Join(Environment.NewLine, decoded.Diagnostics));
        var document = decoded.Value!;
        var element = document.Elements.First(static item => item.Kind != LevelElementKind.Junction);
        var moved = document.MoveElement(element.Index, element.X + 1, element.Y);
        var rendered = new Smb3LevelRenderer().Render(result.Value, moved);
        Assert.True(rendered.IsSuccess, string.Join(Environment.NewLine, rendered.Diagnostics));

        var compiled = new RomCompiler().Compile(ProjectDocumentV2.Create(result.Value).WithArea(moved), result.Value);
        Assert.True(compiled.IsSuccess, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Equal(result.Value.Bytes.Length, compiled.Value!.RomBytes.Length);
        Assert.Equal(result.Value.Bytes[..16], compiled.Value.RomBytes[..16]);
    }

    [Fact]
    public void TruncatedInputReturnsDiagnostic()
    {
        WithTemporaryFile([0x4E, 0x45], path =>
        {
            var result = RomImage.Load(path);

            Assert.False(result.IsSuccess);
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "ROM_TRUNCATED");
        });
    }

    [Fact]
    public void StructurallyValidButUnknownRomIsRejected()
    {
        var bytes = new byte[16 + (16 * 16_384) + (16 * 8_192)];
        bytes[0] = (byte)'N';
        bytes[1] = (byte)'E';
        bytes[2] = (byte)'S';
        bytes[3] = 0x1A;
        bytes[4] = 16;
        bytes[5] = 16;
        bytes[6] = 0x40;

        WithTemporaryFile(bytes, path =>
        {
            var result = RomImage.Load(path);

            Assert.False(result.IsSuccess);
            Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "ROM_UNSUPPORTED");
        });
    }

    [Fact]
    public void OptionalUserSuppliedRomProducesAResultWithoutThrowing()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        OperationResult<RomImage>? result = null;
        var exception = Record.Exception(() => result = RomImage.Load(path));

        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void OptionalUserSuppliedRomPointerTablesProduceBroadCatalog()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var bytes = File.ReadAllBytes(path);
        var declaredLength = 16 + (bytes[4] * 16_384) + (bytes[5] * 8_192);
        var catalog = RomCatalogBuilder.Build(bytes[..declaredLength]);

        Assert.True(catalog.IsSuccess, string.Join(Environment.NewLine, catalog.Diagnostics));
        Assert.Equal(80, catalog.Value!.Count);
        Assert.Contains("W1-1", catalog.Value.Keys);
        Assert.Contains("W8-C", catalog.Value.Keys);
    }

    [Fact]
    public void OptionalUserSuppliedRomCatalogStreamsDecodeWithoutEscapingBounds()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var bytes = File.ReadAllBytes(path);
        var declaredLength = 16 + (bytes[4] * 16_384) + (bytes[5] * 8_192);
        var normalized = bytes[..declaredLength];
        var catalog = RomCatalogBuilder.Build(normalized);
        Assert.True(catalog.IsSuccess);
        var profile = Smb3Profiles.FindById("us-prg1")! with { Levels = catalog.Value! };
        var image = RomImage.CreateForTesting(path, normalized, profile);

        var failures = profile.Levels.Values
            .Select(location => (location.DisplayName, Result: Smb3LevelCodec.Decode(image, location)))
            .Where(static item => !item.Result.IsSuccess)
            .Select(item => $"{item.DisplayName}: {string.Join("; ", item.Result.Diagnostics)}")
            .ToArray();

        Assert.Empty(failures);
    }

    [Fact]
    public void OptionalUserSuppliedRomCatalogRoundTripsEveryDecodedStream()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var bytes = File.ReadAllBytes(path);
        var declaredLength = 16 + (bytes[4] * 16_384) + (bytes[5] * 8_192);
        var normalized = bytes[..declaredLength];
        var catalog = RomCatalogBuilder.Build(normalized);
        Assert.True(catalog.IsSuccess);
        var profile = Smb3Profiles.FindById("us-prg1")! with { Levels = catalog.Value! };
        var image = RomImage.CreateForTesting(path, normalized, profile);
        var failures = new List<string>();

        foreach (var location in profile.Levels.Values)
        {
            var decoded = Smb3LevelCodec.Decode(image, location);
            if (!decoded.IsSuccess)
            {
                failures.Add($"{location.DisplayName}: decode failed");
                continue;
            }

            var document = decoded.Value!;
            var layout = Smb3LevelCodec.EncodeLayout(document);
            var enemies = Smb3LevelCodec.EncodeEnemies(document);
            if (!layout.IsSuccess || !image.Bytes.AsSpan(location.LayoutOffset, document.OriginalLayoutLength).SequenceEqual(layout.Value))
            {
                failures.Add($"{location.DisplayName}: layout did not round-trip");
            }

            if (!enemies.IsSuccess || !image.Bytes.AsSpan(location.EnemyOffset, document.OriginalEnemyLength).SequenceEqual(enemies.Value))
            {
                failures.Add($"{location.DisplayName}: enemies did not round-trip");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void OptionalUserSuppliedPrg1RomParsesAllOverworldMaps()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
        if (loaded.Value!.Profile.Id != "us-prg1")
        {
            return;
        }

        var parsed = Smb3OverworldParser.Parse(loaded.Value);
        Assert.True(parsed.IsSuccess, string.Join(Environment.NewLine, parsed.Diagnostics));
        Assert.Equal(9, parsed.Value!.Count);
        Assert.All(parsed.Value, map =>
        {
            Assert.InRange(map.ScreenCount, 1, 4);
            Assert.All(map.LevelPointers, pointer => Assert.InRange(pointer.Row, 2, 10));
            for (var screen = 0; screen < map.ScreenCount; screen++)
            for (var row = 0; row < OverworldDocument.ScreenHeight; row++)
            for (var column = 0; column < OverworldDocument.ScreenWidth; column++)
            {
                var x = (screen * OverworldDocument.ScreenWidth) + column;
                Assert.Equal(map.Tiles[(screen * OverworldDocument.ScreenWidth * OverworldDocument.ScreenHeight) + (row * OverworldDocument.ScreenWidth) + column], map.TileAt(x, row));
            }
        });
    }

    [Fact]
    public void OptionalUserSuppliedPrg1RomRendersEveryOverworldMapAndAnimationFrame()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
        if (loaded.Value!.Profile.Id != "us-prg1")
        {
            return;
        }

        var parsed = Smb3OverworldParser.Parse(loaded.Value);
        Assert.True(parsed.IsSuccess, string.Join(Environment.NewLine, parsed.Diagnostics));
        foreach (var map in parsed.Value!)
        {
            for (var frame = 0; frame < 4; frame++)
            {
                var rendered = Smb3OverworldRenderer.Render(loaded.Value, map, frame);
                Assert.True(rendered.IsSuccess, $"World {map.World + 1}, frame {frame}: {string.Join(Environment.NewLine, rendered.Diagnostics)}");
                Assert.Equal(map.Width * 16, rendered.Value!.PixelWidth);
                Assert.Equal(OverworldDocument.ScreenHeight * 16, rendered.Value.PixelHeight);
            }
        }
    }

    [Fact]
    public void OptionalUserSuppliedPrg1RomCompilesFixedSizeOverworldTileOverride()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
        var source = loaded.Value!;
        if (source.Profile.Id != "us-prg1") return;

        var maps = Smb3OverworldParser.Parse(source);
        Assert.True(maps.IsSuccess, string.Join(Environment.NewLine, maps.Diagnostics));
        var world = maps.Value![0];
        var tiles = world.Tiles.ToArray();
        tiles[0] ^= 0x01;
        var project = ProjectDocumentV2.Create(source) with
        {
            OverworldTiles = [new OverworldTileOverride(world.World, tiles)]
        };

        var compiled = new RomCompiler().Compile(project, source);
        Assert.True(compiled.IsSuccess, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Equal(tiles, compiled.Value!.RomBytes.AsSpan(world.LayoutOffset, tiles.Length).ToArray());
        Assert.Equal((byte)0xFF, compiled.Value.RomBytes[world.LayoutOffset + tiles.Length]);
    }

    [Fact]
    public void OptionalUserSuppliedPrg1RomCompilesFixedSizeOverworldNodeOverride()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
        var source = loaded.Value!;
        if (source.Profile.Id != "us-prg1") return;

        var maps = Smb3OverworldParser.Parse(source);
        Assert.True(maps.IsSuccess, string.Join(Environment.NewLine, maps.Diagnostics));
        var world = maps.Value![0];
        var original = world.LevelPointers[0];
        var changed = original with { Column = (original.Column + 1) & 0x0F };
        var project = ProjectDocumentV2.Create(source).WithOverworldLevelPointer(world.World, changed);

        var compiled = new RomCompiler().Compile(project, source);
        Assert.True(compiled.IsSuccess, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Equal((byte)((changed.Screen << 4) | changed.Column), compiled.Value!.RomBytes[original.PositionXOffset]);
        Assert.Equal(source.Bytes[original.PositionYOffset], compiled.Value.RomBytes[original.PositionYOffset]);
        Assert.Equal(source.Bytes[original.LevelPointerOffset], compiled.Value.RomBytes[original.LevelPointerOffset]);
        Assert.Equal(source.Bytes[original.EnemyPointerOffset], compiled.Value.RomBytes[original.EnemyPointerOffset]);
    }

    [Fact]
    public void OptionalUserSuppliedPrg1RomRebuildsVanillaOverworldNodePool()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
        var source = loaded.Value!;
        if (source.Profile.Id != "us-prg1") return;

        var maps = Smb3OverworldParser.Parse(source);
        Assert.True(maps.IsSuccess, string.Join(Environment.NewLine, maps.Diagnostics));
        var world = maps.Value![0];
        var nodes = world.LevelPointers.Skip(1).ToArray(); // Remove one node: this exercises table compaction.
        var project = ProjectDocumentV2.Create(source).WithOverworldNodes(world with { LevelPointers = nodes });

        var compiled = new RomCompiler().Compile(project, source);
        Assert.True(compiled.IsSuccess, string.Join(Environment.NewLine, compiled.Diagnostics));
        var reparsed = Smb3OverworldParser.Parse(RomImage.CreateForTesting(path, compiled.Value!.RomBytes, source.Profile));
        Assert.True(reparsed.IsSuccess, string.Join(Environment.NewLine, reparsed.Diagnostics));
        Assert.Equal(nodes.Length, reparsed.Value![0].LevelPointers.Count);
        Assert.Equal(nodes[0].Screen, reparsed.Value[0].LevelPointers[0].Screen);
        Assert.Equal(nodes[0].Column, reparsed.Value[0].LevelPointers[0].Column);
    }

    [Fact]
    public void OptionalUserSuppliedPrg1RomCompilesFixedSizeOverworldLockBridgeOverride()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
        var source = loaded.Value!;
        if (source.Profile.Id != "us-prg1") return;

        var maps = Smb3OverworldParser.Parse(source);
        Assert.True(maps.IsSuccess, string.Join(Environment.NewLine, maps.Diagnostics));
        var original = maps.Value!.First(map => map.LocksAndBridges.Count > 0).LocksAndBridges[0];
        var changed = original with { ReplacementTile = (byte)(original.ReplacementTile ^ 0x01) };
        var project = ProjectDocumentV2.Create(source).WithOverworldLockBridge(changed);

        var compiled = new RomCompiler().Compile(project, source);
        Assert.True(compiled.IsSuccess, string.Join(Environment.NewLine, compiled.Diagnostics));
        Assert.Equal(changed.ReplacementTile, compiled.Value!.RomBytes[original.ReplacementTileOffset]);
        Assert.Equal(source.Bytes[original.PositionOffset], compiled.Value.RomBytes[original.PositionOffset]);
        Assert.Equal(source.Bytes[original.RowOffset], compiled.Value.RomBytes[original.RowOffset]);
    }

    [Fact]
    public void OptionalUserSuppliedPrg1RomCompilesMapSpriteOverride()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
        var source = loaded.Value!;
        if (source.Profile.Id != "us-prg1") return;

        var maps = Smb3OverworldParser.Parse(source);
        Assert.True(maps.IsSuccess, string.Join(Environment.NewLine, maps.Diagnostics));
        var world1 = maps.Value![0];
        var sprite = world1.MapSprites.First(item => !item.IsAirshipSlot) with { Screen = 0, Column = 0, Row = OverworldDocument.FirstMapRow, Type = 3, Item = 1 };
        var project = ProjectDocumentV2.Create(source).WithOverworldMapSprite(sprite);

        var compiled = new RomCompiler().Compile(project, source);
        Assert.True(compiled.IsSuccess, string.Join(Environment.NewLine, compiled.Diagnostics));
        var reparsed = Smb3OverworldParser.Parse(RomImage.CreateForTesting(path, compiled.Value!.RomBytes, source.Profile));
        Assert.True(reparsed.IsSuccess, string.Join(Environment.NewLine, reparsed.Diagnostics));
        Assert.Equal((byte)3, reparsed.Value![0].MapSprites[sprite.Index].Type);
    }

    [Fact]
    public void OptionalUserSuppliedRomMutationSweepsEveryMovablePackedCoordinate()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
        var source = loaded.Value!;
        var normalized = source.Bytes;
        var profile = source.Profile;
        var working = normalized.ToArray();
        var mutationImage = RomImage.CreateForTesting(path, working, profile);
        var renderer = new Smb3LevelRenderer();
        var failures = new List<string>();

        foreach (var location in profile.Levels.Values)
        {
            var decoded = Smb3LevelCodec.Decode(source, location);
            if (!decoded.IsSuccess)
            {
                failures.Add($"{location.DisplayName}: baseline decode failed");
                continue;
            }

            var document = decoded.Value!;
            var unsignedLocation = location with { LayoutDataSignature = [], EnemySignature = [] };
            LevelDocument? representative = null;
            foreach (var element in document.Elements.Where(static item => item.Kind != LevelElementKind.Junction))
            {
                var maxX = document.Header.IsVertical ? 15 : 255;
                var maxY = document.Header.IsVertical ? (document.Header.ScreenCount * 15) - 1 : 26;
                var x = element.X < maxX ? element.X + 1 : Math.Max(0, element.X - 1);
                var y = element.Y < maxY ? element.Y + 1 : Math.Max(0, element.Y - 1);
                var moved = document.MoveElement(element.Index, x, y);
                var encoded = Smb3LevelCodec.EncodeLayout(moved);
                if (!encoded.IsSuccess)
                {
                    failures.Add($"{location.DisplayName}: element {element.Index} mutation did not encode");
                    continue;
                }

                encoded.Value!.CopyTo(working, location.LayoutOffset);
                var reparsed = Smb3LevelCodec.Decode(mutationImage, unsignedLocation);
                Array.Copy(normalized, location.LayoutOffset, working, location.LayoutOffset, document.OriginalLayoutLength);
                if (!reparsed.IsSuccess)
                {
                    failures.Add($"{location.DisplayName}: element {element.Index} mutation did not decode");
                    continue;
                }

                var actual = reparsed.Value!.Elements.First(item => item.Index == element.Index);
                if (actual.X != x || actual.Y != y || actual.GeneratorId != element.GeneratorId || actual.Shape != element.Shape)
                {
                    failures.Add($"{location.DisplayName}: element {element.Index} packed fields changed incorrectly");
                }

                representative ??= moved;
            }

            foreach (var enemy in document.Enemies)
            {
                var maxX = document.Header.IsVertical ? 15 : 255;
                var maxY = document.Header.IsVertical ? (document.Header.ScreenCount * 15) - 1 : 31;
                var x = enemy.X < maxX ? enemy.X + 1 : Math.Max(0, enemy.X - 1);
                var y = enemy.Y < maxY ? enemy.Y + 1 : Math.Max(0, enemy.Y - 1);
                var moved = document.MoveEnemy(enemy.Index, x, y);
                var encoded = Smb3LevelCodec.EncodeEnemies(moved);
                if (!encoded.IsSuccess)
                {
                    failures.Add($"{location.DisplayName}: enemy {enemy.Index} mutation did not encode");
                    continue;
                }

                encoded.Value!.CopyTo(working, location.EnemyOffset);
                var reparsed = Smb3LevelCodec.Decode(mutationImage, unsignedLocation);
                Array.Copy(normalized, location.EnemyOffset, working, location.EnemyOffset, document.OriginalEnemyLength);
                if (!reparsed.IsSuccess)
                {
                    failures.Add($"{location.DisplayName}: enemy {enemy.Index} mutation did not decode");
                    continue;
                }

                var actual = reparsed.Value!.Enemies.First(item => item.Index == enemy.Index);
                if (actual.X != x || actual.Y != y || actual.Id != enemy.Id || actual.Flags != enemy.Flags)
                {
                    failures.Add($"{location.DisplayName}: enemy {enemy.Index} packed fields changed incorrectly");
                }

                representative ??= moved;
            }

            if (representative is not null)
            {
                var rendered = renderer.Render(source, representative);
                if (!rendered.IsSuccess)
                {
                    failures.Add($"{location.DisplayName}: representative mutation did not render safely");
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void OptionalVerifiedRomVerticalPipeMazeUsesFullEnemyScreenCoordinates()
    {
        var path = Environment.GetEnvironmentVariable("SMB3_TEST_ROM");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        var loaded = RomImage.Load(path);
        Assert.True(loaded.IsSuccess, string.Join(Environment.NewLine, loaded.Diagnostics));
        var rom = loaded.Value!;
        var location = new LevelLocation(
            "W7-1-MAZE",
            "World 7-1 Vertical Pipe Maze",
            8,
            0x24BA7,
            512,
            0x0CDA3,
            256,
            new HashSet<int> { 35, 36, 37, 38, 39, 40, 41, 42, 49, 57 },
            [],
            []);

        var decoded = Smb3LevelCodec.Decode(rom, location);

        Assert.True(decoded.IsSuccess, string.Join(Environment.NewLine, decoded.Diagnostics));
        var document = decoded.Value!;
        Assert.True(document.Header.IsVertical);
        Assert.Contains(document.Enemies, static enemy => enemy.Y == 37);
        Assert.Contains(document.Enemies, static enemy => enemy.Y == 103);

        var enemy = document.Enemies.First(static item => item.Y == 37);
        var moved = document.MoveEnemy(enemy.Index, enemy.X - 1, 52);
        var encoded = Smb3LevelCodec.EncodeEnemies(moved);
        Assert.True(encoded.IsSuccess, string.Join(Environment.NewLine, encoded.Diagnostics));

        var working = rom.Bytes.ToArray();
        encoded.Value!.CopyTo(working, location.EnemyOffset);
        var profile = rom.Profile with
        {
            Levels = new Dictionary<string, LevelLocation> { [location.AreaId] = location }
        };
        var mutationImage = RomImage.CreateForTesting(path, working, profile);
        var reparsed = Smb3LevelCodec.Decode(mutationImage, location);
        Assert.True(reparsed.IsSuccess, string.Join(Environment.NewLine, reparsed.Diagnostics));
        var actual = reparsed.Value!.Enemies.First(item => item.Index == enemy.Index);
        Assert.Equal(enemy.X - 1, actual.X);
        Assert.Equal(52, actual.Y);
        Assert.Equal(enemy.Id, actual.Id);

        var rendered = new Smb3LevelRenderer().Render(rom, moved);
        Assert.True(rendered.IsSuccess, string.Join(Environment.NewLine, rendered.Diagnostics));
    }

    [Fact]
    public void HillsCatalogFourByteGeneratorIsEncodedAsFourBytes()
    {
        var profile = Smb3Profiles.FindById("us-prg1")!;
        var hills = profile.Levels["W1-2"];

        Assert.Equal(3, hills.Tileset);
        Assert.Contains(80, hills.FourByteGeneratorIds);
    }

    private static void WithTemporaryFile(byte[] bytes, Action<string> test)
    {
        var path = Path.Combine(Path.GetTempPath(), $"smb3-editor-{Guid.NewGuid():N}.nes");
        try
        {
            File.WriteAllBytes(path, bytes);
            test(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
