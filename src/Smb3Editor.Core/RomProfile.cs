namespace Smb3Editor.Core;

public sealed record LevelLocation(
    string AreaId,
    string DisplayName,
    int Tileset,
    int LayoutOffset,
    int LayoutCapacity,
    int EnemyOffset,
    int EnemyCapacity,
    IReadOnlySet<int> FourByteGeneratorIds,
    IReadOnlyList<byte> LayoutDataSignature,
    IReadOnlyList<byte> EnemySignature)
{
    public override string ToString() => DisplayName;
}

public sealed record RomProfile(
    string Id,
    string DisplayName,
    string Sha1,
    int Mapper,
    int PrgBytes,
    int ChrBytes,
    IReadOnlyDictionary<string, LevelLocation> Levels);

public static class Smb3Profiles
{
    private static readonly IReadOnlySet<int> PlainsFourByteGenerators = new HashSet<int>
    {
        11, 12, 35, 36, 37, 38, 39, 40, 41, 42
    };
    private static readonly IReadOnlySet<int> FortressFourByteGenerators = new HashSet<int>
    {
        13, 14, 35, 36, 37, 38, 39, 40, 41, 42, 46, 47, 48, 57
    };
    private static readonly IReadOnlySet<int> HillsFourByteGenerators = new HashSet<int>
    {
        35, 36, 37, 38, 39, 40, 41, 42, 60, 61, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71
    };
    private static readonly IReadOnlySet<int> HighIceFourByteGenerators = new HashSet<int>
    {
        0, 35, 36, 37, 38, 39, 40, 41, 42, 54
    };

    private static readonly byte[] WorldOneOneLayoutSignature = [0x00, 0x00, 0x03, 0x1A, 0x00, 0xC0, 0x26, 0x11];
    private static readonly byte[] WorldOneOneEnemySignature = [0x01, 0x72, 0x0E, 0x19, 0xA6, 0x16, 0x17];

    private static LevelLocation WorldOneOne() => new(
        "W1-1",
        "World 1-1",
        1,
        0x1F97D,
        274,
        0x0C320,
        47,
        PlainsFourByteGenerators,
        WorldOneOneLayoutSignature,
        WorldOneOneEnemySignature);

    private static LevelLocation Area(
        string id,
        int tileset,
        int layoutOffset,
        int enemyOffset,
        IReadOnlySet<int> fourByteGenerators) => new(
            id,
            $"World {id[1]}-{id[3..]}",
            tileset,
            layoutOffset,
            512,
            enemyOffset,
            256,
            fourByteGenerators,
            Array.Empty<byte>(),
            Array.Empty<byte>());

    // Hashes are for complete, headered GoodNES/No-Intro-compatible images.
    // PRG1 offsets are verified against the Southbird PRG1 disassembly.
    private static readonly RomProfile Prg1 = new(
        "us-prg1",
        "Super Mario Bros. 3 (USA, PRG1 / Rev A)",
        "6bd518e85eb46a4252af07910f61036e84b020d1",
        4,
        262_144,
        131_072,
        new Dictionary<string, LevelLocation>(StringComparer.Ordinal)
        {
            ["W1-1"] = WorldOneOne(),
            ["W1-2"] = Area("W1-2", 3, 0x20E09, 0x0C482, HillsFourByteGenerators),
            ["W1-3"] = Area("W1-3", 1, 0x1EC18, 0x0C15F, PlainsFourByteGenerators),
            ["W1-4"] = Area("W1-4", 4, 0x232C2, 0x0C861, HighIceFourByteGenerators),
            ["W1-F"] = Area("W1-F", 2, 0x2B15B, 0x0CE6D, FortressFourByteGenerators),
            ["W1-5"] = Area("W1-5", 14, 0x1AA51, 0x0C5DC, HillsFourByteGenerators),
            ["W1-6"] = Area("W1-6", 4, 0x23169, 0x0C83B, HighIceFourByteGenerators)
        });

    private static readonly RomProfile Prg0 = new(
        "us-prg0",
        "Super Mario Bros. 3 (USA, PRG0)",
        "a03e7e526e79df222e048ae22214bca2bc49c449",
        4,
        262_144,
        131_072,
        new Dictionary<string, LevelLocation>(StringComparer.Ordinal)
        {
            // PRG revisions retain this slot; Decode additionally requires independent stream signatures.
            ["W1-1"] = WorldOneOne()
        });

    public static IReadOnlyList<RomProfile> All { get; } = [Prg0, Prg1];

    public static RomProfile? FindBySha1(string sha1) =>
        All.FirstOrDefault(p => string.Equals(p.Sha1, sha1, StringComparison.OrdinalIgnoreCase));

    public static RomProfile? FindById(string id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}
