using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Smb3Editor.Core;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Smb3Editor.App;

public sealed class LevelEditCommittedEventArgs(LevelDocument previous, LevelDocument current) : EventArgs
{
    public LevelDocument Previous { get; } = previous;
    public LevelDocument Current { get; } = current;
}

public sealed class CanvasZoomRequestedEventArgs(double oldZoom, double newZoom, Point pointer, Point logicalPoint) : EventArgs
{
    public double OldZoom { get; } = oldZoom;
    public double NewZoom { get; } = newZoom;
    public Point Pointer { get; } = pointer;
    public Point LogicalPoint { get; } = logicalPoint;
}

public sealed class CanvasPanRequestedEventArgs(Vector delta) : EventArgs
{
    public Vector Delta { get; } = delta;
}

public sealed class CanvasEdgeScrollRequestedEventArgs(Point canvasPoint) : EventArgs
{
    public Point CanvasPoint { get; } = canvasPoint;
}

public sealed record EditorActionFeedback(
    DiagnosticSeverity Severity,
    string Summary,
    string Details,
    int? AffectedElement,
    bool Persistent,
    bool BlocksExport);

public sealed class LevelCanvas : Control
{
    private const double TilePixels = 16;
    // Keep the bottom-edge resize handle inside the scrollable control. Without
    // this gutter, a handle on the last visible row is clipped by the viewport.
    private const double BottomHandleGutter = 12;
    private static readonly double[] PlayerStartXCoordinates = [1.5, 7, 13.5, 8];
    private static readonly double[] PlayerStartYCoordinates = [23, 4, 0, 20, 7, 11, 15, 24];
    private readonly ISmb3LevelRenderer _renderer = new Smb3LevelRenderer();
    private LevelDocument? _document;
    private RomImage? _rom;
    private IReadOnlyList<PaletteOverride> _paletteOverrides = [];
    private LevelRenderSnapshot? _renderSnapshot;
    private WriteableBitmap? _levelBitmap;
    private readonly Dictionary<byte, WriteableBitmap> _enemyBitmaps = [];
    private byte[]? _smallMarioPixels;
    private LevelDocument? _dragStartDocument;
    private int? _selectedElement;
    private int? _selectedEnemy;
    private readonly HashSet<int> _selectedElements = [];
    private readonly HashSet<int> _selectedEnemies = [];
    private bool _dragging;
    private bool _draggingPlayerStart;
    private bool _playerStartSelected;
    private bool _panning;
    private Point _panPointer;
    private Point _dragPointerStart;
    private bool _marqueeSelecting;
    private Point _marqueeStart;
    private Point _marqueeCurrent;
    private KeyModifiers _marqueeModifiers;
    private double _dragStartScaledTile;
    private int _dragItemStartX;
    private int _dragItemStartY;
    private double _zoom = 1;
    private IReadOnlyList<LevelElement> _copiedElements = [];
    private IReadOnlyList<EnemyElement> _copiedEnemies = [];
    private string? _hoverTip;
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _wheelOrderTimer;
    private readonly DispatcherTimer _layerHintTimer;
    private LevelDocument? _wheelOrderStartDocument;
    private DragOperation _dragOperation;
    private int _dragStartParameter;
    private int _dragStartExtraParameter;
    private int _dragResizeTilesPerStep = 1;
    private int _dragStartRenderedBottom;
    private LevelDocument? _definitionDocument;
    private readonly Dictionary<int, GeneratorDefinition> _definitionCache = [];
    private IReadOnlyList<Diagnostic> _activeRenderDiagnostics = [];
    private readonly Dictionary<int, string> _elementSafetyDetails = [];
    private readonly Dictionary<int, (int X, int Y, int Parameter, int Extra)> _lastValidElementState = [];
    private readonly Dictionary<int, LevelElementRenderBounds> _lastValidElementBounds = [];
    private int? _unsafeElementIndex;
    private bool _suppressHoverUntilPointerMove;
    private string? _layerHint;
    private Point _layerHintPoint;

