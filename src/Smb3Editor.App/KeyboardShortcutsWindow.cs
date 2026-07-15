using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Smb3Editor.App;

internal sealed class KeyboardShortcutsWindow : Window
{
    public KeyboardShortcutsWindow()
    {
        Title = "Keyboard shortcuts";
        Width = 430;
        Height = 360;
        MinWidth = 360;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var content = new StackPanel { Spacing = 8, Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = "Keyboard shortcuts", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold });
        content.Children.Add(new TextBlock { Text = "Level editor and overworld", Foreground = Avalonia.Media.Brushes.LightSteelBlue });
        foreach (var (keys, action) in new[]
        {
            ("Ctrl + S", "Save project"), ("Ctrl + Z / Ctrl + Y", "Undo / redo"),
            ("Ctrl + C / Ctrl + V", "Copy / paste"), ("Delete / Backspace", "Delete selection"),
            ("Ctrl + mouse wheel", "Zoom at pointer"), ("Middle mouse drag", "Pan canvas"),
            ("Right-click", "Place selected item or map tile")
        })
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
            row.Children.Add(new TextBlock { Text = keys, FontWeight = Avalonia.Media.FontWeight.SemiBold });
            var description = new TextBlock { Text = action };
            Grid.SetColumn(description, 1);
            row.Children.Add(description);
            content.Children.Add(row);
        }
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        close.Click += (_, _) => Close();
        content.Children.Add(close);
        Content = content;
    }
}
