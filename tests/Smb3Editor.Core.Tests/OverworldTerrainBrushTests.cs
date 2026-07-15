using Smb3Editor.Core;

namespace Smb3Editor.Core.Tests;

public sealed class OverworldTerrainBrushTests
{
    [Fact]
    public void UsesVerifiedStockTilesForStraightStrokes()
    {
        var world = CreateWorld();

        var horizontal = OverworldTerrainBrush.ApplyPathStroke(world, [(1, 3), (2, 3), (3, 3)]);
        Assert.Equal(OverworldTerrainBrush.HorizontalPathTile, horizontal.TileAt(1, 3));
        Assert.Equal(OverworldTerrainBrush.HorizontalPathTile, horizontal.TileAt(2, 3));
        Assert.Equal(OverworldTerrainBrush.HorizontalPathTile, horizontal.TileAt(3, 3));

        var vertical = OverworldTerrainBrush.ApplyPathStroke(world, [(4, 3), (4, 4), (4, 5)]);
        Assert.Equal(OverworldTerrainBrush.VerticalPathTile, vertical.TileAt(4, 3));
        Assert.Equal(OverworldTerrainBrush.VerticalPathTile, vertical.TileAt(4, 4));
        Assert.Equal(OverworldTerrainBrush.VerticalPathTile, vertical.TileAt(4, 5));
    }

    [Fact]
    public void RetainsUnpaintedTilesIncluding42()
    {
        var world = CreateWorld().WithTile(10, 3, 0x42);

        var updated = OverworldTerrainBrush.ApplyPathStroke(world, [(1, 3), (2, 3)]);

        Assert.Equal(0x42, updated.TileAt(10, 3));
    }

    [Fact]
    public void UsesVerifiedCornerTileAtAPathTurn()
    {
        Assert.Equal(OverworldTerrainBrush.SouthEastCornerTile,
            OverworldTerrainBrush.ApplyPathStroke(CreateWorld(), [(1, 4), (2, 4), (2, 3)]).TileAt(2, 4));
        Assert.Equal(OverworldTerrainBrush.NorthWestCornerTile,
            OverworldTerrainBrush.ApplyPathStroke(CreateWorld(), [(2, 4), (2, 3), (3, 3)]).TileAt(2, 3));
        Assert.Equal(OverworldTerrainBrush.NorthEastCornerTile,
            OverworldTerrainBrush.ApplyPathStroke(CreateWorld(), [(1, 3), (2, 3), (2, 4)]).TileAt(2, 3));
        Assert.Equal(OverworldTerrainBrush.SouthWestCornerTile,
            OverworldTerrainBrush.ApplyPathStroke(CreateWorld(), [(2, 3), (2, 4), (3, 4)]).TileAt(2, 4));
    }

    [Fact]
    public void TogglesOnlyStraightAlternatePaths()
    {
        var horizontal = CreateWorld()
            .WithTile(1, 3, OverworldTerrainBrush.HorizontalPathTile)
            .WithTile(2, 3, OverworldTerrainBrush.NorthEastCornerTile)
            .WithTile(3, 3, OverworldTerrainBrush.HorizontalPathTile);
        Assert.Equal(OverworldTerrainBrush.AlternateHorizontalPathTile,
            OverworldTerrainBrush.ToggleAlternateAt(horizontal, 2, 3).TileAt(2, 3));

        var corner = CreateWorld()
            .WithTile(1, 3, OverworldTerrainBrush.HorizontalPathTile)
            .WithTile(2, 3, OverworldTerrainBrush.NorthEastCornerTile)
            .WithTile(2, 4, OverworldTerrainBrush.VerticalPathTile);
        Assert.Equal(OverworldTerrainBrush.NorthEastCornerTile,
            OverworldTerrainBrush.ToggleAlternateAt(corner, 2, 3).TileAt(2, 3));
    }

    [Fact]
    public void ApplyLakeStroke_UsesStockShorelineAndUpdatesJoinedCells()
    {
        var lake = OverworldTerrainBrush.ApplyLakeStroke(CreateWorld(), [(3, 3), (4, 3), (5, 3)]);

        Assert.Equal((byte)0xA1, lake.TileAt(3, 3));
        Assert.Equal((byte)0xA2, lake.TileAt(4, 3));
        Assert.Equal((byte)0xA3, lake.TileAt(5, 3));

        var joined = OverworldTerrainBrush.ApplyLakeStroke(lake, [(4, 4)]);
        Assert.Equal((byte)0x85, joined.TileAt(4, 3));
        Assert.Equal(OverworldTerrainBrush.WaterTile, joined.TileAt(4, 4));
    }

    [Theory]
    [InlineData(2, 2, 0x90)] // missing north-west
    [InlineData(4, 2, 0x8F)] // missing north-east
    [InlineData(4, 4, 0x87)] // missing south-east
    [InlineData(2, 4, 0x88)] // missing south-west
    public void ApplyLakeStroke_UsesStockConcaveShorelineTiles(int missingX, int missingY, byte expected)
    {
        var points = new List<(int X, int Y)>();
        for (var y = 2; y <= 4; y++)
        for (var x = 2; x <= 4; x++)
            if (x != missingX || y != missingY) points.Add((x, y));

        var lake = OverworldTerrainBrush.ApplyLakeStroke(CreateWorld(), points);

        Assert.Equal(expected, lake.TileAt(3, 3));
    }

    [Fact]
    public void EraseLakeStroke_Uses42AndReshapesNearbyWater()
    {
        var lake = OverworldTerrainBrush.ApplyLakeStroke(CreateWorld(), [(3, 3), (4, 3), (5, 3)]);
        var erased = OverworldTerrainBrush.EraseLakeStroke(lake, [(4, 3)]);

        Assert.Equal((byte)0x42, erased.TileAt(4, 3));
        // The stock set has no verified one-tile narrow endpoint; use its
        // ordinary water fallback rather than inventing a non-vanilla tile.
        Assert.Equal(OverworldTerrainBrush.WaterTile, erased.TileAt(3, 3));
        Assert.Equal(OverworldTerrainBrush.WaterTile, erased.TileAt(5, 3));
    }

    private static OverworldDocument CreateWorld() =>
        new(0, 0, 0, 0, 0, 0, false,
            Enumerable.Repeat((byte)0x42, OverworldDocument.ScreenWidth * OverworldDocument.ScreenHeight).ToArray(), [], []);
}
