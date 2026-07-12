namespace Smb3Editor.Core;

internal static class RomCatalogBuilder
{
    private static readonly int[] LayoutBankByTileset =
    [
        11, 15, 21, 16, 17, 19, 18, 18, 18, 20, 23, 19, 17, 19, 13, 26, 26, 26, 9
    ];

    private static readonly IReadOnlyDictionary<int, IReadOnlySet<int>> FourByteGenerators =
        new Dictionary<int, IReadOnlySet<int>>
        {
            [1] = Set(11, 12, 35, 36, 37, 38, 39, 40, 41, 42),
            [2] = Set(13, 14, 35, 36, 37, 38, 39, 40, 41, 42, 46, 47, 48, 57),
            [3] = Set(35, 36, 37, 38, 39, 40, 41, 42, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71),
            [4] = Set(0, 35, 36, 37, 38, 39, 40, 41, 42, 54),
            [5] = Set(13, 35, 36, 37, 38, 39, 40, 41, 42, 45, 46),
            [6] = Set(35, 36, 37, 38, 39, 40, 41, 42, 49, 57),
            [7] = Set(35, 36, 37, 38, 39, 40, 41, 42, 49, 57),
            [8] = Set(35, 36, 37, 38, 39, 40, 41, 42, 49, 57),
            [9] = Set(10, 11, 12, 13, 35, 36, 37, 38, 39, 40, 41, 42),
            [10] = Set(1, 2, 35, 36, 37, 38, 39, 40, 41, 42, 48, 51),
            [11] = Set(13, 35, 36, 37, 38, 39, 40, 41, 42, 45, 46),
            [12] = Set(0, 35, 36, 37, 38, 39, 40, 41, 42, 54),
            [13] = Set(13, 35, 36, 37, 38, 39, 40, 41, 42, 45, 46),
            [14] = Set(35, 36, 37, 38, 39, 40, 41, 42, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71),
            [15] = Set(),
            [18] = Set()
        };

