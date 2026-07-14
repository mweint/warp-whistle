namespace Smb3Editor.App;

using Smb3Editor.Core;

internal sealed record NamedLevelObject(int Id, string Name);

/// <summary>
/// Human-readable names for fixed level generators. These names are metadata
/// only; the ROM remains the source of the actual tiles and behavior.
/// </summary>
internal static class ObjectCatalogNames
{
    private static readonly IReadOnlyDictionary<byte, string> EnemyNames = new Dictionary<byte, string>
    {
        [0x41] = "Goal Card", [0x6C] = "Green Koopa", [0x6D] = "Red Koopa",
        [0x3F] = "Dry Bones", [0x4B] = "Boom Boom — Jumping", [0x4C] = "Boom Boom — Flying",
        [0x2F] = "Boo", [0x31] = "Boo Stretch", [0x32] = "Boo Stretch — Upside Down",
        [0x2E] = "Hidden Jumping Block Platform", [0x30] = "Hot Foot", [0x45] = "Hot Foot — Walking",
        [0x51] = "Roto-Disc — Dual Clockwise", [0x5A] = "Roto-Disc — Clockwise",
        [0x5B] = "Roto-Disc — Counter-clockwise", [0x5E] = "Roto-Disc — Dual Horizontal",
        [0x5F] = "Roto-Disc — Dual Vertical", [0x60] = "Roto-Disc — Dual Counter-clockwise",
        [0x6E] = "Green Paratroopa", [0x6F] = "Red Paratroopa", [0x70] = "Buzzy Beetle",
        [0x71] = "Spiny", [0x72] = "Goomba", [0x73] = "Paragoomba",
        [0x76] = "Cheep-Cheep", [0x77] = "Cheep-Cheep — Alternate",
        [0x78] = "Jumping Cheep-Cheep", [0x79] = "Bullet Bill", [0x9E] = "Podoboo",
        [0x9F] = "Parabeetle", [0xA0] = "Piranha Plant — Up",
        [0x8A] = "Thwomp", [0x8B] = "Thwomp — Left", [0x8C] = "Thwomp — Right",
        [0x8D] = "Thwomp — Vertical", [0x8E] = "Thwomp — Diagonal Up",
        [0x8F] = "Thwomp — Diagonal Down",
        [0xA1] = "Piranha Plant — Down", [0xA2] = "Venus Fire Trap — Up",
        [0xA3] = "Venus Fire Trap — Down", [0xA4] = "Red Piranha Plant — Up",
        [0xA5] = "Red Piranha Plant — Down", [0xA6] = "Red Venus Fire Trap — Up",
        [0xA7] = "Red Venus Fire Trap — Down"
    };

    private static readonly string[] PlainsVariableObjects =
    [
        "Big White Block to Ground", "Big Orange Block to Ground", "Big Green Block to Ground",
        "Big Blue Block to Ground", "Big White Block in Sky", "Big Orange Block in Sky",
        "Big Green Block in Sky", "Big Blue Block in Sky", "Small Bush Run", "Dry Pit",
        "Small Cloud Run", "Dry Ground", "Underwater Ground", "Large Background Cloud",
        "Underwater Pit", "Brick Run", "Question Block Coin Run", "Coin Brick Run",
        "Wood Block Run", "Green Note Block Run", "Note Block Run", "Bouncing Wood Block Run",
        "Coin Run", "Vertical Pipe to Alternate Area", "Vertical Pipe to Big Question Area",
        "Vertical Pipe (No Entrance)", "Ceiling Pipe to Alternate Area",
        "Ceiling Pipe (No Entrance)", "Right-Wall Pipe to Alternate Area",
        "Right-Wall Pipe (No Entrance)", "Left-Wall Pipe to Alternate Area",
        "Left-Wall Pipe (No Entrance)", "Bullet Bill Cannon", "Cheep-Cheep Bridge",
        "P-Switch Coins", "Waterfall", "Water Pool — Left Edge", "Water Pool — Still",
        "Water Pool — Right Edge", "Bowser Background", "Diamond Block Run", "Sandy Ground",
        "Orange Block Run", "Ice Brick Run", "Inter-Level Vertical Pipe", "Solid Cloud Run"
    ];

