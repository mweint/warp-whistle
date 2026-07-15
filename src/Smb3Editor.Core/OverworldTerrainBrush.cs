namespace Smb3Editor.Core;

/// <summary>
/// Verified stock world-map road tiles. The four corners are part of the
/// original map tile set and retain their normal vanilla movement semantics.
/// </summary>
public static class OverworldTerrainBrush
{
    public const byte HorizontalPathTile = 0x45;
    public const byte VerticalPathTile = 0x46;
    public const byte NorthWestCornerTile = 0x44;
    public const byte NorthEastCornerTile = 0x47;
    public const byte SouthWestCornerTile = 0x48;
    public const byte SouthEastCornerTile = 0x4A;
    public const byte AlternateHorizontalPathTile = 0x49;

    // Stock overworld water/shoreline family. The representative tiles below
    // were derived from the PRG1 stock maps: each is the most common member
    // of its visible water-edge topology. They remain ordinary vanilla map
    // tiles; the editor never creates a new terrain or collision type.
    public const byte WaterTile = 0x8D;

    /// <summary>
    /// Applies one continuous path stroke using stock straight and corner
    /// tiles. Crossings are intentionally left unchanged because the stock
    /// road set does not provide one generic four-way crossing tile.
    /// </summary>
    public static OverworldDocument ApplyPathStroke(
        OverworldDocument world,
        IReadOnlyList<(int X, int Y)> stroke)
    {
        if (stroke.Count == 0) return world;

        var points = stroke
            .Where(point => point.X >= 0 && point.X < world.Width && point.Y >= 0 && point.Y < OverworldDocument.ScreenHeight)
            .Distinct()
            .ToArray();
        if (points.Length == 0) return world;

        var tiles = world.Tiles.ToArray();
        for (var index = 0; index < points.Length; index++)
        {
            var current = points[index];
            var north = HasConnection(world, tiles, current, 0, -1) || ConnectsTo(points, index, 0, -1);
            var east = HasConnection(world, tiles, current, 1, 0) || ConnectsTo(points, index, 1, 0);
            var south = HasConnection(world, tiles, current, 0, 1) || ConnectsTo(points, index, 0, 1);
            var west = HasConnection(world, tiles, current, -1, 0) || ConnectsTo(points, index, -1, 0);
            var connections = (north ? 1 : 0) + (east ? 1 : 0) + (south ? 1 : 0) + (west ? 1 : 0);
            if (connections > 2) continue;

            tiles[world.TileIndex(current.X, current.Y)] = (north, east, south, west) switch
            {
                // The visual corner names describe the open quadrant, not
                // the two directions entering the tile.
                (true, false, false, true) => SouthEastCornerTile,
                (true, true, false, false) => SouthWestCornerTile,
                (false, false, true, true) => NorthEastCornerTile,
                (false, true, true, false) => NorthWestCornerTile,
                // Endpoints use the direction of their one connected segment;
                // otherwise a vertical stroke would incorrectly start/end as
                // the horizontal road tile $45.
                (true, false, true, false) or (true, false, false, false) or (false, false, true, false) => VerticalPathTile,
                _ => HorizontalPathTile
            };
        }

        return world.WithTiles(tiles);
    }

    /// <summary>
    /// Toggles the stock alternate road graphics only when their immediate
    /// neighbours prove the tile is a straight segment.  `$47` and `$48` are
    /// also legitimate corners, so a corner is deliberately left unchanged.
    /// </summary>
    public static OverworldDocument ToggleAlternateAt(OverworldDocument world, int x, int y)
    {
        if (x < 0 || x >= world.Width || y < 0 || y >= OverworldDocument.ScreenHeight) return world;
        var tile = world.TileAt(x, y);
        var horizontal = HasConnection(world, world.Tiles, (x, y), -1, 0) && HasConnection(world, world.Tiles, (x, y), 1, 0);
        var vertical = HasConnection(world, world.Tiles, (x, y), 0, -1) && HasConnection(world, world.Tiles, (x, y), 0, 1);

        return tile switch
        {
            AlternateHorizontalPathTile => world.WithTile(x, y, NorthEastCornerTile),
            NorthEastCornerTile when horizontal => world.WithTile(x, y, AlternateHorizontalPathTile),
            VerticalPathTile when vertical => world.WithTile(x, y, SouthWestCornerTile),
            SouthWestCornerTile when vertical => world.WithTile(x, y, VerticalPathTile),
            _ => world
        };
    }