    private static readonly WorldSpec[] Worlds =
    [
        new(1,
            [33,35,35,33,35,68,67,67,68,71,99,98,111,131,135,131,131,130,174,163,164],
            [S(0,"W1-1","World 1-1"), S(2,"W1-2","World 1-2"), S(3,"W1-3","World 1-3"), S(8,"W1-4","World 1-4"), S(11,"W1-F","World 1 Fortress"), S(18,"W1-5","World 1-5"), S(20,"W1-6","World 1-6")]),
        new(2,
            [47,41,35,41,41,39,73,73,73,73,73,73,105,105,105,105,110,137,135,142,137,137,163,169,169,169,175,41,41,41,41,39,67,73,73,105,98,105,105,105,137,137,137,137,169,169,169],
            [S(2,"W2-2","World 2-2"), S(10,"W2-3","World 2-3"), S(12,"W2-1","World 2-1"), S(13,"W2-F","World 2 Fortress"), S(28,"W2-4","World 2-4"), S(40,"W2-5","World 2-5"), S(42,"W2-PY","World 2 Pyramid")]),
        new(3,
            [39,33,33,33,39,33,33,68,65,78,65,97,111,98,97,97,97,134,129,142,134,129,161,174,175,161,174,36,33,33,33,78,65,79,66,95,87,97,97,110,110,115,119,127,129,129,129,33,39,130,129,142],
            [S(3,"W3-3","World 3-3"), S(7,"W3-2","World 3-2"), S(9,"W3-4","World 3-4"), S(13,"W3-F1","World 3 Fortress 1"), S(17,"W3-1","World 3-1"), S(20,"W3-5","World 3-5"), S(27,"W3-6","World 3-6"), S(29,"W3-7","World 3-7"), S(31,"W3-8","World 3-8"), S(38,"W3-9","World 3-9")]),
        new(4,
            [75,78,71,65,75,107,98,107,107,98,142,143,139,139,75,78,66,75,75,75,75,107,107,107,111,103,107,139,139,139,135,142,139,167],
            [S(3,"W4-6","World 4-6"), S(9,"W4-F2","World 4 Fortress 2"), S(12,"W4-5","World 4-5"), S(16,"W4-F1","World 4 Fortress 1"), S(18,"W4-3","World 4-3"), S(20,"W4-2","World 4-2"), S(29,"W4-4","World 4-4"), S(32,"W4-1","World 4-1")]),
        new(5,
            [35,46,39,33,35,65,78,78,67,67,66,99,98,99,99,99,131,131,143,131,39,77,77,68,109,109,109,109,111,143,141,130,141,141,141,162,173,173,173,173,167,173],
            [S(1,"W5-2","World 5-2"), S(3,"W5-3","World 5-3"), S(5,"W5-1","World 5-1"), S(10,"W5-T1","World 5 Tower"), S(12,"W5-F1","World 5 Fortress"), S(23,"W5-5","World 5-5"), S(26,"W5-4","World 5-4"), S(33,"W5-7","World 5-7"), S(37,"W5-9","World 5-9"), S(38,"W5-8","World 5-8"), S(41,"W5-6","World 5-6")]),
        new(6,
            [39,76,76,76,78,108,108,108,111,98,108,108,142,140,140,140,143,46,44,44,76,76,76,110,108,108,108,108,108,110,108,140,140,135,140,140,140,140,172,172,172,172,175,172,76,76,76,108,98,108,108,108,108,98,140,140,140],
            [S(2,"W6-2","World 6-2"), S(7,"W6-1","World 6-1"), S(9,"W6-F1","World 6 Fortress 1"), S(15,"W6-3","World 6-3"), S(25,"W6-6","World 6-6"), S(27,"W6-F2","World 6 Fortress 2"), S(29,"W6-8","World 6-8"), S(31,"W6-4","World 6-4"), S(37,"W6-7","World 6-7"), S(39,"W6-5","World 6-5"), S(44,"W6-9","World 6-9"), S(48,"W6-F3","World 6 Fortress 3"), S(56,"W6-10","World 6-10")]),
        new(7,
            [49,49,49,62,62,50,81,94,94,87,94,85,81,126,126,113,113,127,113,153,158,158,147,63,49,49,49,62,78,94,94,95,81,81,87,113,126,126,121,113,114,158,151,146,145,149],
            [S(5,"W7-F1","World 7 Fortress 1"), S(6,"W7-1","World 7-1"), S(11,"W7-I1","World 7 Piranha 1"), S(16,"W7-4","World 7-4"), S(19,"W7-2","World 7-2"), S(22,"W7-3","World 7-3"), S(24,"W7-6","World 7-6"), S(25,"W7-7","World 7-7"), S(33,"W7-8","World 7-8"), S(35,"W7-5","World 7-5"), S(38,"W7-9","World 7-9"), S(40,"W7-F2","World 7 Fortress 2"), S(45,"W7-I2","World 7 Piranha 2")]),
        new(8,
            [62,81,94,81,113,122,113,122,62,94,90,94,81,113,123,123,123,126,94,81,94,81,81,81,113,113,114,113,126,158,158,145,145,145,158,126,122,113,113,113,114],
            [S(5,"W8-T1","World 8 Tank 1"), S(7,"W8-BS","World 8 Battleship"), S(10,"W8-A","World 8 Airship"), S(14,"W8-H3","World 8 Hand 3"), S(15,"W8-H2","World 8 Hand 2"), S(16,"W8-H1","World 8 Hand 1"), S(25,"W8-1","World 8-1"), S(26,"W8-F","World 8 Fortress"), S(29,"W8-2","World 8-2"), S(36,"W8-T2","World 8 Tank 2"), S(40,"W8-C","World 8 Castle")])
    ];

