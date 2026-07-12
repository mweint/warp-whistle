using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Smb3Editor.Core;

namespace Smb3Editor.App;

/// <summary>Shared compact, gap-free palette preview used by every palette selector.</summary>
public sealed class PalettePreview : Border
{
    public static readonly StyledProperty<IEnumerable<IBrush>?> ColorsProperty =
        AvaloniaProperty.Register<PalettePreview, IEnumerable<IBrush>?>(nameof(Colors));

    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 0 };

    public IEnumerable<IBrush>? Colors
    {
        get => GetValue(ColorsProperty);
        set => SetValue(ColorsProperty, value);
    }

    static PalettePreview()
    {
        ColorsProperty.Changed.AddClassHandler<PalettePreview>((preview, args) =>
            preview.Refresh(args.NewValue as IEnumerable<IBrush>));
    }

    public PalettePreview()
    {
        Width = 162;
        Height = 20;
        HorizontalAlignment = HorizontalAlignment.Left;
        BorderBrush = new SolidColorBrush(Color.Parse("#718399"));
        BorderThickness = new Thickness(1);
        Child = _chips;
    }

    public static IReadOnlyList<IBrush> FromNesColors(IEnumerable<byte> colors) =>
        colors.Take(16).Select(value => (IBrush)new SolidColorBrush(Color.FromUInt32(NesPalette.Argb[value & 0x3F]))).ToArray();

    private void Refresh(IEnumerable<IBrush>? colors)
    {
        _chips.Children.Clear();
        foreach (var color in colors?.Take(16) ?? [])
        {
            _chips.Children.Add(new Border { Width = 10, Height = 18, Background = color });
        }
    }
}
