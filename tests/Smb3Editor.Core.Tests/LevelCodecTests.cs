namespace Smb3Editor.Core.Tests;

public sealed class LevelCodecTests
{
    [Fact]
    public void LayoutEncodingPreservesHeaderCommandsAndTerminator()
    {
        var document = CreateDocument();

        var encoded = Smb3LevelCodec.EncodeLayout(document);

        Assert.True(encoded.IsSuccess);
        Assert.Equal(
            new byte[]
            {
                0x34, 0x12, 0x78, 0x56, 0xA0, 0x09, 0x81, 0x01, 0x20,
                0x02, 0x33, 0xB4, 0x7F,
                0xFF
            },
            encoded.Value);
    }

    [Fact]
    public void EnemyEncodingIsThreeBytesPerEntryAndTerminated()
    {
        var document = CreateDocument();

        var encoded = Smb3LevelCodec.EncodeEnemies(document);

        Assert.True(encoded.IsSuccess);
        Assert.Equal(new byte[] { 0x01, 0x72, 0x20, 0x19, 0xFF }, encoded.Value);
    }

    [Fact]
    public void EnemyMovesKeepTheVanillaSpawnStreamOrdered()
    {
        var document = CreateDocument() with
        {
            Enemies =
            [
                new EnemyElement(0, 0x72, 32, 4),
                new EnemyElement(1, 0x40, 96, 4),
                new EnemyElement(2, 0x01, 160, 4)
            ]
        };

        var moved = document.MoveEnemy(2, 16, 4);
        moved = moved with { Enemies = moved.OrderEnemiesForSpawn(moved.Enemies) };

        Assert.Equal(new[] { 2, 0, 1 }, moved.Enemies.Select(enemy => enemy.Index));
        var encoded = Smb3LevelCodec.EncodeEnemies(moved);
        Assert.True(encoded.IsSuccess);
        Assert.Equal(new byte[] { 0x01, 0x01, 0x10, 0x04, 0x72, 0x20, 0x04, 0x40, 0x60, 0x04, 0xFF }, encoded.Value);
    }

    [Fact]
    public void VerticalEnemyMovesUseRowsForSpawnOrder()
    {
        var document = CreateDocument() with
        {
            Header = CreateDocument().Header with { SizeAndStartY = 0x01, TilesetAndScroll = 0x10 },
            Enemies =
            [
                new EnemyElement(0, 0x72, 1, 30),
                new EnemyElement(1, 0x40, 2, 15)
            ]
        };

        var moved = document.MoveEnemy(0, 1, 2);
        moved = moved with { Enemies = moved.OrderEnemiesForSpawn(moved.Enemies) };

        Assert.Equal(new[] { 0, 1 }, moved.Enemies.Select(enemy => enemy.Index));
        var encoded = Smb3LevelCodec.EncodeEnemies(moved);
        Assert.True(encoded.IsSuccess);
        Assert.Equal(new byte[] { 0x01, 0x72, 0x01, 0x02, 0x40, 0x02, 0x10, 0xFF }, encoded.Value);
    }

    [Fact]
    public void SizeUsesLowNibbleAndPreservesStartPositionBits()
    {
        var header = CreateDocument().Header with { SizeAndStartY = 0xEA };

        Assert.Equal(11, header.ScreenCount);
    }

    [Fact]
    public void HeaderEditsPreserveEveryUnrelatedAndReservedBit()
    {
        var original = new LevelHeader(0x1234, 0x5678, 0xB2, 0xE5, 0xD1, 0xA7, 0x35);

        var edited = original.WithEditableSettings(8, 2, 1, 9, 2);

        Assert.Equal((byte)0xB7, edited.SizeAndStartY);
        Assert.Equal((byte)0xEA, edited.PalettesAndStartX);
        Assert.Equal((byte)0xB9, edited.MusicAndTime);
        Assert.Equal(original.AlternateLayoutAddress, edited.AlternateLayoutAddress);
        Assert.Equal(original.AlternateEnemyAddress, edited.AlternateEnemyAddress);
        Assert.Equal(original.TilesetAndScroll, edited.TilesetAndScroll);
        Assert.Equal(original.BackgroundAndAction, edited.BackgroundAndAction);
    }

    [Fact]
    public void VerticalCoordinatesRoundTripThroughScreenEncoding()
    {
        var document = CreateDocument();
        document = document with
        {
            Header = document.Header with { TilesetAndScroll = (byte)(document.Header.TilesetAndScroll | 0x10) },
            Elements = [document.Elements[0] with { X = 3, Y = 31 }],
            Enemies = []
        };

        var encoded = Smb3LevelCodec.EncodeLayout(document);

        Assert.True(encoded.IsSuccess);
        Assert.Equal((byte)0x01, encoded.Value![9]);
        Assert.Equal((byte)0x23, encoded.Value[10]);
    }

