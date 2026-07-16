namespace Smb3Editor.Core;

public enum GeneratorConstraint
{
    Free,
    FloorAnchored,
    CeilingAnchored,
    HorizontalRun,
    Background,
    Unknown
}

public sealed record GeneratorDefinition(
    string Name,
    GeneratorConstraint Constraint,
    bool CanMoveX,
    bool CanMoveY,
    bool CanResizeTop = false,
    bool CanResizeRight = false,
    string? ParameterName = null,
    bool CanResizeBottom = false,
    bool TopResizeChangesPosition = true,
    bool TopResizePreservesBottom = false,
    bool CanResizeLeft = false,
    bool HorizontalSizeUsesExtraParameter = false,
    bool VerticalSizeUsesExtraParameter = false)
{
    public static GeneratorDefinition For(LevelDocument document, LevelElement element)
    {
        if (element.Kind == LevelElementKind.FixedGenerator)
        {
            return new($"Object ${element.GeneratorId:X2}", GeneratorConstraint.Free, true, true);
        }

        if (element.Kind == LevelElementKind.Junction)
        {
            return new("Junction", GeneratorConstraint.Unknown, false, false);
        }

        if (document.Tileset == 1 && element.GeneratorId is >= 0 and <= 3)
        {
            var names = new[] { "Big White Block to Ground", "Big Orange Block to Ground", "Big Green Block to Ground", "Big Blue Block to Ground" };
            return new(names[element.GeneratorId], GeneratorConstraint.FloorAnchored,
                CanMoveX: true, CanMoveY: false, CanResizeTop: true, CanResizeRight: true, ParameterName: "Width");
        }

        if (document.Tileset == 1 && element.GeneratorId is >= 4 and <= 7)
        {
            return new($"Sky platform ${element.GeneratorId:X2}", GeneratorConstraint.Free,
                CanMoveX: true, CanMoveY: true, CanResizeRight: true, ParameterName: "Width");
        }

        // Ground runs are ordinary positioned rectangles. Their low shape
        // nibble is height, while their required fourth byte is width.
        if (document.Tileset == 1 && element.GeneratorId is 11 or 12)
        {
            return new(element.GeneratorId == 11 ? "Dry Ground" : "Underwater Ground", GeneratorConstraint.Free,
                CanMoveX: true, CanMoveY: true, CanResizeTop: true, CanResizeRight: true, ParameterName: "Height × Width",
                CanResizeBottom: true, CanResizeLeft: true, HorizontalSizeUsesExtraParameter: true);
        }

        // These documented four-byte Plains generators use the shape nibble
        // for height and the fourth byte for width. Do not infer controls by
        // probing an outward resize: at the level bottom that probe fails,
        // while upward resizing remains valid.
        // These long-run generators keep the same four-byte shape/width
        // contract across the stock gameplay tilesets.  Their art changes
        // with the tileset, but the editable command is shared.
        if (element.GeneratorId is >= 35 and <= 42)
        {
            var names = new[]
            {
                "Waterfall", "Water Pool - Left Edge", "Water Pool - Still", "Water Pool - Right Edge",
                "Bowser Background", "Diamond Block Run", "Sandy Ground", "Orange Block Run"
            };
            return new(names[element.GeneratorId - 35], GeneratorConstraint.Free,
                CanMoveX: true, CanMoveY: true, CanResizeTop: true, CanResizeRight: true, ParameterName: "Height x Width",
                CanResizeBottom: true, TopResizePreservesBottom: true, CanResizeLeft: true,
                HorizontalSizeUsesExtraParameter: true);
        }

        if (element.GeneratorId == 43)
        {
            return new("White Turtle Bricks", GeneratorConstraint.HorizontalRun,
                CanMoveX: true, CanMoveY: true, CanResizeRight: true, ParameterName: "Length");
        }

        // Pipe generators use the same IDs and edge constraints across the
        // gameplay tilesets; they are not Plains-only objects.
        if (element.GeneratorId is >= 23 and <= 25)
        {
            return new($"Vertical pipe ${element.GeneratorId:X2}", GeneratorConstraint.FloorAnchored,
                CanMoveX: true, CanMoveY: true, CanResizeTop: true, ParameterName: "Height",
                TopResizeChangesPosition: false, TopResizePreservesBottom: true);
        }

        if (element.GeneratorId is >= 26 and <= 27)
        {
            return new($"Ceiling pipe ${element.GeneratorId:X2}", GeneratorConstraint.CeilingAnchored,
                CanMoveX: true, CanMoveY: true, ParameterName: "Height", CanResizeBottom: true);
        }

        if (element.GeneratorId is >= 28 and <= 31)
        {
            var name = element.GeneratorId switch
            {
                28 => "Right-wall pipe to alternate area",
                29 => "Right-wall pipe (no entrance)",
                30 => "Left-wall pipe to alternate area",
                _ => "Left-wall pipe (no entrance)"
            };
            // The disassembly shows a 1-16 horizontal run in the low shape
            // nibble. Right-wall pipes grow toward the left; left-wall pipes
            // grow toward the right.
            return element.GeneratorId is 28 or 29
                ? new(name, GeneratorConstraint.HorizontalRun, CanMoveX: true, CanMoveY: true,
                    CanResizeLeft: true, ParameterName: "Length")
                : new(name, GeneratorConstraint.HorizontalRun, CanMoveX: true, CanMoveY: true,
                    CanResizeRight: true, ParameterName: "Length");
        }

        if (document.Tileset == 2 && element.GeneratorId == 13)
        {
            return new("Gray Diamond Block Rectangle", GeneratorConstraint.Free, true, true,
                CanResizeTop: true, CanResizeRight: true, ParameterName: "Height × Width",
                CanResizeBottom: true, CanResizeLeft: true, HorizontalSizeUsesExtraParameter: true);
        }

        if (document.Tileset == 2 && element.GeneratorId == 14)
        {
            return new("Gray Diamond Block Rectangle (Tall)", GeneratorConstraint.Free, true, true,
                CanResizeTop: true, CanResizeRight: true, ParameterName: "Width × Height",
                CanResizeBottom: true, CanResizeLeft: true, VerticalSizeUsesExtraParameter: true);
        }

        if (element.Kind == LevelElementKind.VariableGenerator)
        {
            return new($"Variable object ${element.GeneratorId:X2}", GeneratorConstraint.Unknown,
                CanMoveX: true, CanMoveY: true, CanResizeRight: true, ParameterName: "Size");
        }

        return new($"Object ${element.GeneratorId:X2}", GeneratorConstraint.Unknown, true, true);
    }
}

