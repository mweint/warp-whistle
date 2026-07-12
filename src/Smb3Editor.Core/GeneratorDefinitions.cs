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
            return new($"Platform ${element.GeneratorId:X2} to ground", GeneratorConstraint.FloorAnchored,
                CanMoveX: true, CanMoveY: false, CanResizeTop: true, CanResizeRight: true, ParameterName: "Width");
        }

        if (document.Tileset == 1 && element.GeneratorId is >= 4 and <= 7)
        {
            return new($"Sky platform ${element.GeneratorId:X2}", GeneratorConstraint.Free,
                CanMoveX: true, CanMoveY: true, CanResizeRight: true, ParameterName: "Width");
        }

        if (document.Tileset == 1 && element.GeneratorId is >= 23 and <= 25)
        {
            return new($"Vertical pipe ${element.GeneratorId:X2}", GeneratorConstraint.FloorAnchored,
                CanMoveX: true, CanMoveY: true, CanResizeTop: true, ParameterName: "Height",
                TopResizeChangesPosition: false, TopResizePreservesBottom: true);
        }

        if (document.Tileset == 1 && element.GeneratorId is >= 26 and <= 27)
        {
            return new($"Ceiling pipe ${element.GeneratorId:X2}", GeneratorConstraint.CeilingAnchored,
                CanMoveX: true, CanMoveY: true, ParameterName: "Height", CanResizeBottom: true);
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