    [Fact]
    public void HorizontalAxisEditsChangeTheirMatchingEncodedCoordinate()
    {
        var document = CreateDocument();
        var movedX = document with { Elements = [document.Elements[0] with { X = 0x34 }] };
        var movedY = document with { Elements = [document.Elements[0] with { Y = 4 }] };

        var encodedX = Smb3LevelCodec.EncodeLayout(movedX);
        var encodedY = Smb3LevelCodec.EncodeLayout(movedY);

        Assert.True(encodedX.IsSuccess);
        Assert.True(encodedY.IsSuccess);
        Assert.Equal((byte)0x02, encodedX.Value![9]);
        Assert.Equal((byte)0x34, encodedX.Value[10]);
        Assert.Equal((byte)0x04, encodedY.Value![9]);
        Assert.Equal((byte)0x33, encodedY.Value[10]);
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(15, 0x0F)]
    [InlineData(16, 0x10)]
    [InlineData(26, 0x1A)]
    [InlineData(31, 0x1F)]
    public void HorizontalGeneratorEncodingUsesAllFiveRowBits(int y, byte expectedFirst)
    {
        var element = CreateDocument().Elements[0] with
        {
            Y = y,
            OriginalFirstByte = 0x12,
            OriginalY = 18
        };
        var document = CreateDocument() with { Elements = [element] };

        var encoded = Smb3LevelCodec.EncodeLayout(document);

        Assert.True(encoded.IsSuccess, string.Join(Environment.NewLine, encoded.Diagnostics));
        Assert.Equal(expectedFirst, encoded.Value![9]);
        if (y > 26)
        {
            Assert.Contains(encoded.Diagnostics, static item => item.Code == "LEVEL_BOUNDS");
        }
    }

    [Fact]
    public void HorizontalXOnlyMovePreservesLowerHalfRowBit()
    {
        var element = CreateDocument().Elements[0] with
        {
            X = 0x34,
            Y = 18,
            OriginalFirstByte = 0x12,
            OriginalSecondByte = 0x33,
            OriginalX = 0x33,
            OriginalY = 18
        };
        var document = CreateDocument() with { Elements = [element] };

        var encoded = Smb3LevelCodec.EncodeLayout(document);

        Assert.True(encoded.IsSuccess);
        Assert.Equal((byte)0x12, encoded.Value![9]);
        Assert.Equal((byte)0x34, encoded.Value[10]);
    }

    [Fact]
    public void VerticalGeneratorMovePreservesFirstByteBitFour()
    {
        var document = CreateDocument();
        document = document with
        {
            Header = document.Header with { TilesetAndScroll = (byte)(document.Header.TilesetAndScroll | 0x10) },
            Elements =
            [
                document.Elements[0] with
                {
                    X = 4,
                    Y = 47,
                    OriginalFirstByte = 0x12,
                    OriginalSecondByte = 0x33,
                    OriginalX = 3,
                    OriginalY = 47
                }
            ],
            Enemies = []
        };

        var encoded = Smb3LevelCodec.EncodeLayout(document);

        Assert.True(encoded.IsSuccess);
        Assert.Equal((byte)0x12, encoded.Value![9]);
        Assert.Equal((byte)0x34, encoded.Value[10]);
    }

    [Fact]
    public void HorizontalEnemyMovePreservesFlags()
    {
        var document = CreateDocument() with
        {
            Enemies = [new EnemyElement(0, 0x72, 0x21, 26, 0xA0, 0x20, 0xB9, 0x20, 25)]
        };

        var encoded = Smb3LevelCodec.EncodeEnemies(document);

        Assert.True(encoded.IsSuccess);
        Assert.Equal(new byte[] { 0x01, 0x72, 0x21, 0xBA, 0xFF }, encoded.Value);
    }

    [Fact]
    public void VerticalEnemyCoordinatesUseScreenAndFifteenRowPacking()
    {
        var document = CreateDocument();
        document = document with
        {
            Header = document.Header with
            {
                SizeAndStartY = (byte)((document.Header.SizeAndStartY & 0xF0) | 0x07),
                TilesetAndScroll = (byte)(document.Header.TilesetAndScroll | 0x10)
            },
            Enemies = [new EnemyElement(0, 0x72, 14, 44, 0, 0x09, 0x27, 9, 37)]
        };

        var encoded = Smb3LevelCodec.EncodeEnemies(document);

        Assert.True(encoded.IsSuccess, string.Join(Environment.NewLine, encoded.Diagnostics));
        Assert.Equal(new byte[] { 0x01, 0x72, 0x0E, 0x2E, 0xFF }, encoded.Value);
    }

    [Fact]
    public void JunctionEncodingAlwaysPreservesItsTypedRawCommand()
    {
        var junction = new LevelElement(0, LevelElementKind.Junction, -1, 99, 99, 0xC4, null, 0xE2, 0x71, 0, 0);
        var document = CreateDocument() with { Elements = [junction] };

        var encoded = Smb3LevelCodec.EncodeLayout(document);

        Assert.True(encoded.IsSuccess);
        Assert.Equal(new byte[] { 0xE2, 0x71, 0xC4 }, encoded.Value![9..12]);
    }

    private static LevelDocument CreateDocument() => new(
        "test",
        "Test Area",
        1,
        new LevelHeader(0x1234, 0x5678, 0xA0, 0x09, 0x81, 0x01, 0x20),
        [new LevelElement(0, LevelElementKind.VariableGenerator, 11, 0x33, 2, 0xB4, 0x7F, 0x02, 0x33, 0x33, 2)],
        0x01,
        [new EnemyElement(0, 0x72, 0x20, 0x19)],
        14,
        5,
        ["Test Area"]);
}