    /// <summary>
    /// Paints a stock lake. Newly painted cells and their existing water
    /// neighbours are re-shaped together so a continuous stroke gains the
    /// appropriate stock shoreline tiles instead of a rectangle of one tile.
    /// </summary>
    public static OverworldDocument ApplyLakeStroke(
        OverworldDocument world,
        IReadOnlyList<(int X, int Y)> stroke)
    {
        var painted = stroke
            .Where(point => point.X >= 0 && point.X < world.Width && point.Y >= 0 && point.Y < OverworldDocument.ScreenHeight)
            .ToHashSet();
        if (painted.Count == 0) return world;

        var candidates = new HashSet<(int X, int Y)>(painted);
        foreach (var point in painted.ToArray())
        foreach (var (xOffset, yOffset) in SurroundingDirections)
        {
            var neighbour = (X: point.X + xOffset, Y: point.Y + yOffset);
            if (neighbour.X >= 0 && neighbour.X < world.Width && neighbour.Y >= 0 && neighbour.Y < OverworldDocument.ScreenHeight &&
                IsWaterTile(world.TileAt(neighbour.X, neighbour.Y)))
                candidates.Add(neighbour);
        }

        var tiles = world.Tiles.ToArray();
        bool HasWater(int x, int y) =>
            x >= 0 && x < world.Width && y >= 0 && y < OverworldDocument.ScreenHeight &&
            (painted.Contains((x, y)) || IsWaterTile(tiles[world.TileIndex(x, y)]));

        foreach (var point in candidates)
        {
            tiles[world.TileIndex(point.X, point.Y)] = WaterTileForTopology(BuildWaterTopology(HasWater, point));
        }

        return world.WithTiles(tiles);
    }

    /// <summary>Erases terrain to the designer-selected stock blank `$42` and
    /// re-shapes the immediately adjacent stock water edge.</summary>
    public static OverworldDocument EraseLakeStroke(
        OverworldDocument world,
        IReadOnlyList<(int X, int Y)> stroke)
    {
        var erased = stroke.Where(point => point.X >= 0 && point.X < world.Width && point.Y >= 0 && point.Y < OverworldDocument.ScreenHeight)
            .ToHashSet();
        if (erased.Count == 0) return world;
        var tiles = world.Tiles.ToArray();
        foreach (var point in erased) tiles[world.TileIndex(point.X, point.Y)] = 0x42;

        var candidates = new HashSet<(int X, int Y)>();
        foreach (var point in erased)
        foreach (var (xOffset, yOffset) in SurroundingDirections)
        {
            var neighbour = (X: point.X + xOffset, Y: point.Y + yOffset);
            if (neighbour.X >= 0 && neighbour.X < world.Width && neighbour.Y >= 0 && neighbour.Y < OverworldDocument.ScreenHeight &&
                IsWaterTile(tiles[world.TileIndex(neighbour.X, neighbour.Y)]))
                candidates.Add(neighbour);
        }
        foreach (var point in candidates)
        {
            bool HasWater(int x, int y) => x >= 0 && x < world.Width && y >= 0 && y < OverworldDocument.ScreenHeight &&
                IsWaterTile(tiles[world.TileIndex(x, y)]);
            tiles[world.TileIndex(point.X, point.Y)] = WaterTileForTopology(BuildWaterTopology(HasWater, point));
        }
        return world.WithTiles(tiles);
    }

    public static bool IsWaterTile(byte tile) => tile is >= 0x82 and <= 0xA9;

