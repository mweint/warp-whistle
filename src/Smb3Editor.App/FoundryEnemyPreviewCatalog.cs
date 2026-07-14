namespace Smb3Editor.App;

/// <summary>
/// Mechanical subset of Foundry master/data/enemy_definitions.json, audited
/// 2026-07-14. These are only entries with a concrete block layout that lack
/// a sprite preview in EnemyMetadata; placeholders are intentionally omitted.
/// </summary>
internal static class FoundryEnemyPreviewCatalog
{
    private static readonly IReadOnlyDictionary<int, BlockPreview> Entries = new Dictionary<int, BlockPreview>
    {
        [7] = new(1, 1, [70]), // Z Special Hidden Whistle
        [8] = new(1, 2, [42, 43]), // Door when P-Switched
        [14] = new(2, 2, [7, 8, 23, 24]), // Boss Koopaling
        [93] = new(4, 8, [118, 118, 118, 118, 118, 118, 118, 118, 118, 118, 118, 0, 0, 118, 118, 118, 0, 118, 118, 0, 0, 118, 118, 0, 0, 0, 118, 0, 0, 118, 0, 0]), // Tornado
        [101] = new(1, 1, [129]), // Water Current Upward
        [102] = new(1, 1, [130]), // Water Current Downward
        [180] = new(1, 1, [164]), // Z Event Cheep Cheeps
        [181] = new(1, 1, [26]), // Z Event Spike Cheeps
        [182] = new(1, 1, [61]), // Z Event Lakitu Flee
        [183] = new(1, 1, [73]), // Z Event Parabeetles
        [184] = new(2, 1, [92, 93]), // Z Event BG Clouds
        [185] = new(3, 1, [55, 127, 57]), // Z Event Wood Platforms
        [188] = new(1, 1, [166]), // Cannon Bullet Bill
        [189] = new(1, 1, [167]), // Cannon Missile Bill
        [190] = new(1, 1, [18]), // Cannon Rocky Wrench
        [191] = new(1, 1, [119]), // Cannon 4-Way
        [192] = new(1, 1, [95]), // Cannon Goomba Pipe Left
        [193] = new(1, 1, [95]), // Cannon Goomba Pipe Right
        [194] = new(1, 1, [119]), // Cannon Left
        [195] = new(2, 2, [212, 213, 228, 229]), // Cannon Big Left
        [196] = new(1, 1, [119]), // Cannon Upper-Left
        [197] = new(1, 1, [119]), // Cannon Upper-Right
        [198] = new(1, 1, [119]), // Cannon Lower-Left
        [199] = new(1, 1, [119]), // Cannon Lower-Right
        [200] = new(1, 1, [119]), // Cannon Left
        [201] = new(1, 1, [119]), // Cannon Upper-Left
        [202] = new(1, 1, [119]), // Cannon Upper-Right
        [203] = new(1, 1, [119]), // Cannon Lower-Left
        [204] = new(1, 1, [119]), // Cannon Right
        [205] = new(2, 2, [212, 213, 228, 229]), // Cannon Big Right
        [206] = new(1, 1, [22]), // Cannon Bob-ombs Left
        [207] = new(1, 1, [22]), // Cannon Bob-ombs Right
        [208] = new(1, 1, [76]), // Cannon Laser
        [209] = new(3, 2, [140, 140, 140, 156, 156, 156]), // Z Special 3 Green Troopas
        [210] = new(1, 3, [205, 205, 205]), // Z Special 3 Orange Cheeps
        [211] = new(1, 1, [147]), // Auto-Scroll
        [212] = new(1, 1, [71]) // Bonus Controller
    };

    public static bool TryGet(int id, out BlockPreview preview) => Entries.TryGetValue(id, out preview!);
}

internal sealed record BlockPreview(int Width, int Height, IReadOnlyList<byte> Blocks);
