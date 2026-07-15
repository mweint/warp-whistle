using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Smb3Editor.Core;
using System.Runtime.InteropServices;

namespace Smb3Editor.App;

public sealed class OverworldZoomRequestedEventArgs(double oldZoom, double newZoom, Point pointer, Point logicalPoint) : EventArgs
{
    public double OldZoom { get; } = oldZoom;
    public double NewZoom { get; } = newZoom;
    public Point Pointer { get; } = pointer;
    public Point LogicalPoint { get; } = logicalPoint;
}

public sealed class OverworldPanRequestedEventArgs(Vector delta) : EventArgs
{
    public Vector Delta { get; } = delta;
}

public sealed class OverworldNodeMoveRequestEventArgs(OverworldLevelPointer node, int screen, int column, int row) : EventArgs
{
    public OverworldLevelPointer Node { get; } = node;
    public int Screen { get; } = screen;
    public int Column { get; } = column;
    public int Row { get; } = row;
    public bool Accepted { get; set; }
}

public sealed class OverworldLockMoveRequestEventArgs(OverworldLockBridge lockBridge, int screen, int column, int row) : EventArgs
{
    public OverworldLockBridge LockBridge { get; } = lockBridge;
    public int Screen { get; } = screen;
    public int Column { get; } = column;
    public int Row { get; } = row;
    public bool Accepted { get; set; }
}

public sealed class OverworldTileMoveRequestEventArgs(int sourceX, int sourceY, int destinationX, int destinationY) : EventArgs
{
    public int SourceX { get; } = sourceX;
    public int SourceY { get; } = sourceY;
    public int DestinationX { get; } = destinationX;
    public int DestinationY { get; } = destinationY;
    public bool Accepted { get; set; }
}

public sealed class OverworldTilePickedEventArgs(int x, int y) : EventArgs
{
    public int X { get; } = x;
    public int Y { get; } = y;
}

public sealed class OverworldNodePlacementRequestedEventArgs(int screen, int column, int row) : EventArgs
{
    public int Screen { get; } = screen;
    public int Column { get; } = column;
    public int Row { get; } = row;
}
public sealed class OverworldMapSpriteMoveRequestEventArgs(OverworldMapSprite sprite, int screen, int column, int row) : EventArgs
{
    public OverworldMapSprite Sprite { get; } = sprite;
    public int Screen { get; } = screen;
    public int Column { get; } = column;
    public int Row { get; } = row;
    public bool Accepted { get; set; }
}
public sealed class OverworldMapSpritePlacementRequestedEventArgs(int screen, int column, int row) : EventArgs
{
    public int Screen { get; } = screen;
    public int Column { get; } = column;
    public int Row { get; } = row;
}

