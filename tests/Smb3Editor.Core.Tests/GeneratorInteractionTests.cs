namespace Smb3Editor.Core.Tests;

public sealed class GeneratorInteractionTests
{
    [Fact]
    public void ResizeChangesOnlyRequestedPackedFields()
    {
        var element = new LevelElement(4, LevelElementKind.VariableGenerator, 3, 22, 18,
            0x3A, 0x77, 0x72, 0x16, 22, 18);
        var document = new LevelDocument("test", "test", 1,
            new LevelHeader(0, 0, 0, 0, 1, 0, 0), [element], 0, [], 32, 2, []);

        var resized = document.ResizeElement(4, top: 12, parameter: 5).Elements.Single();

        Assert.Equal(22, resized.X);
        Assert.Equal(12, resized.Y);
        Assert.Equal(0x35, resized.Shape);
        Assert.Equal<byte?>((byte)0x77, resized.ExtraParameter);
        Assert.Equal(element.GeneratorId, resized.GeneratorId);
        Assert.Equal(element.OriginalFirstByte, resized.OriginalFirstByte);
        Assert.Equal(element.OriginalSecondByte, resized.OriginalSecondByte);
    }

    [Fact]
    public void EditableLayerMovementCrossesJunctionWithoutMovingJunctionRecord()
    {
        var first = Element(1, LevelElementKind.FixedGenerator);
        var junction = Element(2, LevelElementKind.Junction);
        var second = Element(3, LevelElementKind.FixedGenerator);
        var document = new LevelDocument("test", "test", 1,
            new LevelHeader(0, 0, 0, 0, 1, 0, 0), [first, junction, second], 0, [], 32, 2, []);

        var moved = document.MoveElementInEditableOrder(first.Index, 1);

        Assert.True(moved.Moved);
        Assert.Equal(2, moved.Layer);
        Assert.Equal(2, moved.TotalLayers);
        Assert.Equal([second.Index, junction.Index, first.Index], moved.Document.Elements.Select(item => item.Index));
        var back = moved.Document.MoveElementInEditableOrder(first.Index, -1);
        Assert.Equal([first.Index, junction.Index, second.Index], back.Document.Elements.Select(item => item.Index));
    }

    private static LevelElement Element(int index, LevelElementKind kind) =>
        new(index, kind, 0, 0, 0, 0, null, 0, 0, 0, 0);
}
