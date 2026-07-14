namespace Smb3Editor.App;

using Smb3Editor.Core;

internal sealed record NamedLevelObject(int Id, string Name);

/// <summary>
/// Names for stock level commands. The catalog data is set-specific because
/// identical command IDs have different meanings in different object sets.
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

    public static IReadOnlyList<NamedLevelObject> ForTileset(int tileset) =>
        FoundryObjectCatalog.FixedForTileset(tileset);

    public static IReadOnlyList<NamedLevelObject> VariableForTileset(int tileset) =>
        FoundryObjectCatalog.VariableForTileset(tileset);

    public static string Describe(int tileset, LevelElement element)
    {
        if (element.Kind == LevelElementKind.Junction)
            return $"Junction {element.JunctionIndex} (read-only)\nX {element.X}, Y {element.Y}";

        var objects = element.Kind == LevelElementKind.VariableGenerator
            ? VariableForTileset(tileset)
            : ForTileset(tileset);
        var name = objects.FirstOrDefault(item => item.Id == element.GeneratorId)?.Name
            ?? $"{(element.Kind == LevelElementKind.VariableGenerator ? "Variable object" : "Object")} ${element.GeneratorId:X2}";
        return $"{name}\n{element.Kind} ${element.GeneratorId:X2} | X {element.X}, Y {element.Y}";
    }

    public static string Describe(EnemyElement enemy)
    {
        var name = Smb3LevelRenderer.GetEnemyName(enemy.Id)
            ?? EnemyNames.GetValueOrDefault(enemy.Id, $"Sprite ${enemy.Id:X2}");
        var description = Smb3LevelRenderer.GetEnemyDescription(enemy.Id);
        var details = $"Sprite ${enemy.Id:X2} | X {enemy.X}, Y {enemy.Y}";
        return string.IsNullOrWhiteSpace(description) ? $"{name}\n{details}" : $"{name}\n{description}\n{details}";
    }
}