public sealed class OverworldCanvas : Control
{
    public static readonly StyledProperty<OverworldRenderSnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<OverworldCanvas, OverworldRenderSnapshot?>(nameof(Snapshot));
    public static readonly StyledProperty<OverworldDocument?> WorldProperty =
        AvaloniaProperty.Register<OverworldCanvas, OverworldDocument?>(nameof(World));
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<OverworldCanvas, double>(nameof(Zoom), 1.25d);
    public static readonly StyledProperty<int> VisibleScreenProperty =
        AvaloniaProperty.Register<OverworldCanvas, int>(nameof(VisibleScreen), -1);
    public static readonly StyledProperty<bool> EditNodesProperty =
        AvaloniaProperty.Register<OverworldCanvas, bool>(nameof(EditNodes));
    public static readonly StyledProperty<bool> EditLocksProperty =
        AvaloniaProperty.Register<OverworldCanvas, bool>(nameof(EditLocks));
    public static readonly StyledProperty<bool> EditMapSpritesProperty =
        AvaloniaProperty.Register<OverworldCanvas, bool>(nameof(EditMapSprites));
    public static readonly StyledProperty<int> SelectedNodeIndexProperty =
        AvaloniaProperty.Register<OverworldCanvas, int>(nameof(SelectedNodeIndex), -1);
    public static readonly StyledProperty<bool> EditTileMovesProperty =
        AvaloniaProperty.Register<OverworldCanvas, bool>(nameof(EditTileMoves));
    public static readonly StyledProperty<bool> EditTerrainPathsProperty =
        AvaloniaProperty.Register<OverworldCanvas, bool>(nameof(EditTerrainPaths));
    public static readonly StyledProperty<bool> EditTerrainWaterProperty =
        AvaloniaProperty.Register<OverworldCanvas, bool>(nameof(EditTerrainWater));
    public static readonly StyledProperty<bool> ShowNodesProperty =
        AvaloniaProperty.Register<OverworldCanvas, bool>(nameof(ShowNodes), true);
    public static readonly StyledProperty<bool> ShowLocksProperty =
        AvaloniaProperty.Register<OverworldCanvas, bool>(nameof(ShowLocks), true);
    public static readonly StyledProperty<bool> ShowMapSpritesProperty =
        AvaloniaProperty.Register<OverworldCanvas, bool>(nameof(ShowMapSprites), true);
    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<OverworldCanvas, bool>(nameof(ShowGrid), true);
    private WriteableBitmap? _bitmap;
    private readonly Dictionary<byte, WriteableBitmap> _mapSpriteBitmaps = [];
    public event EventHandler<OverworldLevelPointer>? LevelPointerSelected;
    public event EventHandler<IReadOnlyList<(int X, int Y)>>? TilePaintRequested;
    public event EventHandler? PaintStarted;
    public event EventHandler? PaintCompleted;
    public event EventHandler<OverworldZoomRequestedEventArgs>? ZoomRequested;
    public event EventHandler<OverworldPanRequestedEventArgs>? PanRequested;
    public event EventHandler? NodeMoveStarted;
    public event EventHandler<OverworldNodeMoveRequestEventArgs>? NodeMoveRequested;
    public event EventHandler? NodeMoveCompleted;
    public event EventHandler<OverworldNodePlacementRequestedEventArgs>? NodePlacementRequested;
    public event EventHandler? LockMoveStarted;
    public event EventHandler<OverworldLockMoveRequestEventArgs>? LockMoveRequested;
    public event EventHandler? LockMoveCompleted;
    public event EventHandler<OverworldTileMoveRequestEventArgs>? TileMoveRequested;
    public event EventHandler<OverworldTilePickedEventArgs>? TilePicked;
    public event EventHandler<OverworldLockBridge>? LockBridgeSelected;
    public event EventHandler<OverworldMapSprite>? MapSpriteSelected;
    public event EventHandler? MapSpriteMoveStarted;
    public event EventHandler<OverworldMapSpriteMoveRequestEventArgs>? MapSpriteMoveRequested;
    public event EventHandler? MapSpriteMoveCompleted;
    public event EventHandler<OverworldMapSpritePlacementRequestedEventArgs>? MapSpritePlacementRequested;
    public byte PaintTile { get; set; }
    public bool IsErasingTerrainStroke => _terrainErasing;
    public IReadOnlyDictionary<byte, CatalogPreviewData> MapSpritePreviews
    {
        get => _mapSpritePreviews;
        set { _mapSpritePreviews = value; _mapSpriteBitmaps.Clear(); InvalidateVisual(); }
    }
    private IReadOnlyDictionary<byte, CatalogPreviewData> _mapSpritePreviews = new Dictionary<byte, CatalogPreviewData>();
    public OverworldRenderSnapshot? Snapshot { get => GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public OverworldDocument? World { get => GetValue(WorldProperty); set => SetValue(WorldProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    /// <summary>-1 shows the editor overview; otherwise renders one stock map screen.</summary>
    public int VisibleScreen { get => GetValue(VisibleScreenProperty); set => SetValue(VisibleScreenProperty, value); }
    public bool EditNodes { get => GetValue(EditNodesProperty); set => SetValue(EditNodesProperty, value); }
    public bool EditLocks { get => GetValue(EditLocksProperty); set => SetValue(EditLocksProperty, value); }
    public bool EditMapSprites { get => GetValue(EditMapSpritesProperty); set => SetValue(EditMapSpritesProperty, value); }
    public int SelectedNodeIndex { get => GetValue(SelectedNodeIndexProperty); set => SetValue(SelectedNodeIndexProperty, value); }
    public bool EditTileMoves { get => GetValue(EditTileMovesProperty); set => SetValue(EditTileMovesProperty, value); }
    public bool EditTerrainPaths { get => GetValue(EditTerrainPathsProperty); set => SetValue(EditTerrainPathsProperty, value); }
    public bool EditTerrainWater { get => GetValue(EditTerrainWaterProperty); set => SetValue(EditTerrainWaterProperty, value); }
    public bool ShowNodes { get => GetValue(ShowNodesProperty); set => SetValue(ShowNodesProperty, value); }
    public bool ShowLocks { get => GetValue(ShowLocksProperty); set => SetValue(ShowLocksProperty, value); }
    public bool ShowMapSprites { get => GetValue(ShowMapSpritesProperty); set => SetValue(ShowMapSpritesProperty, value); }
    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    private IReadOnlySet<int> _invalidNodeIndices = new HashSet<int>();
    /// <summary>Node indices that cannot be exported to the original game's map format.</summary>
    public IReadOnlySet<int> InvalidNodeIndices
    {
        get => _invalidNodeIndices;
        set { _invalidNodeIndices = value; InvalidateVisual(); }
    }
    private const double BaseScale = 2;
    // A logical gap, scaled together with the map. Keeping it in the same
    // coordinate system is required for stable mouse-anchored zoom.
    private const double ScreenGap = 10;
    private bool _painting;
    private bool _terrainErasing;
    private Point? _lastPaintTile;
    private bool _panning;
    private Point _panPointer;
    private OverworldLevelPointer? _movingNode;
    private Point? _lastNodeTile;
    private OverworldLockBridge? _movingLock;
    private Point? _lastLockTile;
    private OverworldMapSprite? _movingMapSprite;
    private Point? _lastMapSpriteTile;
    private Point? _movingTile;
    private Point? _tileMoveTarget;

    static OverworldCanvas()
    {
        SnapshotProperty.Changed.AddClassHandler<OverworldCanvas>((c, _) => c.Rebuild());
        WorldProperty.Changed.AddClassHandler<OverworldCanvas>((c, _) => c.Rebuild());
        ZoomProperty.Changed.AddClassHandler<OverworldCanvas>((c, _) => c.Rebuild());
        VisibleScreenProperty.Changed.AddClassHandler<OverworldCanvas>((c, _) => c.Rebuild());
        SelectedNodeIndexProperty.Changed.AddClassHandler<OverworldCanvas>((c, _) => c.InvalidateVisual());
        EditTileMovesProperty.Changed.AddClassHandler<OverworldCanvas>((c, _) => { if (!c.EditTileMoves) { c._movingTile = null; c._tileMoveTarget = null; } c.InvalidateVisual(); });
        ShowNodesProperty.Changed.AddClassHandler<OverworldCanvas>((c, _) => c.InvalidateVisual());
        ShowLocksProperty.Changed.AddClassHandler<OverworldCanvas>((c, _) => c.InvalidateVisual());
        ShowMapSpritesProperty.Changed.AddClassHandler<OverworldCanvas>((c, _) => c.InvalidateVisual());
        ShowGridProperty.Changed.AddClassHandler<OverworldCanvas>((c, _) => c.InvalidateVisual());
    }
    public OverworldCanvas() => RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    private void Rebuild()
    {
        var snapshot = Snapshot;
        if (snapshot is null) { _bitmap = null; Width = Height = 0; InvalidateVisual(); return; }
        _bitmap = new WriteableBitmap(new PixelSize(snapshot.PixelWidth, snapshot.PixelHeight), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = _bitmap.Lock();
        var data = snapshot.ArgbPixels.Select(static p => unchecked((int)p)).ToArray();
        for (var y = 0; y < snapshot.PixelHeight; y++) Marshal.Copy(data, y * snapshot.PixelWidth, IntPtr.Add(fb.Address, y * fb.RowBytes), snapshot.PixelWidth);
        var scale = BaseScale * Zoom;
        if (VisibleScreen >= 0) Width = OverworldDocument.ScreenWidth * 16 * scale;
        else
        {
            var gaps = World is { } world ? Math.Max(0, world.ScreenCount - 1) * ScreenGap * scale : 0;
            Width = snapshot.PixelWidth * scale + gaps;
        }
        Height = snapshot.PixelHeight * scale; InvalidateVisual();
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var snapshot = Snapshot; if (snapshot is null || _bitmap is null) return;
        if (World is null) return;
        var scale = BaseScale * Zoom;
        var separateScreens = VisibleScreen < 0 && World.ScreenCount > 1;
        var screenWidth = OverworldDocument.ScreenWidth * 16;
        var firstScreen = VisibleScreen >= 0 ? VisibleScreen : 0;
        var lastScreen = VisibleScreen >= 0 ? Math.Min(VisibleScreen + 1, World.ScreenCount) : World.ScreenCount;
        for (var screen = firstScreen; screen < lastScreen; screen++)
        {
            var destinationX = VisibleScreen >= 0 ? 0 : (screen * screenWidth * scale) + (separateScreens ? screen * ScreenGap * scale : 0);
            context.DrawImage(_bitmap, new Rect(screen * screenWidth, 0, screenWidth, snapshot.PixelHeight),
                new Rect(destinationX, 0, screenWidth * scale, snapshot.PixelHeight * scale));
            if (ShowGrid)
            {
                var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(48, 29, 86, 110)), 1);
                for (var column = 0; column <= OverworldDocument.ScreenWidth; column++)
                {
                    var x = destinationX + (column * 16 * scale);
                    context.DrawLine(gridPen, new Point(x, 0), new Point(x, snapshot.PixelHeight * scale));
                }
                for (var row = 0; row <= OverworldDocument.ScreenHeight; row++)
                {
                    var y = row * 16 * scale;
                    context.DrawLine(gridPen, new Point(destinationX, y), new Point(destinationX + screenWidth * scale, y));
                }
            }
        }
        if (ShowNodes) foreach (var pointer in World.LevelPointers)
        {
            var invalid = InvalidNodeIndices.Contains(pointer.Index);
            // Keep an invalid imported node visible at the nearest map edge so it can
            // be identified even when its stored coordinates are outside the map.
            var screen = Math.Clamp(pointer.Screen, 0, Math.Max(0, World.ScreenCount - 1));
            var column = Math.Clamp(pointer.Column, 0, OverworldDocument.ScreenWidth - 1);
            var row = Math.Clamp(pointer.Row, OverworldDocument.FirstMapRow, OverworldDocument.FirstMapRow + OverworldDocument.ScreenHeight - 1);
            if (VisibleScreen >= 0 && screen != VisibleScreen) continue;
            var x = VisibleScreen >= 0 ? column * 16 * scale :
                (screen * screenWidth * scale) + (separateScreens ? screen * ScreenGap * scale : 0) + (column * 16 * scale);
            var y = (row - 2) * 16 * scale;
            IBrush brush = invalid ? Brushes.IndianRed : pointer.Index == SelectedNodeIndex
                ? new SolidColorBrush(Color.Parse("#2F6BFF"))
                : new SolidColorBrush(Color.Parse("#34D5FF"));
            context.DrawRectangle(null, new Pen(brush, 2), new Rect(x + 2, y + 2, (16 * scale) - 4, (16 * scale) - 4));
        }
        if (ShowLocks) foreach (var item in World.LocksAndBridges)
        {
            if (VisibleScreen >= 0 && item.Screen != VisibleScreen) continue;
            var x = VisibleScreen >= 0 ? item.Column * 16 * scale :
                (item.Screen * screenWidth * scale) + (separateScreens ? item.Screen * ScreenGap * scale : 0) + (item.Column * 16 * scale);
            var y = (item.Row - OverworldDocument.FirstMapRow) * 16 * scale;
            context.DrawRectangle(null, new Pen(Brushes.Orange, 2), new Rect(x + 3, y + 3, (16 * scale) - 6, (16 * scale) - 6));
        }
        if (ShowMapSprites) foreach (var sprite in World.MapSprites.Where(static item => !item.IsEmpty))
        {
            if (VisibleScreen >= 0 && sprite.Screen != VisibleScreen) continue;
            var x = VisibleScreen >= 0 ? sprite.Column * 16 * scale :
                (sprite.Screen * screenWidth * scale) + (separateScreens ? sprite.Screen * ScreenGap * scale : 0) + (sprite.Column * 16 * scale);
            var y = (sprite.Row - OverworldDocument.FirstMapRow) * 16 * scale;
            if (_mapSpritePreviews.TryGetValue(sprite.Type, out var preview))
                context.DrawImage(GetMapSpriteBitmap(sprite.Type, preview), new Rect(0, 0, preview.Width, preview.Height), new Rect(x, y, 16 * scale, 16 * scale));
            context.DrawRectangle(null, new Pen(Brushes.MediumPurple, 2), new Rect(x + 2, y + 2, (16 * scale) - 4, (16 * scale) - 4));
        }
        if (_movingTile is { } selected)
        {
            var screen = (int)selected.X / OverworldDocument.ScreenWidth;
            if (VisibleScreen < 0 || screen == VisibleScreen)
            {
                var column = (int)selected.X % OverworldDocument.ScreenWidth;
                var x = VisibleScreen >= 0 ? column * 16 * scale :
                    (screen * screenWidth * scale) + (separateScreens ? screen * ScreenGap * scale : 0) + (column * 16 * scale);
                var y = selected.Y * 16 * scale;
                var sourceBounds = new Rect(x, y, 16 * scale, 16 * scale);
                // The source remains visible while moving, but is deliberately
                // muted to communicate that it will become the empty tile on drop.
                context.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 17, 25, 35)), null, sourceBounds);
                context.DrawRectangle(null, new Pen(Brushes.Gray, 2), new Rect(x + 2, y + 2, (16 * scale) - 4, (16 * scale) - 4));
            }
        }
        if (_tileMoveTarget is { } target && target != _movingTile)
        {
            var screen = (int)target.X / OverworldDocument.ScreenWidth;
            if (VisibleScreen < 0 || screen == VisibleScreen)
            {
                var column = (int)target.X % OverworldDocument.ScreenWidth;
                var x = VisibleScreen >= 0 ? column * 16 * scale :
                    (screen * screenWidth * scale) + (separateScreens ? screen * ScreenGap * scale : 0) + (column * 16 * scale);
                var y = target.Y * 16 * scale;
                if (_movingTile is { } source)
                {
                    var sourceScreen = (int)source.X / OverworldDocument.ScreenWidth;
                    var sourceColumn = (int)source.X % OverworldDocument.ScreenWidth;
                    context.DrawImage(_bitmap,
                        new Rect((sourceScreen * screenWidth) + (sourceColumn * 16), source.Y * 16, 16, 16),
                        new Rect(x, y, 16 * scale, 16 * scale));
                }
                context.DrawRectangle(null, new Pen(Brushes.Orange, 2), new Rect(x + 2, y + 2, (16 * scale) - 4, (16 * scale) - 4));
            }
        }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (World is null) return;
        Focus();
        if (e.GetCurrentPoint(this).Properties.IsMiddleButtonPressed)
        {
            _panning = true;
            _panPointer = e.GetPosition(null);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed && TryGetTile(e.GetPosition(this), out var paintColumn, out var paintRow))
        {
            if (EditMapSprites)
            {
                MapSpritePlacementRequested?.Invoke(this, new OverworldMapSpritePlacementRequestedEventArgs(
                    paintColumn / OverworldDocument.ScreenWidth, paintColumn % OverworldDocument.ScreenWidth, paintRow + OverworldDocument.FirstMapRow));
                e.Handled = true;
                return;
            }
            _terrainErasing = EditTerrainPaths || EditTerrainWater;
            _painting = true;
            _lastPaintTile = null;
            PaintStarted?.Invoke(this, EventArgs.Empty);
            PaintAt(paintColumn, paintRow);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !TryGetTile(e.GetPosition(this), out var column, out var row)) return;

