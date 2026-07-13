namespace Smb3Editor.Core;

public sealed record LevelHeader(
    ushort AlternateLayoutAddress,
    ushort AlternateEnemyAddress,
    byte SizeAndStartY,
    byte PalettesAndStartX,
    byte TilesetAndScroll,
    byte BackgroundAndAction,
    byte MusicAndTime)
{
    public int ScreenCount => (SizeAndStartY & 0x0F) + 1;
    public int PlayerStartY => (SizeAndStartY >> 5) & 0x07;
    public int BackgroundPalette => PalettesAndStartX & 0x07;
    public int ObjectPalette => (PalettesAndStartX >> 3) & 0x03;
    public int PlayerStartX => (PalettesAndStartX >> 5) & 0x03;
    public bool IsVertical => (TilesetAndScroll & 0x10) != 0;
    public int Music => MusicAndTime & 0x0F;
    public int TimeSetting => (MusicAndTime >> 6) & 0x03;

    public LevelHeader WithEditableSettings(int screens, int backgroundPalette, int objectPalette, int music, int time) =>
        this with
        {
            SizeAndStartY = (byte)((SizeAndStartY & 0xF0) | ((screens - 1) & 0x0F)),
            PalettesAndStartX = (byte)((PalettesAndStartX & 0xE0) | (backgroundPalette & 0x07) | ((objectPalette & 0x03) << 3)),
            MusicAndTime = (byte)((MusicAndTime & 0x30) | ((time & 0x03) << 6) | (music & 0x0F))
        };

    public LevelHeader WithPlayerStart(int x, int y) => this with
    {
        SizeAndStartY = (byte)((SizeAndStartY & 0x1F) | ((Math.Clamp(y, 0, 7) & 0x07) << 5)),
        PalettesAndStartX = (byte)((PalettesAndStartX & 0x9F) | ((Math.Clamp(x, 0, 3) & 0x03) << 5))
    };
}

public enum LevelElementKind
{
    FixedGenerator,
    VariableGenerator,
    Junction
}

public sealed record LevelElement(
    int Index,
    LevelElementKind Kind,
    int GeneratorId,
    int X,
    int Y,
    byte Shape,
    byte? ExtraParameter,
    byte OriginalFirstByte,
    byte OriginalSecondByte,
    int OriginalX,
    int OriginalY)
{
    public int Parameter => Shape & 0x0F;
    public int JunctionIndex => Kind == LevelElementKind.Junction ? OriginalFirstByte & 0x0F : -1;
    public byte JunctionYAndEntry => OriginalSecondByte;
    public byte JunctionX => Shape;
}

public sealed record EnemyElement(
    int Index,
    byte Id,
    int X,
    int Y,
    byte Flags = 0,
    byte? OriginalSecondByte = null,
    byte? OriginalThirdByte = null,
    int? OriginalX = null,
    int? OriginalY = null);

public sealed record LevelDocument(
    string AreaId,
    string DisplayName,
    int Tileset,
    LevelHeader Header,
    IReadOnlyList<LevelElement> Elements,
    byte EnemyHeader,
    IReadOnlyList<EnemyElement> Enemies,
    int OriginalLayoutLength,
    int OriginalEnemyLength,
    IReadOnlyList<string> UsedBy)
{
    public ElementOrderMoveResult MoveElementInEditableOrder(int index, int delta)
    {
        var list = Elements.ToList();
        var editable = list
            .Select((item, position) => (item, position))
            .Where(pair => pair.item.Kind != LevelElementKind.Junction)
            .ToArray();
        var currentLayer = Array.FindIndex(editable, pair => pair.item.Index == index);
        if (currentLayer < 0)
        {
            return new(this, 0, editable.Length, false);
        }
        var targetLayer = Math.Clamp(currentLayer + delta, 0, editable.Length - 1);
        if (targetLayer == currentLayer)
        {
            return new(this, currentLayer + 1, editable.Length, false);
        }
        var currentPosition = editable[currentLayer].position;
        var targetPosition = editable[targetLayer].position;
        (list[currentPosition], list[targetPosition]) = (list[targetPosition], list[currentPosition]);
        return new(this with { Elements = list }, targetLayer + 1, editable.Length, true);
    }

    public LevelDocument MoveElement(int index, int x, int y)
    {
        var updated = Elements.Select(element =>
            element.Index == index ? element with { X = x, Y = y } : element).ToArray();
        return this with { Elements = updated };
    }

    public LevelDocument MoveEnemy(int index, int x, int y)
    {
        var updated = Enemies.Select(enemy =>
            enemy.Index == index ? enemy with { X = x, Y = y } : enemy).ToArray();
        return this with { Enemies = updated };
    }

    /// <summary>
    /// SMB3 indexes its enemy stream by horizontal screen (or vertical screen in vertical areas).
    /// Preserve the original relative order for enemies sharing a spawn coordinate.
    /// </summary>
    public IReadOnlyList<EnemyElement> OrderEnemiesForSpawn(IEnumerable<EnemyElement> enemies) =>
        Header.IsVertical
            ? enemies.OrderBy(static enemy => enemy.Y).ThenBy(static enemy => enemy.Index).ToArray()
            : enemies.OrderBy(static enemy => enemy.X).ThenBy(static enemy => enemy.Index).ToArray();

    public LevelDocument ResizeElement(int index, int? top = null, int? parameter = null, int? extraParameter = null, int? left = null)
    {
        var updated = Elements.Select(element =>
        {
            if (element.Index != index || element.Kind == LevelElementKind.Junction)
            {
                return element;
            }

            var y = top is int requestedTop ? Math.Clamp(requestedTop, 0, Header.IsVertical ? Header.ScreenCount * 15 - 1 : 26) : element.Y;
            var x = left is int requestedLeft ? Math.Clamp(requestedLeft, 0, Header.IsVertical ? 15 : 255) : element.X;
            var shape = parameter is int requestedParameter
                ? (byte)((element.Shape & 0xF0) | Math.Clamp(requestedParameter, 0, 15))
                : element.Shape;
            var extra = extraParameter is int requestedExtra
                ? (byte)Math.Clamp(requestedExtra, 0, 255)
                : element.ExtraParameter;
            return element with { X = x, Y = y, Shape = shape, ExtraParameter = extra };
        }).ToArray();
        return this with { Elements = updated };
    }
}

public sealed record ElementOrderMoveResult(LevelDocument Document, int Layer, int TotalLayers, bool Moved);