/// <summary>Smallest non-wrapping forms for new vanilla variable generators.</summary>
public static class GeneratorDefaults
{
    /// <summary>Smallest parameter proven not to underflow in the stock generator.</summary>
    public static byte Parameter(int tileset, int generatorId) => tileset switch
    {
        // Floating large blocks loop until their width counter reaches one.
        // Values below three create the wrapped, giant form.
        1 when generatorId is >= 4 and <= 7 => 3,
        // These generators decrement with BNE, so zero would wrap to 256.
        _ when generatorId is 9 or 13 or 14 or 26 or 27 or 30 or 31 or 42 or 44 => 1,
        _ => 0
    };

    public static byte? ExtraParameter(int tileset, IReadOnlySet<int> fourByteGeneratorIds, int generatorId)
    {
        if (!fourByteGeneratorIds.Contains(generatorId)) return null;

        // Ground's fourth byte is a special run length: zero wraps to 256,
        // while one makes its smallest useful two-column form. Other known
        // long-run generators use zero as their one-column form.
        return tileset == 1 && generatorId is 11 or 12 ? (byte)1 : (byte)0;
    }

    /// <summary>Clamps an editable shape value away from stock-engine underflow forms.</summary>
    public static int ClampParameter(int tileset, int generatorId, int value) =>
        Math.Clamp(value, Parameter(tileset, generatorId), 15);

    /// <summary>Clamps known four-byte run lengths away from their 256-tile wrapped form.</summary>
    public static int ClampExtraParameter(int tileset, int generatorId, int value) =>
        Math.Clamp(value, tileset == 1 && generatorId is 11 or 12 ? 1 : 0, 255);
}