        if (EditTerrainPaths || EditTerrainWater)
        {
            // Terrain brushes support either button. Unlike ordinary tiles,
            // they derive their stock variants from the continuous stroke.
            _terrainErasing = false;
            _painting = true;
            _lastPaintTile = null;
            PaintStarted?.Invoke(this, EventArgs.Empty);
            PaintAt(column, row);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (EditTileMoves)
        {
            TilePicked?.Invoke(this, new OverworldTilePickedEventArgs(column, row));
            _movingTile = new Point(column, row);
            _tileMoveTarget = _movingTile;
            InvalidateVisual();
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (EditNodes)
        {
            var node = World.LevelPointers.FirstOrDefault(item => item.Screen * 16 + item.Column == column && item.Row == row + OverworldDocument.FirstMapRow);
            if (node is null)
            {
                NodePlacementRequested?.Invoke(this, new OverworldNodePlacementRequestedEventArgs(
                    column / OverworldDocument.ScreenWidth, column % OverworldDocument.ScreenWidth, row + OverworldDocument.FirstMapRow));
                e.Handled = true;
                return;
            }
            _movingNode = node;
            _lastNodeTile = new Point(column, row);
            LevelPointerSelected?.Invoke(this, node);
            NodeMoveStarted?.Invoke(this, EventArgs.Empty);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (EditLocks)
        {
            var item = World.LocksAndBridges.FirstOrDefault(lockBridge => lockBridge.Screen * 16 + lockBridge.Column == column && lockBridge.Row == row + OverworldDocument.FirstMapRow);
            if (item is null) return;
            LockBridgeSelected?.Invoke(this, item);
            _movingLock = item;
            _lastLockTile = new Point(column, row);
            LockMoveStarted?.Invoke(this, EventArgs.Empty);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }
        if (EditMapSprites)
        {
            var sprite = World.MapSprites.FirstOrDefault(item => !item.IsEmpty && item.Screen * 16 + item.Column == column && item.Row == row + OverworldDocument.FirstMapRow);
            if (sprite is null) return;
            _movingMapSprite = sprite;
            _lastMapSpriteTile = new Point(column, row);
            MapSpriteSelected?.Invoke(this, sprite);
            MapSpriteMoveStarted?.Invoke(this, EventArgs.Empty);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        _painting = true;
        _lastPaintTile = null;
        PaintStarted?.Invoke(this, EventArgs.Empty);
        PaintAt(column, row);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_panning)
        {
            var pointer = e.GetPosition(null);
            var delta = pointer - _panPointer;
            _panPointer = pointer;
            PanRequested?.Invoke(this, new OverworldPanRequestedEventArgs(delta));
            e.Handled = true;
            return;
        }
        if (_movingNode is not null && TryGetTile(e.GetPosition(this), out var nodeColumn, out var nodeRow))
        {
            var target = new Point(nodeColumn, nodeRow);
            if (_lastNodeTile == target) return;
            var proposed = _movingNode with { Screen = nodeColumn / OverworldDocument.ScreenWidth, Column = nodeColumn % OverworldDocument.ScreenWidth, Row = nodeRow + OverworldDocument.FirstMapRow };
            var request = new OverworldNodeMoveRequestEventArgs(proposed, proposed.Screen, proposed.Column, proposed.Row);
            NodeMoveRequested?.Invoke(this, request);
            if (request.Accepted)
            {
                _movingNode = proposed;
                _lastNodeTile = target;
            }
            e.Handled = true;
            return;
        }
        if (_movingLock is not null && TryGetTile(e.GetPosition(this), out var lockColumn, out var lockRow))
        {
            var target = new Point(lockColumn, lockRow);
            if (_lastLockTile == target) return;
            var proposed = _movingLock with { Screen = lockColumn / OverworldDocument.ScreenWidth, Column = lockColumn % OverworldDocument.ScreenWidth, Row = lockRow + OverworldDocument.FirstMapRow };
            var request = new OverworldLockMoveRequestEventArgs(proposed, proposed.Screen, proposed.Column, proposed.Row);
            LockMoveRequested?.Invoke(this, request);
            if (request.Accepted)
            {
                _movingLock = proposed;
                _lastLockTile = target;
            }
            e.Handled = true;
            return;
        }
        if (_movingMapSprite is not null && TryGetTile(e.GetPosition(this), out var spriteColumn, out var spriteRow))
        {
            var target = new Point(spriteColumn, spriteRow);
            if (_lastMapSpriteTile == target) return;
            var proposed = _movingMapSprite with { Screen = spriteColumn / OverworldDocument.ScreenWidth, Column = spriteColumn % OverworldDocument.ScreenWidth, Row = spriteRow + OverworldDocument.FirstMapRow };
            var request = new OverworldMapSpriteMoveRequestEventArgs(proposed, proposed.Screen, proposed.Column, proposed.Row);
            MapSpriteMoveRequested?.Invoke(this, request);
            if (request.Accepted) { _movingMapSprite = proposed; _lastMapSpriteTile = target; }
            e.Handled = true;
            return;
        }
        if (_movingTile is not null && TryGetTile(e.GetPosition(this), out var moveColumn, out var moveRow))
        {
            _tileMoveTarget = new Point(moveColumn, moveRow);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (!_painting || !TryGetTile(e.GetPosition(this), out var column, out var row)) return;
        PaintAt(column, row);
        e.Handled = true;
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
        if (_movingNode is not null)
        {
            _movingNode = null;
            _lastNodeTile = null;
            NodeMoveCompleted?.Invoke(this, EventArgs.Empty);
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (_movingLock is not null)
        {
            _movingLock = null;
            _lastLockTile = null;
            LockMoveCompleted?.Invoke(this, EventArgs.Empty);
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (_movingMapSprite is not null)
        {
            _movingMapSprite = null;
            _lastMapSpriteTile = null;
            MapSpriteMoveCompleted?.Invoke(this, EventArgs.Empty);
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (_movingTile is { } source)
        {
            if (_tileMoveTarget is { } target && target != source)
            {
                var request = new OverworldTileMoveRequestEventArgs((int)source.X, (int)source.Y, (int)target.X, (int)target.Y);
                TileMoveRequested?.Invoke(this, request);
            }
            _movingTile = null;
            _tileMoveTarget = null;
            InvalidateVisual();
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (!_painting) return;
        _painting = false;
        _terrainErasing = false;
        _lastPaintTile = null;
        PaintCompleted?.Invoke(this, EventArgs.Empty);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) { base.OnPointerWheelChanged(e); return; }
        var oldZoom = Zoom;
        var newZoom = Math.Clamp(Math.Round(oldZoom + (e.Delta.Y > 0 ? 0.1 : -0.1), 2), 0.25, 8);
        if (Math.Abs(newZoom - oldZoom) < double.Epsilon) return;
        var pointer = e.GetPosition(this);
        ZoomRequested?.Invoke(this, new OverworldZoomRequestedEventArgs(oldZoom, newZoom, pointer, new Point(pointer.X / oldZoom, pointer.Y / oldZoom)));
        e.Handled = true;
    }

    private bool TryGetTile(Point p, out int column, out int row)
    {
        column = row = 0;
        if (World is null) return false;
        var scale = BaseScale * Zoom;
        var screenWidth = OverworldDocument.ScreenWidth * 16 * scale;
        var gap = VisibleScreen >= 0 ? 0 : ScreenGap * scale;
        var screen = VisibleScreen >= 0 ? VisibleScreen : Math.Clamp((int)(p.X / (screenWidth + gap)), 0, World.ScreenCount - 1);
        var localX = VisibleScreen >= 0 ? p.X : p.X - (screen * screenWidth) - (screen * gap);
        if (localX < 0 || localX >= screenWidth || p.Y < 0 || p.Y >= OverworldDocument.ScreenHeight * 16 * scale) return false;
        column = (screen * OverworldDocument.ScreenWidth) + (int)(localX / (16 * scale));
        row = (int)(p.Y / (16 * scale));
        return true;
    }

    private WriteableBitmap GetMapSpriteBitmap(byte type, CatalogPreviewData preview)
    {
        if (_mapSpriteBitmaps.TryGetValue(type, out var bitmap)) return bitmap;
        bitmap = new WriteableBitmap(new PixelSize(preview.Width, preview.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = bitmap.Lock();
        var data = preview.Pixels.Select(static p => unchecked((int)p)).ToArray();
        for (var y = 0; y < preview.Height; y++) Marshal.Copy(data, y * preview.Width, IntPtr.Add(fb.Address, y * fb.RowBytes), preview.Width);
        _mapSpriteBitmaps[type] = bitmap;
        return bitmap;
    }

    private void PaintAt(int column, int row)
    {
        var tile = new Point(column, row);
        if (_lastPaintTile == tile) return;

        var points = new List<(int X, int Y)>();
        var startX = _lastPaintTile is { } previous ? (int)previous.X : column;
        var startY = _lastPaintTile is { } previousPoint ? (int)previousPoint.Y : row;
        var deltaX = Math.Abs(column - startX);
        var stepX = startX < column ? 1 : -1;
        var deltaY = -Math.Abs(row - startY);
        var stepY = startY < row ? 1 : -1;
        var error = deltaX + deltaY;
        while (true)
        {
            points.Add((startX, startY));
            if (startX == column && startY == row) break;
            var twiceError = error * 2;
            if (twiceError >= deltaY) { error += deltaY; startX += stepX; }
            if (twiceError <= deltaX) { error += deltaX; startY += stepY; }
        }

        _lastPaintTile = tile;
        TilePaintRequested?.Invoke(this, points);
    }
}
