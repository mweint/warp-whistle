using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Smb3Editor.App;

public sealed record CatalogPreviewData(int Width, int Height, IReadOnlyList<uint> Pixels);

public sealed class CatalogPreview : Control
{
    public static readonly StyledProperty<CatalogPreviewData?> PreviewProperty =
        AvaloniaProperty.Register<CatalogPreview, CatalogPreviewData?>(nameof(Preview));

    public CatalogPreviewData? Preview
    {
        get => GetValue(PreviewProperty);
        set => SetValue(PreviewProperty, value);
    }

    private static readonly ConditionalWeakTable<CatalogPreviewData, WriteableBitmap> SharedBitmaps = [];

    static CatalogPreview() => PreviewProperty.Changed.AddClassHandler<CatalogPreview>((control, _) =>
    {
        control.InvalidateVisual();
    });

    public CatalogPreview()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var preview = Preview;
        if (preview is null || preview.Width <= 0 || preview.Height <= 0) return;
        var bitmap = SharedBitmaps.GetValue(preview, CreateBitmap);
        var scale = Math.Min(Bounds.Width / preview.Width, Bounds.Height / preview.Height);
        var left = (Bounds.Width - preview.Width * scale) / 2;
        var top = (Bounds.Height - preview.Height * scale) / 2;
        context.DrawImage(bitmap, new Rect(0, 0, preview.Width, preview.Height),
            new Rect(left, top, preview.Width * scale, preview.Height * scale));
    }

    private static WriteableBitmap CreateBitmap(CatalogPreviewData preview)
    {
        var bitmap = new WriteableBitmap(new PixelSize(preview.Width, preview.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        var pixels = preview.Pixels.Select(static pixel => unchecked((int)pixel)).ToArray();
        using var framebuffer = bitmap.Lock();
        for (var row = 0; row < preview.Height; row++)
            Marshal.Copy(pixels, row * preview.Width, IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), preview.Width);
        return bitmap;
    }
}
