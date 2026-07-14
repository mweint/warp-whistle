using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Smb3Editor.App;

internal enum UnsavedChangesChoice { Cancel, Save, Discard }

internal sealed class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog(string action)
    {
        Title = "Unsaved changes";
        Width = 390;
        Height = 160;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var save = new Button { Content = "💾", MinWidth = 42 };
        ToolTip.SetTip(save, "Save changes");
        var discard = new Button { Content = "Don't Save", MinWidth = 96 };
        var cancel = new Button { Content = "Cancel", MinWidth = 82 };
        save.Click += (_, _) => Close(UnsavedChangesChoice.Save);
        discard.Click += (_, _) => Close(UnsavedChangesChoice.Discard);
        cancel.Click += (_, _) => Close(UnsavedChangesChoice.Cancel);

        Content = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children =
            {
                new TextBlock { Text = "Save changes?", FontSize = 18, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = $"You have unsaved changes. Save before {action}?", TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, [Grid.RowProperty] = 1 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, discard, save }, [Grid.RowProperty] = 2 }
            }
        };
    }
}