    private static readonly IReadOnlyDictionary<int, string> FortressVariableObjects = new Dictionary<int, string>
    {
        [0] = "Foreground Pillar", [1] = "Background Pillar", [2] = "Red Brick and Shadow",
        [3] = "Fortress Wall — Light Shadow", [4] = "Fortress Wall — Dark Shadow",
        [5] = "Short Windows", [6] = "Tall Windows", [7] = "Very Short Windows",
        [8] = "Hanging Globes", [9] = "Ghost Block Rectangle", [10] = "Stretch Boo Blocks",
        [11] = "Upward Spikes", [12] = "Downward Spikes",
        [13] = "Gray Diamond Block Rectangle", [14] = "Gray Diamond Block Rectangle (Tall)",
        [15] = "Brick Run", [16] = "Question Block Coin Run", [17] = "Coin Brick Run",
        [18] = "Wood Block Run", [19] = "Green Note Block Run", [20] = "Note Block Run",
        [21] = "Bouncing Wood Block Run", [22] = "Coin Run", [23] = "Vertical Pipe to Alternate Area",
        [24] = "Vertical Pipe to Big Question Area", [25] = "Vertical Pipe (No Entrance)",
        [26] = "Ceiling Pipe to Alternate Area", [27] = "Ceiling Pipe (No Entrance)",
        [28] = "Right-Wall Pipe to Alternate Area", [29] = "Right-Wall Pipe (No Entrance)",
        [30] = "Left-Wall Pipe to Alternate Area", [31] = "Left-Wall Pipe (No Entrance)",
        [32] = "Bullet Bill Cannon", [33] = "Cheep-Cheep Bridge", [34] = "P-Switch Coins"
    };

    // These generator IDs are shared by the gameplay tilesets. They were
    // previously exposed only for Plains/Fortress, which hid pipes and common
    // runs from Hills, desert, airship, and other level catalogs.
    private static readonly IReadOnlyList<NamedLevelObject> SharedVariableObjects =
        PlainsVariableObjects
            .Select((name, id) => new NamedLevelObject(id, name))
            .Where(item => item.Id is >= 15 and <= 45)
            .ToArray();

    private static readonly NamedLevelObject[] CommonObjects =
    [
        new(0x10, "Question Block — Fire Flower"),
        new(0x11, "Question Block — Leaf"),
        new(0x12, "Question Block — Star"),
        new(0x13, "Question Block — Coin / Star"),
        new(0x14, "Question Block — Coin"),
        new(0x15, "Muncher"),
        new(0x16, "Brick — Fire Flower"),
        new(0x17, "Brick — Leaf"),
        new(0x18, "Brick — Star"),
        new(0x19, "Brick — Coin / Star"),
        new(0x1A, "Brick — 10 Coins"),
        new(0x1B, "Brick — 1-Up"),
        new(0x1C, "Brick — Vine"),
        new(0x1D, "Brick — P-Switch"),
        new(0x1E, "Invisible Coin Block"),
        new(0x1F, "Invisible 1-Up Block"),
        new(0x20, "Invisible Note Block"),
        new(0x21, "Note Block — Fire Flower"),
        new(0x22, "Note Block — Leaf"),
        new(0x23, "Note Block — Star"),
        new(0x24, "Wood Block — Fire Flower"),
        new(0x25, "Wood Block — Leaf"),
        new(0x26, "Wood Block — Star"),
        new(0x27, "Invisible Orange Note Block"),
        new(0x28, "P-Switch"),
        new(0x29, "Goal Card")
    ];

    private static readonly NamedLevelObject[] PlainsObjects =
    [
        new(0x00, "Bush — Medium"),
        new(0x01, "Bush — Small"),
        new(0x02, "Bush — Large"),
        new(0x03, "Power-Up Clouds — Random"),
        new(0x04, "Door — Type 2"),
        new(0x05, "Door — Type 1"),
        new(0x06, "Vine to Ground"),
        new(0x07, "Small Background Cloud")
    ];

    private static readonly NamedLevelObject[] FortressObjects =
    [
        new(0x00, "Door — Type 2"),
        new(0x01, "Dark Red Background"),
        new(0x02, "Roto-Disc"),
        new(0x03, "Throne Room Background"),
        new(0x04, "Candle"),
        new(0x05, "Bowser Statue"),
        new(0x06, "Door — Type 1"),
        new(0x07, "Final Door")
    ];