    // The first four bits describe cardinal continuity. The low nibble holds
    // NE/SE/SW/NW and selects the stock concave shoreline variants when a
    // filled lake has a one-tile notch. These four variants are present in
    // the authenticated PRG1 maps; no non-stock narrow end tile is invented.
    private static byte WaterTileForTopology(int topology)
    {
        var cardinal = topology >> 4;
        if (cardinal == 0b1111)
        {
            // A single missing diagonal is a concave (inside) lake corner.
            // Preserve the normal interior for more complex diagonal shapes;
            // the stock set has no general purpose tile for every such mask.
            var diagonals = topology & 0x0F;
            return diagonals switch
            {
                0b1110 => 0x90, // missing north-west
                0b0111 => 0x8F, // missing north-east
                0b1011 => 0x87, // missing south-east
                0b1101 => 0x88, // missing south-west
                _ => WaterTile
            };
        }

        return cardinal switch
        {
            0b1110 => 0x8C, // N/E/S
            0b1101 => 0x95, // N/E/W
            0b1011 => 0x8E, // N/S/W
            0b0111 => 0x85, // E/S/W
            0b1100 => 0x94, // N/E
            0b1001 => 0x96, // N/W
            0b0110 => 0x84, // E/S
            0b0011 => 0x86, // S/W
            0b1010 => 0x9D, // N/S
            0b0101 => 0xA2, // E/W
            0b0100 => 0xA1, // E
            0b0001 => 0xA3, // W
            _ => WaterTile // no verified generic narrow vertical cap
        };
    }

    private static int BuildWaterTopology(Func<int, int, bool> hasWater, (int X, int Y) point) =>
        (hasWater(point.X, point.Y - 1) ? 0x80 : 0) |
        (hasWater(point.X + 1, point.Y) ? 0x40 : 0) |
        (hasWater(point.X, point.Y + 1) ? 0x20 : 0) |
        (hasWater(point.X - 1, point.Y) ? 0x10 : 0) |
        (hasWater(point.X + 1, point.Y - 1) ? 0x08 : 0) |
        (hasWater(point.X + 1, point.Y + 1) ? 0x04 : 0) |
        (hasWater(point.X - 1, point.Y + 1) ? 0x02 : 0) |
        (hasWater(point.X - 1, point.Y - 1) ? 0x01 : 0);

    private static readonly (int X, int Y)[] CardinalDirections = [(0, -1), (1, 0), (0, 1), (-1, 0)];
    private static readonly (int X, int Y)[] SurroundingDirections =
        [(0, -1), (1, 0), (0, 1), (-1, 0), (1, -1), (1, 1), (-1, 1), (-1, -1)];

    private static bool ConnectsTo(IReadOnlyList<(int X, int Y)> points, int index, int xOffset, int yOffset) =>
        (index > 0 && points[index - 1] == (points[index].X + xOffset, points[index].Y + yOffset)) ||
        (index + 1 < points.Count && points[index + 1] == (points[index].X + xOffset, points[index].Y + yOffset));

    private static bool HasConnection(OverworldDocument world, IReadOnlyList<byte> tiles, (int X, int Y) source, int xOffset, int yOffset)
    {
        var x = source.X + xOffset;
        var y = source.Y + yOffset;
        if (x < 0 || x >= world.Width || y < 0 || y >= OverworldDocument.ScreenHeight) return false;
        var neighbor = tiles[world.TileIndex(x, y)];
        return neighbor switch
        {
            HorizontalPathTile => yOffset == 0,
            VerticalPathTile => xOffset == 0,
            NorthWestCornerTile => (xOffset, yOffset) is (0, -1) or (-1, 0),
            NorthEastCornerTile => (xOffset, yOffset) is (0, -1) or (1, 0),
            SouthWestCornerTile => (xOffset, yOffset) is (0, 1) or (-1, 0),
            SouthEastCornerTile => (xOffset, yOffset) is (0, 1) or (1, 0),
            _ => false
        };
    }
}
