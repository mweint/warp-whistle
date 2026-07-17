using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Smb3Editor.Core;

namespace Smb3Editor.App;

/// <summary>Project-wide output and allocation settings. This dialog saves only
/// its settings; it never commits pending level edits.</summary>
internal sealed class ProjectSettingsWindow : Window
{
    private readonly CheckBox _enhanced;
    private readonly CheckBox _managedStorage;
    private readonly TextBlock _storageWarning;
    private readonly Func<RomStorageMode, string?> _fixedSlotWarning;
    private readonly Func<RomOutputMode, RomStorageMode, Task<bool>> _save;

    public ProjectSettingsWindow(
        RomOutputMode outputMode,
        RomStorageMode storageMode,
        bool enhancedAvailable,
        string enhancedUnavailableReason,
        Func<RomStorageMode, string?> fixedSlotWarning,
        Func<RomOutputMode, RomStorageMode, Task<bool>> saveSettings)
    {
        _fixedSlotWarning = fixedSlotWarning;
        _save = saveSettings;
        Title = "Project settings";
        Width = 520;
        Height = 300;
        MinWidth = 480;
        MinHeight = 280;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock
        {
            Text = "These are project settings. Saving here does not save or discard pending level edits.",
            FontSize = 12,
            Foreground = Brushes.LightSteelBlue,
            TextWrapping = TextWrapping.Wrap
        });

        _managedStorage = new CheckBox
        {
            Content = "Managed vanilla storage",
            IsChecked = storageMode == RomStorageMode.ManagedVanilla,
            Margin = new Thickness(0, 6, 0, 0)
        };
        _managedStorage.IsCheckedChanged += (_, _) => RefreshStorageWarning();
        body.Children.Add(_managedStorage);
        body.Children.Add(new TextBlock
        {
            Text = "Shares verified space within the original PRG banks without expanding the ROM.",
            FontSize = 11,
            Foreground = Brushes.SlateGray,
            TextWrapping = TextWrapping.Wrap
        });
        _storageWarning = new TextBlock
        {
            Foreground = Brushes.Gold,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };
        body.Children.Add(_storageWarning);

        _enhanced = new CheckBox
        {
            Content = "Enhanced MMC3 output",
            IsChecked = outputMode == RomOutputMode.EnhancedMmc3,
            IsEnabled = enhancedAvailable,
            Margin = new Thickness(0, 6, 0, 0)
        };
        var enhancedRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        enhancedRow.Children.Add(_enhanced);
        if (!enhancedAvailable)
        {
            var help = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = Brushes.Goldenrod,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "!", FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            ToolTip.SetTip(help, enhancedUnavailableReason);
            enhancedRow.Children.Add(help);
        }
        body.Children.Add(enhancedRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();
        var saveButton = new Button { Content = "\uD83D\uDCBE", Width = 34 };
        ToolTip.SetTip(saveButton, "Save project settings without saving pending level edits");
        saveButton.Click += Save_Click;
        buttons.Children.Add(cancel);
        buttons.Children.Add(saveButton);
        Grid.SetRow(buttons, 1);
        Content = new Grid
        {
            Margin = new Thickness(18),
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children =
            {
                new ScrollViewer
                {
                    Content = body,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                },
                buttons
            }
        };
        RefreshStorageWarning();
    }

    private void RefreshStorageWarning()
    {
        var warning = _managedStorage.IsChecked == true ? null : _fixedSlotWarning(RomStorageMode.FixedSlots);
        _storageWarning.Text = warning ?? string.Empty;
        _storageWarning.IsVisible = !string.IsNullOrWhiteSpace(warning);
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var mode = _managedStorage.IsChecked == true ? RomStorageMode.ManagedVanilla : RomStorageMode.FixedSlots;
        var warning = mode == RomStorageMode.FixedSlots ? _fixedSlotWarning(mode) : null;
        if (!string.IsNullOrWhiteSpace(warning) &&
            !await new ProjectSettingsConfirmationDialog("Use fixed slots?", warning, "Use fixed slots").ShowDialog<bool>(this))
        {
            return;
        }

        var saved = await _save(_enhanced.IsChecked == true ? RomOutputMode.EnhancedMmc3 : RomOutputMode.Vanilla, mode);
        if (saved) Close();
    }
}

internal sealed class ProjectSettingsConfirmationDialog : Window
{
    public ProjectSettingsConfirmationDialog(string title, string message, string confirm)
    {
        Title = title;
        Width = 470;
        Height = 190;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new Button { Content = "Cancel", MinWidth = 82 };
        cancel.Click += (_, _) => Close(false);
        var accept = new Button { Content = confirm, MinWidth = 108 };
        accept.Click += (_, _) => Close(true);
        Content = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Children =
            {
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, [Grid.RowProperty] = 1 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, accept }, [Grid.RowProperty] = 2 }
            }
        };
    }
}