    private static readonly NamedLevelObject[] HillsObjects =
    [
        new(0x00, "Upper-Left Corner — Above Ground"),
        new(0x01, "Upper-Left Corner — Underground / Hills"),
        new(0x02, "Upper-Left Corner — Water"),
        new(0x03, "Upper-Right Corner — Above Ground"),
        new(0x04, "Upper-Right Corner — Underground / Hills"),
        new(0x05, "Upper-Right Corner — Water"),
        new(0x06, "Lower-Left Corner — Above Ground"),
        new(0x07, "Lower-Left Corner — Underground / Hills"),
        new(0x08, "Lower-Left Corner — Water"),
        new(0x09, "Lower-Right Corner — Above Ground"),
        new(0x0A, "Lower-Right Corner — Underground / Hills"),
        new(0x0B, "Lower-Right Corner — Water"),
        new(0x0F, "Door — Type 2"),
        new(0x2A, "Bush — Medium"),
        new(0x2B, "Bush — Small"),
        new(0x2C, "Bush — Large"),
        new(0x2D, "Fill Background — Sky"),
        new(0x2E, "Fill Background — Underground"),
        new(0x2F, "Fill Background — Alternate"),
        new(0x30, "Slope Prefab — Down Right"),
        new(0x31, "Slope Prefab — Down Left"),
        new(0x32, "Thin 45° Slope — Rising"),
        new(0x33, "Thin 45° Slope — Falling"),
        new(0x34, "Thin Floor Prefab"),
        new(0x35, "Thin Wall Prefab"),
        new(0x36, "Thin 22.5° Slope — Rising"),
        new(0x37, "Thin 22.5° Slope — Falling"),
        new(0x38, "Downward Triangle Prefab"),
        new(0x39, "Upward Triangle Prefab"),
        new(0x3A, "Stairs — Rising"),
        new(0x3B, "Stairs — Falling"),
        new(0x3C, "Small Square Prefab"),
        new(0x3D, "Two Pools Prefab"),
        new(0x3E, "Two Pools — Step Up"),
        new(0x3F, "Two Pools — Step Down"),
        new(0x40, "Background Cloud"),
        new(0x41, "Background Decoration A"),
        new(0x42, "Background Decoration B")
    ];

    private static readonly NamedLevelObject[] HighUpObjects =
    [
        new(0x00, "Small Background Cloud"),
        new(0x01, "Wide Background Cloud"),
        new(0x02, "Suspension Cable — Right"),
        new(0x03, "Suspension Cable — Left"),
        new(0x04, "Wide Tree Trunk"),
        new(0x05, "Door — Type 2"),
        new(0x06, "Platform Puller — Unused"),
        new(0x07, "Platform Puller")
    ];

    private static readonly NamedLevelObject[] PlantObjects =
    [
        new(0x00, "Muncher Plant — Alternate 1"),
        new(0x01, "Muncher Plant — Alternate 2"),
        new(0x02, "Sky Cloud Background — 1"),
        new(0x03, "Clear Sky to Cloud Background"),
        new(0x04, "Starry Sky Background — Unused"),
        new(0x05, "Giant Background Hill"),
        new(0x06, "Sky Cloud Background — 2"),
        new(0x07, "Sky Cloud Background — 3")
    ];

    private static readonly NamedLevelObject[] PipeMazeObjects =
    [
        new(0x00, "Pipe Elbow — Upper Left"),
        new(0x01, "Pipe Elbow — Upper Right"),
        new(0x02, "Pipe Elbow — Lower Left"),
        new(0x03, "Pipe Elbow — Lower Right"),
        new(0x04, "Arrow Lift — Up"),
        new(0x05, "Arrow Lift — Right"),
        new(0x06, "Arrow Lift — Left"),
        new(0x07, "Arrow Lift — Random"),
        new(0x08, "Toad House Chest"),
        new(0x09, "Small Toad House Chest — Unused"),
        new(0x0A, "Door — Type 2"),
        new(0x0B, "Unknown Background — Unused 1"),
        new(0x0C, "Clear Pipe-Maze Background"),
        new(0x0D, "Fill Water Level"),
        new(0x0E, "Unknown Background — Unused 2"),
        new(0x0F, "Unknown Background — Unused 3")
    ];

    private static readonly NamedLevelObject[] DesertObjects =
    [
        new(0x00, "Small Pyramid Blocks to Ground"),
        new(0x01, "Medium Pyramid Blocks to Ground"),
        new(0x02, "Large Pyramid Blocks to Ground"),
        new(0x03, "Largest Pyramid Blocks to Ground"),
        new(0x04, "Background Palm Tree"),
        new(0x05, "Cannon Platform"),
        new(0x06, "Clear to Alternate Background"),
        new(0x07, "Breakable Pipe Link — Unused"),
        new(0x08, "Cracked Pipe"),
        new(0x09, "Pipeworks Ground Junction"),
        new(0x0A, "Desert Background Cloud"),
        new(0x0B, "Door — Type 2"),
        new(0x0C, "Background Pyramid to Ground")
    ];