    public static OperationResult<IReadOnlyDictionary<string, LevelLocation>> Build(byte[] romBytes)
    {
        var diagnostics = new List<Diagnostic>();
        var levels = new Dictionary<string, LevelLocation>(StringComparer.Ordinal);
        foreach (var world in Worlds)
        {
            var rowOffset = FindUnique(romBytes, world.RowTypes);
            if (rowOffset < 0)
            {
                diagnostics.Add(Diagnostics.Error("CATALOG_MAP", $"World {world.World} pointer tables could not be located uniquely."));
                continue;
            }

            var count = world.RowTypes.Length;
            var enemyTable = rowOffset + (count * 2);
            var layoutTable = enemyTable + (count * 2);
            foreach (var stage in world.Stages)
            {
                var tileset = world.RowTypes[stage.Index] & 0x0F;
                if (tileset >= LayoutBankByTileset.Length || !FourByteGenerators.TryGetValue(tileset, out var extraIds))
                {
                    diagnostics.Add(Diagnostics.Warning("CATALOG_TILESET", $"{stage.DisplayName} uses unsupported tileset {tileset}."));
                    continue;
                }

                var enemyPointer = ReadWord(romBytes, enemyTable + (stage.Index * 2));
                var layoutPointer = ReadWord(romBytes, layoutTable + (stage.Index * 2));
                if (enemyPointer is < 0xC000 or > 0xDFFF || layoutPointer is < 0xA000 or > 0xBFFF)
                {
                    diagnostics.Add(Diagnostics.Warning("CATALOG_POINTER", $"{stage.DisplayName} has a non-level pointer and was skipped."));
                    continue;
                }

                var layoutBank = LayoutBankByTileset[tileset];
                var layoutOffset = 16 + (layoutBank * 0x2000) + (layoutPointer - 0xA000);
                var enemyOffset = 16 + (6 * 0x2000) + (enemyPointer - 0xC000);
                var layoutCapacity = Math.Min(0x2000 - (layoutPointer - 0xA000), romBytes.Length - layoutOffset);
                var enemyCapacity = Math.Min(0x2000 - (enemyPointer - 0xC000), romBytes.Length - enemyOffset);
                if (layoutCapacity <= Smb3LevelCodec.HeaderLength || enemyCapacity <= 1)
                {
                    diagnostics.Add(Diagnostics.Warning("CATALOG_RANGE", $"{stage.DisplayName} points outside its legal data bank."));
                    continue;
                }

                var signed = stage.Id == "W1-1";
                levels[stage.Id] = new LevelLocation(
                    stage.Id,
                    stage.DisplayName,
                    tileset,
                    layoutOffset,
                    layoutCapacity,
                    enemyOffset,
                    enemyCapacity,
                    extraIds,
                    signed ? new byte[] { 0x00, 0x00, 0x03, 0x1A, 0x00, 0xC0, 0x26, 0x11 } : Array.Empty<byte>(),
                    signed ? new byte[] { 0x01, 0x72, 0x0E, 0x19, 0xA6, 0x16, 0x17 } : Array.Empty<byte>());
            }
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return OperationResult<IReadOnlyDictionary<string, LevelLocation>>.Failure(diagnostics.ToArray());
        }

        var aliasGroups = levels
            .GroupBy(static pair => (pair.Value.LayoutOffset, pair.Value.EnemyOffset))
            .Select(static group => group.ToArray())
            .ToArray();
        foreach (var group in aliasGroups)
        {
            var canonicalId = group.Select(static pair => pair.Key).OrderBy(static id => id, StringComparer.Ordinal).First();
            foreach (var pair in group)
            {
                levels[pair.Key] = pair.Value with { AreaId = canonicalId };
            }
        }

        diagnostics.Add(Diagnostics.Info("CATALOG_READY", $"Discovered {levels.Count} stock stages from authenticated pointer tables."));
        return OperationResult<IReadOnlyDictionary<string, LevelLocation>>.Success(levels, diagnostics);
    }

    private static IReadOnlySet<int> Set(params int[] values) => new HashSet<int>(values);
    private static StageSpec S(int index, string id, string displayName) => new(index, id, displayName);

    private static ushort ReadWord(byte[] bytes, int offset) => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static int FindUnique(byte[] bytes, byte[] pattern)
    {
        var found = -1;
        for (var offset = 0; offset <= bytes.Length - pattern.Length; offset++)
        {
            if (!bytes.AsSpan(offset, pattern.Length).SequenceEqual(pattern))
            {
                continue;
            }

            if (found >= 0)
            {
                return -1;
            }

            found = offset;
        }

        return found;
    }

    private sealed record WorldSpec(int World, byte[] RowTypes, StageSpec[] Stages);
    private sealed record StageSpec(int Index, string Id, string DisplayName);
}
