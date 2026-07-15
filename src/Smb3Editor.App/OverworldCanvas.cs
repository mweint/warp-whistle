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

public sealed class OverworldCanvas : Control
{
    public static readonly StyledProperty<OverworldRenderSnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<OverworldCanvas, OverworldRenderSnapshot?>(nameof(Snapshot));
    public static readonly StyledProperty<OverworldDocument?> WorldProperty =
        AvaloniaProperty.Register<OverworldCanvas, OverworldDocument?>(nameof(World));
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<OverworldCanvas, double>(nameof(Zoom), 1d);
    public static readonly StyledProperty<int> VisibleScreenProperty =
        AvaloniaProperty.Register<OverworldCanvas, int>(nameof(VisibleScreen), -1);
    public static readonly StyledProperty<bool> EditNodesProperty =
        AvaloniaProperty.Register<OverworldCanvas, bool>(nameof(EditNodes));
    public static readonly StyledProperty<bool> EditLocksProperty =
        AvaloniaProperty.Register<OverworldCanvas, bool>(nameof(EditLocks));
    private WriteableBitmap? _bitmap;
    public event EventHandler<OverworldLevelPointer>? LevelPointerSelected;
    public event EventHandler<IReadOnlyList<(int X, int Y)>>? TilePaintRequested;
    public event EventHandler? PaintStarted;
    public event EventHandler? PaintCompleted;
    public event EventHandler<OverworldZoomRequestedEventArgs>? ZoomRequested;
    public event EventHandler<OverworldPanRequestedEventArgs>? PanRequested;
    public event EventHandler? NodeMoveStarted;
    public event EventHandler<OverworldNodeMoveRequestEventArgs>? NodeMoveRequested;
    public event EventHandler? NodeMoveCompleted;
    public event EventHandler<OverworldLockBridge>? LockBridgeSelected;
    public byte PaintTile { get; set; }
    public OverworldRenderSnapshot? Snapshot { get => GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public OverworldDocument? World { get => GetValue(WorldProperty); set => SetValue(WorldProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    /// <summary>-1 shows the editor overview; otherwise renders one stock map screen.</summary>
    public int VisibleScreen { get => GetValue(VisibleScreenProperty); set => SetValue(VisibleScreenProperty, value); }
    public bool EditNodes { get => GetValue(EditNodesProperty); set => SetValue(EditNodesProperty, value); }
    public bool EditLocks { get => GetValue(EditLocksProperty); set => SetValue(EditLocksProperty, value); }
    private const double BaseScale = 2;
    // A logical gap, scaled together with the map. Keeping it in the same
    // coordinate system is required for stable mouse-anchored zoom.
    private const double ScreenGap = 10;
    private bool _painting;
    private Point? _lastPaintTile;
    private bool _panning;
    private Point _panPointer;
    private OverworldLevelPointer? _movingNode;
    private Point? _lastNodeTile;

    static OverworldCanvas()
    {
        SnapshotProperty.Changed.AddClassHandler<OverworldCanvas>((c, _) => c.Rebuild());
        WorldProperty.Changed.AddClassHandler<OverworldCanvas>((c, _) => c.Rebuild());
        ZoomProperty.Changed.AddClassHandler<OverworldCanvas>((c, _) => c.Rebuild());
        VisibleScreenProperty.Changed.AddClassHandler<OverworldCanvas>((c, _) => c.Rebuild());
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
            var gaps = World is { ScrollEnabled: false } world ? Math.Max(0, world.ScreenCount - 1) * ScreenGap * scale : 0;
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
        var separateScreens = World is { ScrollEnabled: false };
        var screenWidth = OverworldDocument.ScreenWidth * 16;
        var firstScreen = VisibleScreen >= 0 ? VisibleScreen : 0;
        var lastScreen = VisibleScreen >= 0 ? Math.Min(VisibleScreen + 1, World.ScreenCount) : World.ScreenCount;
        for (var screen = firstScreen; screen < lastScreen; screen++)
        {
            var destinationX = VisibleScreen >= 0 ? 0 : (screen * screenWidth * scale) + (separateScreens ? screen * ScreenGap * scale : 0);
            context.DrawImage(_bitmap, new Rect(screen * screenWidth, 0, screenWidth, snapshot.PixelHeight),
                new Rect(destinationX, 0, screenWidth * scale, snapshot.PixelHeight * scale));
        }
        foreach (var pointer in World.LevelPointers)
        {
            if (VisibleScreen >= 0 && pointer.Screen != VisibleScreen) continue;
            var x = VisibleScreen >= 0 ? pointer.Column * 16 * scale :
                (pointer.Screen * screenWidth * scale) + (separateScreens ? pointer.Screen * ScreenGap * scale : 0) + (pointer.Column * 16 * scale);
            var y = (pointer.Row - 2) * 16 * scale;
            context.DrawRectangle(null, new Pen(Brushes.White, 2), new Rect(x + 2, y + 2, (16 * scale) - 4, (16 * scale) - 4));
        }
        foreach (var item in World.LocksAndBridges)
        {
            if (VisibleScreen >= 0 && item.Screen != VisibleScreen) continue;
            var x = VisibleScreen >= 0 ? item.Column * 16 * scale :
                (item.Screen * screenWidth * scale) + (separateScreens ? item.Screen * ScreenGap * scale : 0) + (item.Column * 16 * scale);
            var y = (item.Row - OverworldDocument.FirstMapRow) * 16 * scale;
            context.DrawRectangle(null, new Pen(Brushes.Orange, 2), new Rect(x + 3, y + 3, (16 * scale) - 6, (16 * scale) - 6));
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
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !TryGetTile(e.GetPosition(this), out var column, out var row)) return;

        if (EditNodes)
        {
            var node = World.LevelPointers.FirstOrDefault(item => item.Screen * 16 + item.Column == column && item.Row == row + OverworldDocument.FirstMapRow);
            if (node is null) return;
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
            e.Handled = true;
            return;
        }

        var hit = World.LevelPointers.FirstOrDefault(item => item.Screen * 16 + item.Column == column && item.Row == row + 2);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && hit is not null)
        {
            LevelPointerSelected?.Invoke(this, hit);
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
        if (!_painting) return;
        _painting = false;
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
        var gap = World.ScrollEnabled ? 0 : ScreenGap * scale;
        var screen = VisibleScreen >= 0 ? VisibleScreen : Math.Clamp((int)(p.X / (screenWidth + gap)), 0, World.ScreenCount - 1);
        var localX = VisibleScreen >= 0 ? p.X : p.X - (screen * screenWidth) - (screen * gap);
        if (localX < 0 || localX >= screenWidth || p.Y < 0 || p.Y >= OverworldDocument.ScreenHeight * 16 * scale) return false;
        column = (screen * OverworldDocument.ScreenWidth) + (int)(localX / (16 * scale));
        row = (int)(p.Y / (16 * scale));
        return true;
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