    private static readonly NamedLevelObject[] AirshipObjects =
    [
        new(0x00, "Horizontal Screw"),
        new(0x01, "Horizontal Flame Jet"),
        new(0x02, "Vertical Flame Jet"),
        new(0x03, "Short Wood Tip"),
        new(0x04, "Stowed Anchor Background"),
        new(0x05, "Vertical Screw"),
        new(0x06, "Cannon — Left"),
        new(0x07, "Cannon — Right"),
        new(0x08, "Ceiling Cannon — Left"),
        new(0x09, "Ceiling Cannon — Right"),
        new(0x0A, "Four-Way Cannon — 90°"),
        new(0x0B, "Four-Way Cannon — 45°"),
        new(0x0C, "Wall Cannon — Forward"),
        new(0x0D, "Wall Cannon — Backward"),
        new(0x0E, "Metal Square"),
        new(0x0F, "Black Background — 14 Rows"),
        new(0x2A, "Long Wood Tip"),
        new(0x2B, "Wood Tip Underside"),
        new(0x2C, "Tank Prefab — 1"),
        new(0x2D, "Tank Prefab — 2"),
        new(0x2E, "Airship Intro Wood"),
        new(0x2F, "Ledge"),
        new(0x30, "Rocky Wrench Hole"),
        new(0x31, "World 8 Mini-Airship Prefab — 1"),
        new(0x32, "World 8 Mini-Airship Prefab — 2")
    ];

    public static IReadOnlyList<NamedLevelObject> ForTileset(int tileset)
    {
        var specific = tileset switch
        {
            1 => PlainsObjects,
            2 => FortressObjects,
            3 or 14 => HillsObjects,
            4 or 12 => HighUpObjects,
            5 or 11 or 13 => PlantObjects,
            6 or 7 or 8 => PipeMazeObjects,
            9 => DesertObjects,
            10 => AirshipObjects,
            _ => []
        };

        // Variable generators are returned separately by
        // VariableForTileset. Keeping them out of this fixed-object list
        // avoids duplicate catalog entries and, more importantly, prevents
        // selecting a shared pipe/run through the wrong encoder path.
        return specific
            .Concat(CommonObjects)
            .DistinctBy(static item => item.Id)
            .OrderBy(static item => item.Id)
            .ToArray();
    }

    public static IReadOnlyList<NamedLevelObject> VariableForTileset(int tileset)
    {
        var specific = tileset switch
        {
            1 => PlainsVariableObjects.Select((name, id) => new NamedLevelObject(id, name)),
            2 => FortressVariableObjects.Select(item => new NamedLevelObject(item.Key, item.Value)),
            _ => []
        };
        return SharedVariableObjects
            .Concat(specific)
            .DistinctBy(static item => item.Id)
            .OrderBy(static item => item.Id)
            .ToArray();
    }

    public static string Describe(int tileset, LevelElement element)
    {
        if (element.Kind == LevelElementKind.Junction)
        {
            return $"Junction {element.JunctionIndex} (read-only)\nX {element.X}, Y {element.Y}";
        }

        string? name;
        if (element.Kind == LevelElementKind.VariableGenerator)
        {
            name = SharedVariableObjects.FirstOrDefault(item => item.Id == element.GeneratorId)?.Name
                ?? (tileset == 1 && element.GeneratorId < PlainsVariableObjects.Length
                    ? PlainsVariableObjects[element.GeneratorId]
                    : tileset == 2 && FortressVariableObjects.TryGetValue(element.GeneratorId, out var fortressName)
                        ? fortressName
                        : $"Variable object ${element.GeneratorId:X2}");
        }
        else
        {
            name = ForTileset(tileset).FirstOrDefault(item => item.Id == element.GeneratorId)?.Name
                ?? $"Object ${element.GeneratorId:X2}";
        }
        return $"{name}\n{element.Kind} ${element.GeneratorId:X2} | X {element.X}, Y {element.Y}";
    }

    public static string Describe(EnemyElement enemy)
    {
        var name = Smb3LevelRenderer.GetEnemyName(enemy.Id)
            ?? EnemyNames.GetValueOrDefault(enemy.Id, $"Enemy ${enemy.Id:X2}");
        var description = Smb3LevelRenderer.GetEnemyDescription(enemy.Id);
        var details = $"Enemy ${enemy.Id:X2} | X {enemy.X}, Y {enemy.Y}";
        return string.IsNullOrWhiteSpace(description)
            ? $"{name}\n{details}"
            : $"{name}\n{description}\n{details}";
    }
}