    public LevelCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        ToolTip.SetShowDelay(this, 600);
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (_hoverTip is not null) ToolTip.SetIsOpen(this, true);
        };
        _wheelOrderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _wheelOrderTimer.Tick += (_, _) => FlushWheelOrderBatch();
        _layerHintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(950) };
        _layerHintTimer.Tick += (_, _) =>
        {
            _layerHintTimer.Stop();
            _layerHint = null;
            InvalidateVisual();
        };
        UpdateExtent();
    }

    public event EventHandler<LevelEditCommittedEventArgs>? EditCommitted;
    public event EventHandler<CanvasZoomRequestedEventArgs>? ZoomRequested;
    public event EventHandler<CanvasPanRequestedEventArgs>? PanRequested;
    public event EventHandler<CanvasEdgeScrollRequestedEventArgs>? EdgeScrollRequested;
    public event Action<string>? SelectionDescriptionChanged;
    public event Action? CanvasItemSelected;
    public event Action<string>? LayerOrderFeedback;
    public event Action<EditorActionFeedback>? ActionFeedbackAvailable;
    public event Action? PersistentActionFeedbackCleared;
    public event Action<IReadOnlyList<Diagnostic>>? ActiveRenderDiagnosticsChanged;
    public event Action<int, int>? CatalogPlacementRequested;

    public bool HasBlockingRenderErrors => _activeRenderDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    public bool CanFixActiveIssue
    {
        get
        {
            if (_document is null || _unsafeElementIndex is not int index) return false;
            var platform = _document.Elements.FirstOrDefault(item => item.Index == index);
            if (platform is null || platform.GeneratorId is < 0 or > 3) return false;
            var platformPosition = _document.Elements.Select((item, position) => (item, position))
                .FirstOrDefault(pair => pair.item.Index == index).position;
            var groundPosition = FindSupportingGroundPosition(_document.Elements, platformPosition);
            return groundPosition >= 0 && platformPosition >= 0 && platformPosition < groundPosition;
        }
    }

    public bool TryFixActiveIssue()
    {
        if (!CanFixActiveIssue || _document is null || _unsafeElementIndex is not int index) return false;
        var previous = _document;
        var elements = _document.Elements.ToList();
        var platformPosition = elements.FindIndex(item => item.Index == index);
        var groundPosition = FindSupportingGroundPosition(elements, platformPosition);
        if (platformPosition < 0 || groundPosition < 0 || platformPosition >= groundPosition) return false;
        var platform = elements[platformPosition];
        var supportingGround = elements[groundPosition];
        elements.RemoveAt(platformPosition);
        groundPosition = elements.FindIndex(item => item.Index == supportingGround.Index);
        if (groundPosition < 0) return false;
        elements.Insert(groundPosition + 1, platform);
        _document = _document with { Elements = elements.ToArray() };
        Commit(previous);
        return true;
    }

    private static bool IsSupportingGround(LevelElement element) => element.GeneratorId is 11 or 12 or 41;

    private static int FindSupportingGroundPosition(IReadOnlyList<LevelElement> elements, int platformPosition)
    {
        for (var position = platformPosition + 1; position < elements.Count; position++)
        {
            if (IsSupportingGround(elements[position])) return position;
        }
        return -1;
    }

    public RomImage? SourceRom
    {
        get => _rom;
        set
        {
            _rom = value;
            LoadSmallMarioSprite();
            UpdateRenderedLevel();
        }
    }

    public IReadOnlyList<PaletteOverride> PaletteOverrides
    {
        get => _paletteOverrides;
        set
        {
            _paletteOverrides = value ?? [];
            UpdateRenderedLevel();
            InvalidateVisual();
        }
    }

    public IReadOnlySet<int> FourByteGeneratorIds { get; set; } = new HashSet<int>();

    public LevelDocument? Document
    {
        get => _document;
        set
        {
            FlushWheelOrderBatch();
            _document = value;
            ClearSelectionState();
            UpdateRenderedLevel();
            UpdateExtent();
            InvalidateVisual();
        }
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, 0.25, 8.0);
            UpdateExtent();
            InvalidateVisual();
        }
    }

    public bool ShowGrid { get; set; } = true;

    public void ClearSelection()
    {
        ClearSelectionState();
        NotifySelectionChanged();
        InvalidateVisual();
    }

    /// <summary>Selects every movable or removable item in the current area.</summary>
    public void SelectAllItems()
    {
        if (_document is null) return;

        ClearSelectionState();
        foreach (var element in _document.Elements.Where(static item => item.Kind != LevelElementKind.Junction))
            _selectedElements.Add(element.Index);
        foreach (var enemy in _document.Enemies)
            _selectedEnemies.Add(enemy.Index);

        _selectedElement = _selectedElements.Count > 0 ? _selectedElements.First() : null;
        _selectedEnemy = _selectedElements.Count == 0 && _selectedEnemies.Count > 0
            ? _selectedEnemies.First()
            : null;
        NotifySelectionChanged();
        InvalidateVisual();
    }

    private bool IsElementSelected(int index) => _selectedElements.Contains(index);

    private bool IsEnemySelected(int index) => _selectedEnemies.Contains(index);

    private bool HasSelection => _selectedElements.Count > 0 || _selectedEnemies.Count > 0;

    private bool HasMultipleSelection => _selectedElements.Count + _selectedEnemies.Count > 1;

    private void ClearSelectionState()
    {
        _selectedElement = null;
        _selectedEnemy = null;
        _selectedElements.Clear();
        _selectedEnemies.Clear();
        _playerStartSelected = false;
    }

    private void SelectOnly(int? elementIndex, int? enemyIndex)
    {
        ClearSelectionState();
        _selectedElement = elementIndex;
        _selectedEnemy = enemyIndex;
        if (elementIndex is int element) _selectedElements.Add(element);
        if (enemyIndex is int enemy) _selectedEnemies.Add(enemy);
    }

    private void AddSelection(int? elementIndex, int? enemyIndex)
    {
        _playerStartSelected = false;
        if (elementIndex is int element)
        {
            _selectedElements.Add(element);
            _selectedElement = element;
            _selectedEnemy = null;
        }
        else if (enemyIndex is int enemy)
        {
            _selectedEnemies.Add(enemy);
            _selectedEnemy = enemy;
            _selectedElement = null;
        }
    }

    private void ToggleSelection(int? elementIndex, int? enemyIndex)
    {
        _playerStartSelected = false;
        if (elementIndex is int element)
        {
            if (!_selectedElements.Remove(element))
            {
                _selectedElements.Add(element);
                _selectedElement = element;
                _selectedEnemy = null;
            }
        }
        else if (enemyIndex is int enemy)
        {
            if (!_selectedEnemies.Remove(enemy))
            {
                _selectedEnemies.Add(enemy);
                _selectedEnemy = enemy;
                _selectedElement = null;
            }
        }

        if ((_selectedElement is int selectedElement && !_selectedElements.Contains(selectedElement)) ||
            (_selectedEnemy is int selectedEnemy && !_selectedEnemies.Contains(selectedEnemy)))
        {
            _selectedElement = _selectedElements.Count > 0 ? _selectedElements.First() : null;
            _selectedEnemy = _selectedElements.Count > 0
                ? null
                : _selectedEnemies.Count > 0 ? _selectedEnemies.First() : null;
        }
    }

    public void CopySelectionTo(int x, int y)
    {
        CopySelection();
        PasteCopiedSelectionAt(x, y);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#101722")), bounds);

        if (_document is null)
        {
            DrawGrid(context, 16, 27);
            return;
        }

        var columns = _document.Header.IsVertical ? 16 : Math.Max(16, _document.Header.ScreenCount * 16);
        var rows = _document.Header.IsVertical ? _document.Header.ScreenCount * 15 : 27;
        if (_levelBitmap is not null && _renderSnapshot is not null)
        {
            context.DrawImage(
                _levelBitmap,
                new Rect(0, 0, _renderSnapshot.PixelWidth, _renderSnapshot.PixelHeight),
                new Rect(0, 0, _renderSnapshot.PixelWidth * _zoom, _renderSnapshot.PixelHeight * _zoom));
        }

        DrawGrid(context, columns, rows);
        DrawLayerHint(context);
        DrawPlayerStart(context);

        foreach (var element in _document.Elements)
        {
            var renderBounds = GetElementBounds(element);
            var rect = new Rect(
                renderBounds.Left * ScaledTile,
                renderBounds.Top * ScaledTile,
                Math.Max(1, renderBounds.Width) * ScaledTile,
                Math.Max(1, renderBounds.Height) * ScaledTile);
            var selected = IsElementSelected(element.Index);
            var unsafeObject = _unsafeElementIndex == element.Index;
            var bouncingBlock = element.Kind == LevelElementKind.VariableGenerator && element.GeneratorId == 21;
            var marker = element.Kind == LevelElementKind.Junction
                ? Color.Parse("#D58CFF")
                : bouncingBlock ? Color.Parse("#FFB547") : Color.Parse("#42D6C5");
            if (selected || element.Kind == LevelElementKind.Junction)
            {
                context.DrawRectangle(
                    null,
                    new Pen(selected ? Brushes.White : new SolidColorBrush(marker, 0.8), selected ? 3 : 2),
                    rect);
            }

            if (unsafeObject)
            {
                context.DrawRectangle(null, new Pen(Brushes.Gold, 2), rect);
            }

            if (selected && element.Kind != LevelElementKind.Junction)
            {
                var definition = GetEffectiveDefinition(element);
                if (definition.CanResizeTop)
                {
                    context.FillRectangle(Brushes.White, ResizeTopHandle(rect));
                }
                if (definition.CanResizeRight)
                {
                    context.FillRectangle(Brushes.White, ResizeRightHandle(rect));
                }
                if (definition.CanResizeBottom)
                {
                    context.FillRectangle(Brushes.White, ResizeBottomHandle(rect));
                }
                if (definition.CanResizeLeft)
                {
                    context.FillRectangle(Brushes.White, ResizeLeftHandle(rect));
                }
                if (definition.CanResizeLeft && definition.CanResizeRight && definition.CanResizeTop && definition.CanResizeBottom)
                {
                    foreach (var corner in ResizeCornerHandles(rect)) context.FillRectangle(Brushes.White, corner);
                }
            }

            var renderAnchor = GetElementAnchor(element);
            var anchor = new Point(
                (renderAnchor.X + 0.5) * ScaledTile,
                (renderAnchor.Y + 0.5) * ScaledTile);
            context.DrawEllipse(new SolidColorBrush(marker, selected ? 1 : 0.75),
                bouncingBlock ? new Pen(Brushes.White, 1) : null, anchor, bouncingBlock ? 5 : 3, bouncingBlock ? 5 : 3);
        }

        foreach (var enemy in _document.Enemies)
        {
            if (_renderSnapshot?.EnemySprites.TryGetValue(enemy.Id, out var preview) == true &&
                _enemyBitmaps.TryGetValue(enemy.Id, out var spriteBitmap))
            {
                var destination = new Rect(
                    ((enemy.X * 16) + preview.OffsetX) * _zoom,
                    ((enemy.Y * 16) + preview.OffsetY) * _zoom,
                    preview.PixelWidth * _zoom,
                    preview.PixelHeight * _zoom);
                context.DrawImage(
                    spriteBitmap,
                    new Rect(0, 0, preview.PixelWidth, preview.PixelHeight),
                    destination);
                if (IsEnemySelected(enemy.Index))
                {
                    // Selection follows the actual opaque sprite footprint.
                    // SMB3 stores some commands at a pipe/base while their
                    // visible art is offset above or beside it.
                    context.DrawRectangle(null, new Pen(Brushes.White, 2), destination.Inflate(1));
                }

                continue;
            }

            var center = new Point((enemy.X + 0.5) * ScaledTile, (enemy.Y + 0.5) * ScaledTile);
            var selected = IsEnemySelected(enemy.Index);
            var size = ScaledTile * 0.72;
            var marker = new Rect(center.X - size / 2, center.Y - size / 2, size, size);
            context.FillRectangle(new SolidColorBrush(Color.Parse("#7356D8"), selected ? 1 : 0.88), marker);
            context.DrawRectangle(null, new Pen(selected ? Brushes.White : Brushes.MediumPurple, selected ? 3 : 2), marker);
            context.DrawLine(new Pen(Brushes.White, 1), marker.TopLeft, marker.BottomRight);
            context.DrawLine(new Pen(Brushes.White, 1), marker.TopRight, marker.BottomLeft);
        }

        if (_marqueeSelecting)
        {
            var marquee = MarqueeRect();
            context.FillRectangle(new SolidColorBrush(Color.Parse("#5EA9E8"), 0.14), marquee);
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#8FCBFF")), 1), marquee);
        }
    }

    public void AddFixedGenerator(int generatorId, int x = 1, int y = 1)
    {
        if (_document is null || generatorId is < 0 or > 0x7F)
        {
            return;
        }

        var previous = _document;
        var upper = (byte)((generatorId & 0x70) << 1);
        var shape = (byte)(generatorId & 0x0F);
        var next = _document.Elements.Count == 0 ? 0 : _document.Elements.Max(static item => item.Index) + 1;
        var maxX = _document.Header.IsVertical ? 15 : 255;
        var maxY = _document.Header.IsVertical ? (_document.Header.ScreenCount * 15) - 1 : 26;
        x = Math.Clamp(x, 0, maxX);
        y = Math.Clamp(y, 0, maxY);
        var first = _document.Header.IsVertical
            ? (byte)((upper & 0xF0) | (y % 15))
            : (byte)((upper & 0xE0) | (y & 0x1F));
        var second = _document.Header.IsVertical
            ? (byte)(((y / 15) << 4) | (x & 0x0F))
            : (byte)x;
        var element = new LevelElement(next, LevelElementKind.FixedGenerator, generatorId, x, y, shape, null, first, second, x, y);
        _document = _document with { Elements = _document.Elements.Append(element).ToArray() };
        SelectOnly(next, null);
        Commit(previous);
    }

    public void AddVariableGenerator(int generatorId, int x = 1, int y = 1)
    {
        if (_document is null || generatorId is < 0 or > 119) return;
        var previous = _document;
        var next = _document.Elements.Count == 0 ? 0 : _document.Elements.Max(static item => item.Index) + 1;
        var maxX = _document.Header.IsVertical ? 15 : 255;
        var maxY = _document.Header.IsVertical ? (_document.Header.ScreenCount * 15) - 1 : 26;
        x = Math.Clamp(x, 0, maxX);
        y = Math.Clamp(y, 0, maxY);
        var encoded = generatorId + 1;
        var firstHigh = (encoded / 15) << 5;
        var shape = (byte)(((encoded % 15) << 4) | GeneratorDefaults.Parameter(_document.Tileset, generatorId));
        var first = _document.Header.IsVertical
            ? (byte)((firstHigh & 0xF0) | (y % 15))
            : (byte)((firstHigh & 0xE0) | (y & 0x1F));
        var second = _document.Header.IsVertical
            ? (byte)(((y / 15) << 4) | (x & 0x0F))
            : (byte)x;
        var extra = GeneratorDefaults.ExtraParameter(_document.Tileset, FourByteGeneratorIds, generatorId);
        var element = new LevelElement(next, LevelElementKind.VariableGenerator, generatorId, x, y, shape, extra, first, second, x, y);
        _document = _document with { Elements = _document.Elements.Append(element).ToArray() };
        SelectOnly(next, null);
        Commit(previous);
    }

    public void AddEnemy(byte id, int x = 1, int y = 1)
    {
        if (_document is null)
        {
            return;
        }

        var previous = _document;
        var next = _document.Enemies.Count == 0 ? 0 : _document.Enemies.Max(static item => item.Index) + 1;
        x = Math.Clamp(x, 0, _document.Header.IsVertical ? 15 : 255);
        y = Math.Clamp(y, 0, _document.Header.IsVertical ? (_document.Header.ScreenCount * 15) - 1 : 31);
        var second = _document.Header.IsVertical
            ? (byte)(x & 0x0F)
            : (byte)x;
        var third = _document.Header.IsVertical
            ? (byte)(((y / 15) << 4) | (y % 15))
            : (byte)(y & 0x1F);
        var enemy = new EnemyElement(next, id, x, y, 0, second, third, x, y);
        var enemies = _document.OrderEnemiesForSpawn(_document.Enemies.Append(enemy));
        var reordered = enemies[^1].Index != next;
        _document = _document with { Enemies = enemies };
        SelectOnly(null, next);
        Commit(previous);
        if (reordered) ReportEnemyStreamReordered();
        RefreshEnemyValidation();
    }

    public void ClearLevel()
    {
        if (_document is null || (_document.Elements.Count == 0 && _document.Enemies.Count == 0)) return;
        var previous = _document;
        _document = _document with { Elements = [], Enemies = [] };
        ClearSelectionState();
        Commit(previous);
        ActionFeedbackAvailable?.Invoke(new(DiagnosticSeverity.Information, "Level cleared", "All placed generators and enemies were removed. Use Undo to restore them.", null, false, false));
    }

    public void RefreshEnemyValidation()
    {
        if (_document is null)
        {
            PersistentActionFeedbackCleared?.Invoke();
            return;
        }
        var encoded = Smb3LevelCodec.EncodeEnemies(_document);
        if (!encoded.IsSuccess)
        {
            ActionFeedbackAvailable?.Invoke(new(DiagnosticSeverity.Error, "Enemy cannot be encoded", string.Join(" ", encoded.Diagnostics.Select(item => item.Message)), null, true, true));
            return;
        }
        if (encoded.Value!.Length > _document.OriginalEnemyLength)
        {
            var extraBytes = encoded.Value.Length - _document.OriginalEnemyLength;
            ActionFeedbackAvailable?.Invoke(new(DiagnosticSeverity.Warning, "Sprite data is over capacity", $"This area needs {extraBytes} more sprite byte{(extraBytes == 1 ? string.Empty : "s")}. Remove or replace sprites before exporting.", null, true, true));
            return;
        }
        PersistentActionFeedbackCleared?.Invoke();
    }

    private void ReportEnemyStreamReordered() =>
        ActionFeedbackAvailable?.Invoke(new(
            DiagnosticSeverity.Information,
            "Placed in vanilla spawn order",
            "SMB3 reads enemies in screen order. The stream was reordered so this enemy can spawn; Undo restores the prior order.",
            null,
            false,
            false));

    public void DeleteSelection()
    {
        if (_document is null)
        {
            return;
        }

        var removableElements = _document.Elements
            .Where(item => _selectedElements.Contains(item.Index) && item.Kind != LevelElementKind.Junction)
            .Select(static item => item.Index)
            .ToHashSet();
        var removableEnemies = _document.Enemies
            .Where(item => _selectedEnemies.Contains(item.Index))
            .Select(static item => item.Index)
            .ToHashSet();
        if (removableElements.Count == 0 && removableEnemies.Count == 0) return;

        var previous = _document;
        _document = _document with
        {
            Elements = _document.Elements.Where(item => !removableElements.Contains(item.Index)).ToArray(),
            Enemies = _document.Enemies.Where(item => !removableEnemies.Contains(item.Index)).ToArray()
        };
        ClearSelectionState();

        Commit(previous);
    }

    public void CopySelection()
    {
        _copiedElements = _document?.Elements
            .Where(item => _selectedElements.Contains(item.Index) && item.Kind != LevelElementKind.Junction)
            .ToArray() ?? [];
        _copiedEnemies = _document?.Enemies
            .Where(item => _selectedEnemies.Contains(item.Index))
            .ToArray() ?? [];
    }

    public void PasteSelection()
    {
        if (_copiedElements.Count == 0 && _copiedEnemies.Count == 0) return;
        var anchorX = _copiedElements.Select(static item => item.X).Concat(_copiedEnemies.Select(static item => item.X)).Min() + 1;
        var anchorY = _copiedElements.Select(static item => item.Y).Concat(_copiedEnemies.Select(static item => item.Y)).Min();
        PasteCopiedSelectionAt(anchorX, anchorY);
    }

    private void PasteCopiedSelectionAt(int x, int y)
    {
        if (_document is null || (_copiedElements.Count == 0 && _copiedEnemies.Count == 0)) return;

        var originX = _copiedElements.Select(static item => item.X).Concat(_copiedEnemies.Select(static item => item.X)).Min();
        var originY = _copiedElements.Select(static item => item.Y).Concat(_copiedEnemies.Select(static item => item.Y)).Min();
        var deltaX = x - originX;
        var deltaY = y - originY;
        var maxX = _document.Header.IsVertical ? 15 : 255;
        var maxElementY = _document.Header.IsVertical ? (_document.Header.ScreenCount * 15) - 1 : 26;
        var maxEnemyY = _document.Header.IsVertical ? (_document.Header.ScreenCount * 15) - 1 : 31;
        var previous = _document;
        var nextElement = _document.Elements.Count == 0 ? 0 : _document.Elements.Max(static item => item.Index) + 1;
        var nextEnemy = _document.Enemies.Count == 0 ? 0 : _document.Enemies.Max(static item => item.Index) + 1;
        var elementCopies = _copiedElements.Select(item =>
        {
            var definition = GetEffectiveDefinition(item);
            return item with
            {
                Index = nextElement++,
                X = definition.CanMoveX ? Math.Clamp(item.X + deltaX, 0, maxX) : item.X,
                Y = definition.CanMoveY ? Math.Clamp(item.Y + deltaY, 0, maxElementY) : item.Y
            };
        }).ToArray();
        var enemyCopies = _copiedEnemies.Select(item => item with
        {
            Index = nextEnemy++,
            X = Math.Clamp(item.X + deltaX, 0, maxX),
            Y = Math.Clamp(item.Y + deltaY, 0, maxEnemyY)
        }).ToArray();
        var enemies = _document.OrderEnemiesForSpawn(_document.Enemies.Concat(enemyCopies));
        _document = _document with { Elements = _document.Elements.Concat(elementCopies).ToArray(), Enemies = enemies };
        SelectOnly(null, null);
        foreach (var element in elementCopies) AddSelection(element.Index, null);
        foreach (var enemy in enemyCopies) AddSelection(null, enemy.Index);
        Commit(previous);
        if (!previous.Enemies.Select(static item => item.Index).SequenceEqual(enemies.Select(static item => item.Index))) ReportEnemyStreamReordered();
    }

    public void MoveSelectionInOrder(int delta, bool coalesce = false)
    {
        if (_document is null || _selectedElement is not int selected)
        {
            return;
        }

        var result = _document.MoveElementInEditableOrder(selected, delta);
        ShowLayerHint($"{result.Layer}/{result.TotalLayers}");
        if (!result.Moved)
        {
            LayerOrderFeedback?.Invoke($"{result.Layer}/{result.TotalLayers}");
            ActionFeedbackAvailable?.Invoke(new(
                DiagnosticSeverity.Information,
                result.Layer <= 1 ? "Already at first layer" : "Already at last layer",
                "Layer 1 is processed first/bottom; the highest layer is processed last/top.",
                selected, false, false));
            return;
        }

        var previous = _document;
        _document = result.Document;
        LayerOrderFeedback?.Invoke($"{result.Layer}/{result.TotalLayers}");
        if (coalesce)
        {
            _wheelOrderStartDocument ??= previous;
            _wheelOrderTimer.Stop();
            _wheelOrderTimer.Start();
            UpdateRenderedLevel();
            InvalidateVisual();
        }
        else
        {
            Commit(previous);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        FlushWheelOrderBatch();
        Focus();
        var point = e.GetPosition(this);
        var tileX = (int)(point.X / ScaledTile);
        var tileY = (int)(point.Y / ScaledTile);
        var modifiers = e.KeyModifiers;
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (CatalogPlacementRequested is not null) CatalogPlacementRequested(tileX, tileY);
            else CopySelectionTo(tileX, tileY);
            e.Handled = true;
            return;
        }
        if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            _panning = true;
            _panPointer = e.GetPosition(null);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            !modifiers.HasFlag(KeyModifiers.Shift) && !modifiers.HasFlag(KeyModifiers.Control) &&
            PlayerStartContains(point))
        {
            BeginPlayerStartDrag(e, point);
            return;
        }

        if (!modifiers.HasFlag(KeyModifiers.Shift) && !modifiers.HasFlag(KeyModifiers.Control) &&
            _document is not null && _selectedElement is int currentIndex &&
            _document.Elements.FirstOrDefault(item => item.Index == currentIndex) is { } current)
        {
            var currentBounds = GetElementRect(current);
            var definition = GetEffectiveDefinition(current);
            if (definition.CanResizeLeft && definition.CanResizeRight && definition.CanResizeTop && definition.CanResizeBottom)
            {
                var corners = ResizeCornerHandles(currentBounds);
                var cornerOperation = corners[0].Inflate(3).Contains(point) ? DragOperation.ResizeTopLeft
                    : corners[1].Inflate(3).Contains(point) ? DragOperation.ResizeTopRight
                    : corners[2].Inflate(3).Contains(point) ? DragOperation.ResizeBottomLeft
                    : corners[3].Inflate(3).Contains(point) ? DragOperation.ResizeBottomRight
                    : DragOperation.None;
                if (cornerOperation != DragOperation.None)
                {
                    BeginDrag(e, point, current, cornerOperation);
                    return;
                }
            }
            if (definition.CanResizeTop && ResizeTopEdge(currentBounds).Contains(point))
            {
                BeginDrag(e, point, current, DragOperation.ResizeTop);
                return;
            }
            if (definition.CanResizeRight && ResizeRightEdge(currentBounds).Contains(point))
            {
                BeginDrag(e, point, current, DragOperation.ResizeRight);
                return;
            }
            if (definition.CanResizeBottom && ResizeBottomEdge(currentBounds).Contains(point))
            {
                BeginDrag(e, point, current, DragOperation.ResizeBottom);
                return;
            }
            if (definition.CanResizeLeft && ResizeLeftEdge(currentBounds).Contains(point))
            {
                BeginDrag(e, point, current, DragOperation.ResizeLeft);
                return;
            }
        }

        var hitEnemy = _document?.Enemies
            .Where(item => EnemyContainsPoint(item, point))
            .Select(static item => (int?)item.Index)
            .LastOrDefault();

        var hitElement = hitEnemy is null
            ? _document?.Elements
                .Select(item => (Item: item, Distance: Math.Min(
                    ElementAnchorDistanceSquared(item, point),
                    EncodedAnchorDistanceSquared(item, point))))
                .Where(static candidate => candidate.Distance <= 64)
                .OrderBy(static candidate => candidate.Distance)
                .Select(static candidate => (int?)candidate.Item.Index)
                .FirstOrDefault()
              ?? _document?.Elements
                  .Where(item => ElementContainsTile(item, tileX, tileY))
                  .Select(static item => (int?)item.Index)
                  .LastOrDefault()
            : null;

        if (hitElement is not null || hitEnemy is not null)
        {
            if (modifiers.HasFlag(KeyModifiers.Control))
            {
                ToggleSelection(hitElement, hitEnemy);
            }
            else if (modifiers.HasFlag(KeyModifiers.Shift))
            {
                AddSelection(hitElement, hitEnemy);
            }
            else if (!(HasMultipleSelection &&
                       ((hitElement is int element && IsElementSelected(element)) ||
                        (hitEnemy is int enemy && IsEnemySelected(enemy)))))
            {
                SelectOnly(hitElement, hitEnemy);
            }
            else
            {
                _selectedElement = hitElement;
                _selectedEnemy = hitEnemy;
            }

            var selectedElement = _selectedElement is int elementIndex
                ? _document?.Elements.FirstOrDefault(item => item.Index == elementIndex)
                : null;
            if (!modifiers.HasFlag(KeyModifiers.Shift) && !modifiers.HasFlag(KeyModifiers.Control) &&
                selectedElement?.Kind != LevelElementKind.Junction)
            {
                if (selectedElement is not null)
                {
                    BeginDrag(e, point, selectedElement, DragOperation.Move);
                }
                else if (_selectedEnemy is int enemyIndex)
                {
                    _dragging = true;
                    _dragOperation = DragOperation.Move;
                    _dragStartDocument = _document;
                    _dragPointerStart = point;
                    _dragStartScaledTile = ScaledTile;
                    var selectedEnemy = _document?.Enemies.First(item => item.Index == enemyIndex);
                    _dragItemStartX = selectedEnemy?.X ?? 0;
                    _dragItemStartY = selectedEnemy?.Y ?? 0;
                    e.Pointer.Capture(this);
                }
            }
        }
        else if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _marqueeSelecting = true;
            _marqueeStart = point;
            _marqueeCurrent = point;
            _marqueeModifiers = modifiers;
            e.Pointer.Capture(this);
        }

        if (hitElement is not null || hitEnemy is not null)
        {
            // A canvas selection becomes the current paste source, replacing a
            // previously selected catalog item.
            CopySelection();
            CanvasItemSelected?.Invoke();
        }

        InvalidateVisual();
        NotifySelectionChanged();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_suppressHoverUntilPointerMove)
        {
            _suppressHoverUntilPointerMove = false;
        }
        if (_panning)
        {
            var pointer = e.GetPosition(null);
            var delta = pointer - _panPointer;
            _panPointer = pointer;
            PanRequested?.Invoke(this, new CanvasPanRequestedEventArgs(delta));
            e.Handled = true;
            return;
        }
        var point = e.GetPosition(this);
        if (_dragging)
        {
            EdgeScrollRequested?.Invoke(this, new CanvasEdgeScrollRequestedEventArgs(point));
        }
        if (_marqueeSelecting)
        {
            _marqueeCurrent = point;
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (!_dragging)
        {
            UpdateCursor(point);
            UpdateHoverTip(point);
        }
        if (!_dragging || _document is null)
        {
            return;
        }

        if (_draggingPlayerStart)
        {
            MovePlayerStart(point);
            e.Handled = true;
            return;
        }

        var deltaX = (int)Math.Round(
            (point.X - _dragPointerStart.X) / _dragStartScaledTile,
            MidpointRounding.AwayFromZero);
        var deltaY = (int)Math.Round(
            (point.Y - _dragPointerStart.Y) / _dragStartScaledTile,
            MidpointRounding.AwayFromZero);
        var dragSource = _dragStartDocument ?? _document;
        if (_dragOperation == DragOperation.Move && HasMultipleSelection)
        {
            _document = MoveSelectedItems(dragSource, deltaX, deltaY);
        }
        else if (_selectedElement is int elementIndex)
        {
            var element = dragSource.Elements.First(item => item.Index == elementIndex);
            var definition = GetEffectiveDefinition(element);
            var maxX = dragSource.Header.IsVertical ? 15 : 255;
            var maxY = dragSource.Header.IsVertical ? (dragSource.Header.ScreenCount * 15) - 1 : 26;
            var x = Math.Clamp(_dragItemStartX + deltaX, 0, maxX);
            var y = Math.Clamp(_dragItemStartY + deltaY, 0, maxY);
            _document = _dragOperation switch
            {
                DragOperation.ResizeTopLeft => ResizeCorner(dragSource, elementIndex, definition,
                    -QuantizedDelta(deltaX, _dragResizeTilesPerStep), -QuantizedDelta(deltaY, _dragResizeTilesPerStep),
                    _dragItemStartX + QuantizedDelta(deltaX, _dragResizeTilesPerStep) * _dragResizeTilesPerStep,
                    _dragItemStartY + QuantizedDelta(deltaY, _dragResizeTilesPerStep) * _dragResizeTilesPerStep),
                DragOperation.ResizeTopRight => ResizeCorner(dragSource, elementIndex, definition,
                    QuantizedDelta(deltaX, _dragResizeTilesPerStep), -QuantizedDelta(deltaY, _dragResizeTilesPerStep), null,
                    _dragItemStartY + QuantizedDelta(deltaY, _dragResizeTilesPerStep) * _dragResizeTilesPerStep),
                DragOperation.ResizeBottomLeft => ResizeCorner(dragSource, elementIndex, definition,
                    -QuantizedDelta(deltaX, _dragResizeTilesPerStep), QuantizedDelta(deltaY, _dragResizeTilesPerStep),
                    _dragItemStartX + QuantizedDelta(deltaX, _dragResizeTilesPerStep) * _dragResizeTilesPerStep),
                DragOperation.ResizeBottomRight => ResizeCorner(dragSource, elementIndex, definition,
                    QuantizedDelta(deltaX, _dragResizeTilesPerStep), QuantizedDelta(deltaY, _dragResizeTilesPerStep)),
                DragOperation.ResizeTop when definition.TopResizePreservesBottom => ResizeTopPreservingBottom(
                    dragSource, elementIndex, deltaY),
                DragOperation.ResizeTop when definition.CanResizeBottom => ResizeVertical(
                    dragSource, elementIndex, definition, -QuantizedDelta(deltaY, _dragResizeTilesPerStep),
                    _dragItemStartY + QuantizedDelta(deltaY, _dragResizeTilesPerStep) * _dragResizeTilesPerStep),
                DragOperation.ResizeTop => definition.TopResizeChangesPosition
                    ? dragSource.ResizeElement(elementIndex, top: y)
                    : definition.TopResizePreservesBottom
                        ? dragSource.ResizeElement(elementIndex,
                            top: _dragItemStartY + QuantizedDelta(deltaY, _dragResizeTilesPerStep) * _dragResizeTilesPerStep,
                            parameter: _dragStartParameter - QuantizedDelta(deltaY, _dragResizeTilesPerStep))
                        : dragSource.ResizeElement(elementIndex, parameter: _dragStartParameter - QuantizedDelta(deltaY, _dragResizeTilesPerStep)),
                DragOperation.ResizeRight => ResizeHorizontal(dragSource, elementIndex, definition, QuantizedDelta(deltaX, _dragResizeTilesPerStep)),
                DragOperation.ResizeLeft => ResizeHorizontal(
                    dragSource, elementIndex, definition, -QuantizedDelta(deltaX, _dragResizeTilesPerStep),
                    _dragItemStartX + QuantizedDelta(deltaX, _dragResizeTilesPerStep) * _dragResizeTilesPerStep),
                DragOperation.ResizeBottom => ResizeVertical(dragSource, elementIndex, definition, QuantizedDelta(deltaY, _dragResizeTilesPerStep)),
                _ => dragSource.MoveElement(
                    elementIndex,
                    definition.CanMoveX ? x : _dragItemStartX,
                    definition.CanMoveY ? y : _dragItemStartY)
            };
        }
        else if (_selectedEnemy is int enemyIndex)
        {
            var maxX = dragSource.Header.IsVertical ? 15 : 255;
            var maxY = dragSource.Header.IsVertical ? (dragSource.Header.ScreenCount * 15) - 1 : 31;
            var x = Math.Clamp(_dragItemStartX + deltaX, 0, maxX);
            var y = Math.Clamp(_dragItemStartY + deltaY, 0, maxY);
            var moved = dragSource.MoveEnemy(enemyIndex, x, y);
            _document = moved with { Enemies = moved.OrderEnemiesForSpawn(moved.Enemies) };
        }

        UpdateRenderedLevel();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverTip = null;
        _hoverTimer.Stop();
        ToolTip.SetIsOpen(this, false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (_marqueeSelecting)
        {
            _marqueeCurrent = e.GetPosition(this);
            _marqueeSelecting = false;
            ApplyMarqueeSelection();
            e.Pointer.Capture(null);
            InvalidateVisual();
            NotifySelectionChanged();
            e.Handled = true;
            return;
        }
        if (_dragging && _dragStartDocument is not null && _document is not null && _dragStartDocument != _document)
        {
            EditCommitted?.Invoke(this, new LevelEditCommittedEventArgs(_dragStartDocument, _document));
            if (!_dragStartDocument.Enemies.Select(static enemy => enemy.Index)
                .SequenceEqual(_document.Enemies.Select(static enemy => enemy.Index)))
            {
                ReportEnemyStreamReordered();
            }
        }

        _dragging = false;
        _draggingPlayerStart = false;
        _dragOperation = DragOperation.None;
        _dragStartDocument = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var oldZoom = Zoom;
            var newZoom = Math.Clamp(Math.Round(
                oldZoom + (e.Delta.Y > 0 ? 0.1 : -0.1),
                1,
                MidpointRounding.AwayFromZero), 0.25, 8.0);
            var pointer = e.GetPosition(this);
            ZoomRequested?.Invoke(this, new CanvasZoomRequestedEventArgs(
                oldZoom, newZoom, pointer, new Point(pointer.X / oldZoom, pointer.Y / oldZoom)));
            e.Handled = true;
        }
        else if (_document is not null && _selectedElement is int index &&
                 _document.Elements.Any(item => item.Index == index))
        {
            _suppressHoverUntilPointerMove = true;
            _hoverTimer.Stop();
            ToolTip.SetIsOpen(this, false);
            _layerHintPoint = e.GetPosition(this);
            MoveSelectionInOrder(e.Delta.Y > 0 ? 1 : -1, coalesce: true);
            e.Handled = true;
        }
    }

    private Rect MarqueeRect()
    {
        var left = Math.Min(_marqueeStart.X, _marqueeCurrent.X);
        var top = Math.Min(_marqueeStart.Y, _marqueeCurrent.Y);
        return new Rect(left, top, Math.Abs(_marqueeCurrent.X - _marqueeStart.X), Math.Abs(_marqueeCurrent.Y - _marqueeStart.Y));
    }

    private LevelDocument MoveSelectedItems(LevelDocument source, int deltaX, int deltaY)
    {
        var maxX = source.Header.IsVertical ? 15 : 255;
        var maxElementY = source.Header.IsVertical ? (source.Header.ScreenCount * 15) - 1 : 26;
        var maxEnemyY = source.Header.IsVertical ? (source.Header.ScreenCount * 15) - 1 : 31;
        var moved = source;
        foreach (var element in source.Elements.Where(item => _selectedElements.Contains(item.Index) && item.Kind != LevelElementKind.Junction))
        {
            var definition = GetEffectiveDefinition(element);
            moved = moved.MoveElement(element.Index,
                definition.CanMoveX ? Math.Clamp(element.X + deltaX, 0, maxX) : element.X,
                definition.CanMoveY ? Math.Clamp(element.Y + deltaY, 0, maxElementY) : element.Y);
        }
        foreach (var enemy in source.Enemies.Where(item => _selectedEnemies.Contains(item.Index)))
        {
            moved = moved.MoveEnemy(enemy.Index,
                Math.Clamp(enemy.X + deltaX, 0, maxX),
                Math.Clamp(enemy.Y + deltaY, 0, maxEnemyY));
        }
        return moved with { Enemies = moved.OrderEnemiesForSpawn(moved.Enemies) };
    }

    private void ApplyMarqueeSelection()
    {
        if (_document is null) return;
        var marquee = MarqueeRect();
        if (!_marqueeModifiers.HasFlag(KeyModifiers.Shift) && !_marqueeModifiers.HasFlag(KeyModifiers.Control))
        {
            ClearSelectionState();
        }

        foreach (var element in _document.Elements)
        {
            var bounds = GetElementBounds(element);
            var rect = new Rect(bounds.Left * ScaledTile, bounds.Top * ScaledTile,
                Math.Max(1, bounds.Width) * ScaledTile, Math.Max(1, bounds.Height) * ScaledTile);
            if (!marquee.Intersects(rect)) continue;
            if (_marqueeModifiers.HasFlag(KeyModifiers.Control)) ToggleSelection(element.Index, null);
            else AddSelection(element.Index, null);
        }

        foreach (var enemy in _document.Enemies)
        {
            var rect = new Rect(enemy.X * ScaledTile, enemy.Y * ScaledTile, ScaledTile, ScaledTile);
            if (!marquee.Intersects(rect)) continue;
            if (_marqueeModifiers.HasFlag(KeyModifiers.Control)) ToggleSelection(null, enemy.Index);
            else AddSelection(null, enemy.Index);
        }

        if (HasSelection)
        {
            CopySelection();
            CanvasItemSelected?.Invoke();
        }
    }

    private double ScaledTile => TilePixels * _zoom;

    private void DrawLayerHint(DrawingContext context)
    {
        if (string.IsNullOrWhiteSpace(_layerHint)) return;
        var text = new FormattedText(_layerHint, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), 13, Brushes.White);
        var origin = new Point(Math.Max(2, _layerHintPoint.X + 12), Math.Max(2, _layerHintPoint.Y + 12));
        var background = new Rect(origin.X - 6, origin.Y - 4, text.Width + 12, text.Height + 8);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#E6172231")), background, 4);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#7897BA"))), background, 4);
        context.DrawText(text, origin);
    }

    private void DrawGrid(DrawingContext context, int columns, int rows)
    {
        var lightBackground = IsLightBackground();
        var minorColor = lightBackground
            ? Color.FromArgb(48, 25, 65, 92)
            : Color.FromArgb(32, 220, 235, 255);
        var screenColor = lightBackground
            ? Color.FromArgb(125, 32, 105, 150)
            : Color.FromArgb(150, 120, 195, 255);
        var minorPen = new Pen(new SolidColorBrush(minorColor), 1);
        var screenPen = new Pen(new SolidColorBrush(screenColor), 2);
        for (var column = 0; column <= columns; column++)
        {
            var x = column * ScaledTile;
            if (column % 16 == 0 || ShowGrid)
            {
                context.DrawLine(column % 16 == 0 ? screenPen : minorPen, new Point(x, 0), new Point(x, rows * ScaledTile));
            }
        }

        if (ShowGrid)
        {
            for (var row = 0; row <= rows; row++)
            {
                var y = row * ScaledTile;
                context.DrawLine(minorPen, new Point(0, y), new Point(columns * ScaledTile, y));
            }
        }
    }

    private bool IsLightBackground()
    {
        if (_renderSnapshot?.ArgbPixels is not { Count: > 0 } pixels) return false;
        var sample = pixels[0];
        var red = (sample >> 16) & 0xFF;
        var green = (sample >> 8) & 0xFF;
        var blue = sample & 0xFF;
        return ((red * 299) + (green * 587) + (blue * 114)) / 1000 > 150;
    }

    private void Commit(LevelDocument previous)
    {
        if (_document is null || previous == _document)
        {
            return;
        }

        UpdateRenderedLevel();
        UpdateExtent();
        InvalidateVisual();
        EditCommitted?.Invoke(this, new LevelEditCommittedEventArgs(previous, _document));
    }

    private void UpdateExtent()
    {
        var columns = _document?.Header.IsVertical == true
            ? 16
            : Math.Max(16, _document?.Header.ScreenCount * 16 ?? 16);
        var rows = _document?.Header.IsVertical == true
            ? _document.Header.ScreenCount * 15
            : 27;
        Width = Math.Max(720, columns * ScaledTile);
        Height = (rows * ScaledTile) + BottomHandleGutter;
    }

    private bool UpdateRenderedLevel()
    {
        if (_rom is null || _document is null)
        {
            _levelBitmap?.Dispose();
            foreach (var bitmap in _enemyBitmaps.Values) bitmap.Dispose();
            _enemyBitmaps.Clear();
            _levelBitmap = null;
            _renderSnapshot = null;
            SetActiveRenderDiagnostics([]);
            return false;
        }

        var rendered = _renderer.Render(_rom, _document, paletteOverrides: _paletteOverrides);
        if (!rendered.IsSuccess)
        {
            SetActiveRenderDiagnostics(rendered.Diagnostics);
            if (_unsafeElementIndex is int excluded)
            {
                var preview = _renderer.Render(_rom, _document, excluded, _paletteOverrides);
                if (preview.IsSuccess)
                {
                    ApplyRenderedSnapshot(preview.Value!, updateState: false);
                }
            }
            return false;
        }
        SetActiveRenderDiagnostics([]);

        ApplyRenderedSnapshot(rendered.Value!, updateState: true);
        return true;
    }

    private void ApplyRenderedSnapshot(LevelRenderSnapshot newSnapshot, bool updateState)
    {
        var newLevelBitmap = CreateBitmap(
            newSnapshot.ArgbPixels,
            newSnapshot.PixelWidth,
            newSnapshot.PixelHeight,
            AlphaFormat.Opaque);
        var newEnemyBitmaps = new Dictionary<byte, WriteableBitmap>();
        foreach (var pair in newSnapshot.EnemySprites)
        {
            newEnemyBitmaps[pair.Key] = CreateBitmap(
                pair.Value.ArgbPixels,
                pair.Value.PixelWidth,
                pair.Value.PixelHeight,
                AlphaFormat.Premul);
        }
        _levelBitmap?.Dispose();
        foreach (var bitmap in _enemyBitmaps.Values) bitmap.Dispose();
        _enemyBitmaps.Clear();
        foreach (var pair in newEnemyBitmaps) _enemyBitmaps[pair.Key] = pair.Value;
        _renderSnapshot = newSnapshot;
        if (updateState && _document is not null)
        {
            _lastValidElementState.Clear();
            _lastValidElementBounds.Clear();
            foreach (var element in _document.Elements)
            {
                _lastValidElementState[element.Index] = (element.X, element.Y, element.Parameter, element.ExtraParameter ?? 0);
                if (newSnapshot.ElementBounds.TryGetValue(element.Index, out var bounds))
                {
                    _lastValidElementBounds[element.Index] = bounds;
                }
            }
        }
        _levelBitmap = newLevelBitmap;
    }

    private void SetActiveRenderDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        _activeRenderDiagnostics = diagnostics;
        _unsafeElementIndex = diagnostics
            .Select(item => TryReadElementIndex(item.Code, out var index) ? (int?)index : null)
            .FirstOrDefault(item => item is not null);
        _elementSafetyDetails.Clear();
        foreach (var diagnostic in diagnostics)
        {
            if (TryReadElementIndex(diagnostic.Code, out var index))
            {
                _elementSafetyDetails[index] = diagnostic.Message;
            }
        }
        ActiveRenderDiagnosticsChanged?.Invoke(diagnostics);
        if (diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error) is { } error)
        {
            ActionFeedbackAvailable?.Invoke(new(
                error.Severity,
                "Current level is unsafe",
                error.Message,
                TryReadElementIndex(error.Code, out var index) ? index : null,
                true,
                true));
        }
        else
        {
            PersistentActionFeedbackCleared?.Invoke();
        }
    }

    private static bool TryReadElementIndex(string code, out int index)
    {
        index = -1;
        const string marker = ":ELEMENT:";
        var position = code.IndexOf(marker, StringComparison.Ordinal);
        return position >= 0 && int.TryParse(code[(position + marker.Length)..], out index);
    }

    private static WriteableBitmap CreateBitmap(
        IReadOnlyList<uint> sourcePixels,
        int width,
        int height,
        AlphaFormat alphaFormat)
    {
        var pixels = new int[sourcePixels.Count];
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] = unchecked((int)sourcePixels[index]);
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            alphaFormat);
        using var framebuffer = bitmap.Lock();
        for (var row = 0; row < height; row++)
        {
            Marshal.Copy(
                pixels,
                row * width,
                IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                width);
        }

        return bitmap;
    }

    private bool ElementContainsTile(LevelElement element, int x, int y)
    {
        var bounds = GetElementBounds(element);
        return x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom;
    }

    private double ElementAnchorDistanceSquared(LevelElement element, Point point)
    {
        var anchor = GetElementAnchor(element);
        var anchorX = (anchor.X + 0.5) * ScaledTile;
        var anchorY = (anchor.Y + 0.5) * ScaledTile;
        var deltaX = point.X - anchorX;
        var deltaY = point.Y - anchorY;
        return (deltaX * deltaX) + (deltaY * deltaY);
    }

    private double EncodedAnchorDistanceSquared(LevelElement element, Point point)
    {
        var anchorX = (element.X + 0.5) * ScaledTile;
        var anchorY = (element.Y + 0.5) * ScaledTile;
        var deltaX = point.X - anchorX;
        var deltaY = point.Y - anchorY;
        return (deltaX * deltaX) + (deltaY * deltaY);
    }

    private LevelElementRenderBounds GetElementBounds(LevelElement element)
    {
        if (_unsafeElementIndex == element.Index && _lastValidElementBounds.TryGetValue(element.Index, out var lastValidBounds))
        {
            return lastValidBounds;
        }
        if (_renderSnapshot is null ||
            !_renderSnapshot.ElementBounds.TryGetValue(element.Index, out var trackedBounds))
        {
            return new LevelElementRenderBounds(element.X, element.Y, element.X + 1, element.Y + 1);
        }
        if (_lastValidElementState.TryGetValue(element.Index, out var state))
        {
            var dx = element.X - state.X;
            var dy = element.Y - state.Y;
            return trackedBounds with { Left = trackedBounds.Left + dx, Right = trackedBounds.Right + dx,
                Top = trackedBounds.Top + dy, Bottom = trackedBounds.Bottom + dy };
        }
        return trackedBounds;
    }

    private LevelElementRenderAnchor GetElementAnchor(LevelElement element) =>
        _unsafeElementIndex != element.Index && _renderSnapshot?.ElementAnchors.TryGetValue(element.Index, out var trackedAnchor) == true
            ? trackedAnchor
            : new LevelElementRenderAnchor(element.X, element.Y);

    private void ShowLayerHint(string value)
    {
        _layerHint = value;
        _layerHintTimer.Stop();
        _layerHintTimer.Start();
        InvalidateVisual();
    }

    private Point PlayerStartPoint => _document is null
        ? default
        : new Point(
            PlayerStartXCoordinates[_document.Header.PlayerStartX] * ScaledTile,
            PlayerStartYCoordinates[_document.Header.PlayerStartY] * ScaledTile);

    private Rect PlayerStartSpriteBounds
    {
        get
        {
            var anchor = PlayerStartPoint;
            // SMB3 uses 8x16 hardware sprites. PF3E uses two of them on the
            // lower OAM row, producing a 16x16 standing Small Mario.
            return new Rect(anchor.X + (ScaledTile / 2), anchor.Y + ScaledTile, ScaledTile, ScaledTile);
        }
    }

    private bool PlayerStartContains(Point point)
    {
        if (_document is null) return false;
        return PlayerStartSpriteBounds.Inflate(Math.Max(5, ScaledTile * 0.3)).Contains(point);
    }

    private void DrawPlayerStart(DrawingContext context)
    {
        if (_document is null) return;
        var bounds = PlayerStartSpriteBounds;
        if (_smallMarioPixels is { Length: 256 })
        {
            var pixel = ScaledTile / TilePixels;
            var colors = new[]
            {
                (Avalonia.Media.IBrush)Brushes.Transparent,
                new SolidColorBrush(Color.FromUInt32(NesPalette.Argb[0x16])),
                new SolidColorBrush(Color.FromUInt32(NesPalette.Argb[0x36])),
                new SolidColorBrush(Color.FromUInt32(NesPalette.Argb[0x0F]))
            };
            for (var y = 0; y < 16; y++)
            for (var x = 0; x < 16; x++)
            {
                // The editor start marker faces the direction of normal level
                // entry (right) while retaining the ROM's Small Mario pixels.
                var color = _smallMarioPixels[(y * 16) + (15 - x)];
                if (color != 0) context.FillRectangle(colors[color], new Rect(bounds.X + (x * pixel), bounds.Y + (y * pixel), pixel, pixel));
            }
        }
        else
        {
            context.FillRectangle(new SolidColorBrush(Color.Parse("#D94841")), new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height * 0.3));
            context.FillRectangle(new SolidColorBrush(Color.Parse("#3E72C4")), new Rect(bounds.X, bounds.Y + (bounds.Height * 0.55), bounds.Width, bounds.Height * 0.45));
        }
        context.DrawRectangle(null, new Pen(_playerStartSelected ? Brushes.White : new SolidColorBrush(Color.Parse("#F6D365")), _playerStartSelected ? 2 : 1), bounds.Inflate(2));
    }

    private void BeginPlayerStartDrag(PointerPressedEventArgs e, Point point)
    {
        if (_document is null) return;
        ClearSelectionState();
        _playerStartSelected = true;
        _dragging = true;
        _draggingPlayerStart = true;
        _dragStartDocument = _document;
        _dragPointerStart = point;
        _dragStartScaledTile = ScaledTile;
        e.Pointer.Capture(this);
        NotifySelectionChanged();
        InvalidateVisual();
        e.Handled = true;
    }

    private void MovePlayerStart(Point point)
    {
        if (_document is null) return;
        var logicalX = (point.X / ScaledTile) - 0.5;
        var logicalY = (point.Y / ScaledTile) - 1;
        var x = ClosestPlayerStartIndex(PlayerStartXCoordinates, logicalX);
        var y = ClosestPlayerStartIndex(PlayerStartYCoordinates, logicalY);
        var header = _document.Header.WithPlayerStart(x, y);
        if (header == _document.Header) return;
        _document = _document with { Header = header };
        InvalidateVisual();
    }

    private static int ClosestPlayerStartIndex(IReadOnlyList<double> values, double value) =>
        Enumerable.Range(0, values.Count).MinBy(index => Math.Abs(values[index] - value));

    private void LoadSmallMarioSprite()
    {
        _smallMarioPixels = null;
        if (_rom is null) return;
        const int smallMarioStandingPage = 0x53;
        // PF3E is the idle Small Mario frame. Each OAM pattern is an 8x16
        // sprite: $05 maps to CHR tiles $04/$05 and $07 maps to $06/$07.
        var sprite = new byte[16 * 16];
        var tiles = new[,]
        {
            { 0x04, 0x06 },
            { 0x05, 0x07 }
        };
        for (var row = 0; row < 2; row++)
        for (var column = 0; column < 2; column++)
        {
            var offset = (smallMarioStandingPage * 0x400) + (tiles[row, column] * ChrTileDecoder.BytesPerTile);
            if (offset > _rom.Chr.Length - ChrTileDecoder.BytesPerTile) return;
            var decoded = ChrTileDecoder.DecodeTile(_rom.Chr.Slice(offset, ChrTileDecoder.BytesPerTile), 0);
            if (!decoded.IsSuccess) return;
            for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                sprite[((row * 8 + y) * 16) + (column * 8 + x)] = decoded.Value![(y * 8) + x];
            }
        }
        _smallMarioPixels = sprite;
    }

    private bool EnemyContainsPoint(EnemyElement enemy, Point point)
    {
        if (_renderSnapshot?.EnemySprites.TryGetValue(enemy.Id, out var preview) == true)
        {
            return new Rect(
                ((enemy.X * 16) + preview.OffsetX) * _zoom,
                ((enemy.Y * 16) + preview.OffsetY) * _zoom,
                preview.PixelWidth * _zoom,
                preview.PixelHeight * _zoom).Contains(point);
        }

        // Unknown/unpreviewable sprites retain a conservative command-cell
        // hit area rather than an arbitrary neighboring-tile radius.
        return new Rect(enemy.X * ScaledTile, enemy.Y * ScaledTile,
            ScaledTile, ScaledTile).Contains(point);
    }

    private void UpdateHoverTip(Point point)
    {
        if (_document is null)
        {
            ToolTip.SetIsOpen(this, false);
            return;
        }

        var tileX = (int)(point.X / ScaledTile);
        var tileY = (int)(point.Y / ScaledTile);
        if (PlayerStartContains(point))
        {
            var startTip = $"Mario start\nX: {PlayerStartXCoordinates[_document.Header.PlayerStartX]:0.##} tiles\nY: {PlayerStartYCoordinates[_document.Header.PlayerStartY]:0} tiles\nDrag to one of SMB3's available start positions.";
            if (startTip != _hoverTip)
            {
                _hoverTimer.Stop();
                ToolTip.SetIsOpen(this, false);
                _hoverTip = startTip;
                ToolTip.SetTip(this, startTip);
                _hoverTimer.Start();
            }
            return;
        }
        var enemy = _document.Enemies.LastOrDefault(item =>
            EnemyContainsPoint(item, point));
        string? tip = enemy is not null ? ObjectCatalogNames.Describe(enemy) : null;
        if (tip is null)
        {
            var radius = Math.Max(8, ScaledTile * 0.65);
            var element = _document.Elements
                .Select(item => (Item: item, Distance: ElementAnchorDistanceSquared(item, point)))
                .Where(item => item.Distance <= radius * radius)
                .OrderBy(item => item.Distance)
                .Select(item => item.Item)
                .FirstOrDefault();
            if (element is not null)
            {
                var definition = GetEffectiveDefinition(element);
                tip = ObjectCatalogNames.Describe(_document.Tileset, element) +
                      $"\nConstraint: {definition.Constraint}";
                if (_elementSafetyDetails.TryGetValue(element.Index, out var safety))
                {
                    tip += $"\nSafety: {safety}";
                }
            }
        }

        if (tip == _hoverTip)
        {
            return;
        }
        _hoverTimer.Stop();
        ToolTip.SetIsOpen(this, false);
        _hoverTip = tip;
        ToolTip.SetTip(this, tip);
        if (tip is not null) _hoverTimer.Start();
    }

    private void UpdateCursor(Point point)
    {
        var type = StandardCursorType.Arrow;
        if (PlayerStartContains(point))
        {
            type = StandardCursorType.SizeAll;
        }
        else if (_document is not null && _selectedElement is int index &&
            _document.Elements.FirstOrDefault(item => item.Index == index) is { } element)
        {
            var definition = GetEffectiveDefinition(element);
            var rect = GetElementRect(element);
            if (definition.CanResizeLeft && definition.CanResizeRight && definition.CanResizeTop && definition.CanResizeBottom &&
                ResizeCornerHandles(rect).Any(handle => handle.Inflate(3).Contains(point)))
            {
                type = StandardCursorType.SizeAll;
            }
            else if (definition.CanResizeTop && ResizeTopEdge(rect).Contains(point))
            {
                type = StandardCursorType.SizeNorthSouth;
            }
            else if (definition.CanResizeRight && ResizeRightEdge(rect).Contains(point))
            {
                type = StandardCursorType.SizeWestEast;
            }
            else if (definition.CanResizeBottom && ResizeBottomEdge(rect).Contains(point))
            {
                type = StandardCursorType.SizeNorthSouth;
            }
            else if (definition.CanResizeLeft && ResizeLeftEdge(rect).Contains(point))
            {
                type = StandardCursorType.SizeWestEast;
            }
            else if (rect.Contains(point))
            {
                type = definition.CanMoveX && definition.CanMoveY
                    ? StandardCursorType.SizeAll
                    : StandardCursorType.Hand;
            }
        }
        Cursor = new Cursor(type);
    }

    private void NotifySelectionChanged()
    {
        if (_document is null) return;
        if (_playerStartSelected)
        {
            SelectionDescriptionChanged?.Invoke($"Mario start\nX: {PlayerStartXCoordinates[_document.Header.PlayerStartX]:0.##} tiles\nY: {PlayerStartYCoordinates[_document.Header.PlayerStartY]:0} tiles");
        }
        else if (HasMultipleSelection)
        {
            var count = _selectedElements.Count + _selectedEnemies.Count;
            SelectionDescriptionChanged?.Invoke($"{count} items selected\nDrag to move the group. Ctrl+C, Ctrl+V, Delete, and Backspace apply to the group.");
        }
        else if (_selectedElement is int elementIndex && _document.Elements.FirstOrDefault(item => item.Index == elementIndex) is { } element)
        {
            var definition = GetEffectiveDefinition(element);
            var step = definition.CanResizeRight
                ? MeasureResizeStep(element, DragOperation.ResizeRight)
                : definition.CanResizeTop || definition.CanResizeBottom
                    ? MeasureResizeStep(element, DragOperation.ResizeBottom)
                    : 1;
            var stepText = step > 1 ? $"\nResize step: {step} tiles" : string.Empty;
            var minimumShape = GeneratorDefaults.Parameter(_document.Tileset, element.GeneratorId);
            var minimumExtra = definition.HorizontalSizeUsesExtraParameter
                ? GeneratorDefaults.ClampExtraParameter(_document.Tileset, element.GeneratorId, 0)
                : 0;
            var minimumText = minimumShape > 0 || minimumExtra > 0
                ? $"\nMinimum vanilla size: {Math.Max(minimumShape, minimumExtra)} (smaller values wrap into a giant object)"
                : string.Empty;
            SelectionDescriptionChanged?.Invoke($"{ObjectCatalogNames.Describe(_document.Tileset, element)}\nConstraint: {definition.Constraint}\nSize parameter: {element.Parameter}{stepText}{minimumText}");
        }
        else if (_selectedEnemy is int enemyIndex && _document.Enemies.FirstOrDefault(item => item.Index == enemyIndex) is { } enemy)
        {
            SelectionDescriptionChanged?.Invoke(ObjectCatalogNames.Describe(enemy));
        }
        else
        {
            SelectionDescriptionChanged?.Invoke("Nothing selected");
        }
    }

    private void FlushWheelOrderBatch()
    {
        _wheelOrderTimer.Stop();
        if (_wheelOrderStartDocument is null || _document is null || _wheelOrderStartDocument == _document)
        {
            _wheelOrderStartDocument = null;
            return;
        }
        var previous = _wheelOrderStartDocument;
        _wheelOrderStartDocument = null;
        EditCommitted?.Invoke(this, new LevelEditCommittedEventArgs(previous, _document));
    }

    private void BeginDrag(PointerPressedEventArgs e, Point point, LevelElement element, DragOperation operation)
    {
        _dragging = true;
        _dragOperation = operation;
        _dragStartDocument = _document;
        _dragPointerStart = point;
        _dragStartScaledTile = ScaledTile;
        _dragItemStartX = element.X;
        _dragItemStartY = element.Y;
        _dragStartParameter = element.Parameter;
        _dragStartExtraParameter = element.ExtraParameter ?? 0;
        _dragStartRenderedBottom = GetElementBounds(element).Bottom;
        var definition = GetEffectiveDefinition(element);
        _dragResizeTilesPerStep = operation == DragOperation.ResizeTop && definition.TopResizePreservesBottom
            ? 1
            : operation is DragOperation.ResizeTopLeft or DragOperation.ResizeTopRight or DragOperation.ResizeBottomLeft or DragOperation.ResizeBottomRight
                ? Math.Max(MeasureResizeStep(element, DragOperation.ResizeRight), MeasureResizeStep(element, DragOperation.ResizeBottom))
                : operation is DragOperation.ResizeRight or DragOperation.ResizeTop or DragOperation.ResizeBottom or DragOperation.ResizeLeft
                    ? MeasureResizeStep(element, operation)
                    : 1;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private Rect GetElementRect(LevelElement element)
    {
        var bounds = GetElementBounds(element);
        return new Rect(bounds.Left * ScaledTile, bounds.Top * ScaledTile,
            Math.Max(1, bounds.Width) * ScaledTile, Math.Max(1, bounds.Height) * ScaledTile);
    }

    private static Rect ResizeTopHandle(Rect bounds) => new(bounds.Center.X - 5, bounds.Top - 5, 10, 10);
    private static Rect ResizeRightHandle(Rect bounds) => new(bounds.Right - 5, bounds.Center.Y - 5, 10, 10);
    private static Rect ResizeBottomHandle(Rect bounds) => new(bounds.Center.X - 5, bounds.Bottom - 5, 10, 10);
    private static Rect ResizeLeftHandle(Rect bounds) => new(bounds.Left - 5, bounds.Center.Y - 5, 10, 10);
    private static Rect ResizeTopEdge(Rect bounds) => new(bounds.Left, bounds.Top - 6, bounds.Width, 12);
    private static Rect ResizeRightEdge(Rect bounds) => new(bounds.Right - 6, bounds.Top, 12, bounds.Height);
    private static Rect ResizeBottomEdge(Rect bounds) => new(bounds.Left, bounds.Bottom - 6, bounds.Width, 12);
    private static Rect ResizeLeftEdge(Rect bounds) => new(bounds.Left - 6, bounds.Top, 12, bounds.Height);
    private static Rect[] ResizeCornerHandles(Rect bounds) =>
    [
        new(bounds.Left - 5, bounds.Top - 5, 10, 10), new(bounds.Right - 5, bounds.Top - 5, 10, 10),
        new(bounds.Left - 5, bounds.Bottom - 5, 10, 10), new(bounds.Right - 5, bounds.Bottom - 5, 10, 10)
    ];

    private int MeasureResizeStep(LevelElement element, DragOperation operation)
    {
        if (_rom is null || _document is null || _renderSnapshot is null) return 1;
        var definition = GeneratorDefinition.For(_document, element);
        var horizontal = operation is DragOperation.ResizeRight or DragOperation.ResizeLeft;
        var usesExtra = horizontal && definition.HorizontalSizeUsesExtraParameter ||
                        !horizontal && definition.VerticalSizeUsesExtraParameter;
        var delta = SafeProbeDelta(_document, element, usesExtra);
        if (delta == 0) return 1;
        var candidate = usesExtra
            ? _document.ResizeElement(element.Index, extraParameter: (element.ExtraParameter ?? 0) + delta)
            : _document.ResizeElement(element.Index, parameter: element.Parameter + delta);
        var rendered = _renderer.Render(_rom, candidate);
        if (!rendered.IsSuccess ||
            !_renderSnapshot.ElementBounds.TryGetValue(element.Index, out var before) ||
            !rendered.Value!.ElementBounds.TryGetValue(element.Index, out var after)) return 1;
        var difference = horizontal
            ? Math.Abs(after.Width - before.Width)
            : Math.Abs(after.Height - before.Height);
        return Math.Max(1, difference);
    }

    private static int QuantizedDelta(int tileDelta, int tilesPerStep) =>
        (int)Math.Round((double)tileDelta / Math.Max(1, tilesPerStep), MidpointRounding.AwayFromZero);

    private static int SafeProbeDelta(LevelDocument document, LevelElement element, bool extra)
    {
        var current = extra ? element.ExtraParameter ?? 0 : element.Parameter;
        var minimum = extra
            ? GeneratorDefaults.ClampExtraParameter(document.Tileset, element.GeneratorId, 0)
            : GeneratorDefaults.Parameter(document.Tileset, element.GeneratorId);
        return current > minimum ? -1 : current < (extra ? 255 : 15) ? 1 : 0;
    }

    private LevelDocument ResizeTopPreservingBottom(LevelDocument source, int index, int topDelta)
    {
        var element = source.Elements.FirstOrDefault(item => item.Index == index);
        if (element is null) return source;

        // The pointer edits the visual top edge directly. This matters for
        // rectangles already touching the lower boundary: their encoded Y
        // anchor and rendered bounds can differ after previous edits.
        var top = Math.Clamp(_dragItemStartY + (topDelta * _dragResizeTilesPerStep), 0, _dragStartRenderedBottom - 1);
        var parameter = GeneratorDefaults.ClampParameter(source.Tileset, element.GeneratorId, _dragStartRenderedBottom - top - 1);
        return source.ResizeElement(index, top: top, parameter: parameter);
    }


    private GeneratorDefinition GetEffectiveDefinition(LevelElement element)
    {
        if (_document is null) return new GeneratorDefinition("Object", GeneratorConstraint.Unknown, false, false);
        if (!ReferenceEquals(_definitionDocument, _document))
        {
            _definitionDocument = _document;
            _definitionCache.Clear();
        }
        if (_definitionCache.TryGetValue(element.Index, out var cached)) return cached;

        var definition = GeneratorDefinition.For(_document, element);
        if (element.Kind == LevelElementKind.VariableGenerator && definition.Constraint == GeneratorConstraint.Unknown)
        {
            var primary = MeasureParameterEffect(element, extra: false);
            var extra = element.ExtraParameter is not null ? MeasureParameterEffect(element, extra: true) : (Width: 0, Height: 0);
            var hasWidth = primary.Width > 0 || extra.Width > 0;
            var hasHeight = primary.Height > 0 || extra.Height > 0;
            definition = definition with
            {
                CanResizeLeft = hasWidth,
                CanResizeRight = hasWidth,
                CanResizeTop = hasHeight,
                CanResizeBottom = hasHeight,
                HorizontalSizeUsesExtraParameter = extra.Width > primary.Width,
                VerticalSizeUsesExtraParameter = extra.Height > primary.Height
            };
        }
        _definitionCache[element.Index] = definition;
        return definition;
    }

    private (int Width, int Height) MeasureParameterEffect(LevelElement element, bool extra)
    {
        if (_rom is null || _document is null || _renderSnapshot is null) return (0, 0);
        var delta = SafeProbeDelta(_document, element, extra);
        if (delta == 0) return (0, 0);
        var candidate = extra
            ? _document.ResizeElement(element.Index, extraParameter: (element.ExtraParameter ?? 0) + delta)
            : _document.ResizeElement(element.Index, parameter: element.Parameter + delta);
        var rendered = _renderer.Render(_rom, candidate);
        if (!rendered.IsSuccess || !_renderSnapshot.ElementBounds.TryGetValue(element.Index, out var before) ||
            !rendered.Value!.ElementBounds.TryGetValue(element.Index, out var after)) return (0, 0);
        return (Math.Abs(after.Width - before.Width), Math.Abs(after.Height - before.Height));
    }

    private LevelDocument ResizeHorizontal(LevelDocument source, int index, GeneratorDefinition definition, int parameterDelta, int? left = null) =>
        definition.HorizontalSizeUsesExtraParameter
            ? ResizeHorizontalExtra(source, index, parameterDelta, left)
            : ResizeHorizontalShape(source, index, parameterDelta, left);

    private LevelDocument ResizeVertical(LevelDocument source, int index, GeneratorDefinition definition, int parameterDelta, int? top = null) =>
        definition.VerticalSizeUsesExtraParameter
            ? ResizeVerticalExtra(source, index, parameterDelta, top)
            : ResizeVerticalShape(source, index, parameterDelta, top);

    private static LevelDocument ResizeHorizontalShape(LevelDocument source, int index, int delta, int? left)
    {
        var element = source.Elements.FirstOrDefault(item => item.Index == index);
        if (element is null) return source;
        var parameter = GeneratorDefaults.ClampParameter(source.Tileset, element.GeneratorId, element.Parameter + delta);
        return parameter == element.Parameter
            ? source
            : source.ResizeElement(index, parameter: parameter, left: left);
    }

    private static LevelDocument ResizeHorizontalExtra(LevelDocument source, int index, int delta, int? left)
    {
        var element = source.Elements.FirstOrDefault(item => item.Index == index);
        if (element is null || element.ExtraParameter is not byte extra) return source;
        var value = GeneratorDefaults.ClampExtraParameter(source.Tileset, element.GeneratorId, extra + delta);
        return value == extra
            ? source
            : source.ResizeElement(index, extraParameter: value, left: left);
    }

    private static LevelDocument ResizeVerticalShape(LevelDocument source, int index, int delta, int? top)
    {
        var element = source.Elements.FirstOrDefault(item => item.Index == index);
        if (element is null) return source;
        var parameter = GeneratorDefaults.ClampParameter(source.Tileset, element.GeneratorId, element.Parameter + delta);
        return parameter == element.Parameter
            ? source
            : source.ResizeElement(index, parameter: parameter, top: top);
    }

    private static LevelDocument ResizeVerticalExtra(LevelDocument source, int index, int delta, int? top)
    {
        var element = source.Elements.FirstOrDefault(item => item.Index == index);
        if (element is null || element.ExtraParameter is not byte extra) return source;
        var value = GeneratorDefaults.ClampExtraParameter(source.Tileset, element.GeneratorId, extra + delta);
        return value == extra
            ? source
            : source.ResizeElement(index, extraParameter: value, top: top);
    }

    private LevelDocument ResizeCorner(LevelDocument source, int index, GeneratorDefinition definition,
        int horizontalDelta, int verticalDelta, int? left = null, int? top = null)
    {
        var horizontal = ResizeHorizontal(source, index, definition, horizontalDelta, left);
        return ResizeVertical(horizontal, index, definition, verticalDelta, top);
    }

    private enum DragOperation
    {
        None, Move, ResizeTop, ResizeRight, ResizeBottom, ResizeLeft,
        ResizeTopLeft, ResizeTopRight, ResizeBottomLeft, ResizeBottomRight
    }
}
