using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Smb3Editor.Core;

namespace Smb3Editor.App;

public sealed partial class PatchManagerWindow : Window
{
    private readonly Dictionary<string, (CheckBox Include, ComboBox Default)> _rows = new(StringComparer.Ordinal);
    private readonly CheckBox _startSelectAsmExample = new() { Content = "Include ASM6f example" };
    private readonly PatchSettings _initial;
    private readonly IReadOnlyList<string> _initialExternalPatches;
    private readonly Action<PatchSettings, IReadOnlyList<string>> _commit;
    private bool _dirty;
    private bool _closeApproved;

    public PatchManagerWindow() : this(PatchSettings.None, [], (_, _) => { })
    {
    }

    public PatchManagerWindow(PatchSettings initial, IReadOnlyList<string> initialExternalPatches, Action<PatchSettings, IReadOnlyList<string>> commit)
    {
        InitializeComponent();
        _initial = initial;
        _initialExternalPatches = initialExternalPatches;
        _commit = commit;
        BuildRows();
    }

    private void BuildRows()
    {
        foreach (var definition in PatchRegistry.BuiltIns)
        {
            var current = GetSetting(definition.Id);
            var include = new CheckBox { Content = "Include", IsChecked = current is not null };
            var defaultBox = new ComboBox
            {
                Width = 170,
                ItemsSource = new[] { "Default off", "Default on" },
                SelectedIndex = current is not null ? (current.EnabledByDefault ? 1 : 0) : (definition.RecommendedDefault ? 1 : 0),
                IsEnabled = current is not null
            };
            include.Click += (_, _) =>
            {
                _dirty = true;
                defaultBox.IsEnabled = include.IsChecked == true;
                if (include.IsChecked == true && current is null)
                    defaultBox.SelectedIndex = definition.RecommendedDefault ? 1 : 0;
            };
            defaultBox.SelectionChanged += (_, _) => _dirty = true;

            var title = new TextBlock { Text = definition.DisplayName, FontWeight = FontWeight.SemiBold, FontSize = 16 };
            var description = new TextBlock { Text = definition.Description, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray };
            var compatibility = new TextBlock
            {
                Text = $"Verified profiles: {string.Join(", ", definition.SupportedProfiles ?? [])} · {(definition.RecommendedDefault ? "Recommended default: on" : "Recommended default: off")}",
                FontSize = 12,
                Foreground = Brushes.SlateGray
            };
            var row = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#182534")),
                BorderBrush = new SolidColorBrush(Color.Parse("#3A506B")),
                BorderThickness = new Avalonia.Thickness(1),
                Padding = new Avalonia.Thickness(12),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        title,
                        description,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 14,
                            Children = { include, new TextBlock { Text = "Default", VerticalAlignment = VerticalAlignment.Center }, defaultBox }
                        }
                    }
                }
            };
            PatchList.Children.Add(row);
            _rows[definition.Id] = (include, defaultBox);
        }

        _startSelectAsmExample.IsChecked = _initialExternalPatches.Contains("start-select-map", StringComparer.Ordinal);
        _startSelectAsmExample.Click += (_, _) => _dirty = true;
        PatchList.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#182534")),
            BorderBrush = new SolidColorBrush(Color.Parse("#3A506B")),
            BorderThickness = new Avalonia.Thickness(1),
            Padding = new Avalonia.Thickness(12),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = "ASM6f example: Start + Select: Return to Map", FontWeight = FontWeight.SemiBold, FontSize = 16 },
                    new TextBlock { Text = "A shipped external ASM patch package. While paused, Select returns to the map without completing the level. It is global-only and cannot be combined with the built-in Start + Select patch.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray },
                    _startSelectAsmExample
                }
            }
        });
    }

    private PatchSetting? GetSetting(string id) => id switch
    {
        "quick-retry" => _initial.QuickRetry,
        "start-select-map" => _initial.StartSelectReturnToMap,
        "continuous-auto-scroll" => _initial.ContinuousAutoScroll,
        _ => null
    };

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        PatchSetting? Setting(string id)
        {
            var row = _rows[id];
            if (row.Include.IsChecked != true) return null;
            return new PatchSetting(row.Default.SelectedIndex == 1, GetSetting(id)?.LevelOverrides);
        }

        var externalPatches = _initialExternalPatches.Where(static id => id != "start-select-map").ToList();
        if (_startSelectAsmExample.IsChecked == true) externalPatches.Add("start-select-map");
        _commit(new PatchSettings(
            Setting("quick-retry"),
            Setting("start-select-map"),
            Setting("continuous-auto-scroll")), externalPatches);
        _closeApproved = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        _closeApproved = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_closeApproved || !_dirty)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            var choice = Owner is null
                ? UnsavedChangesChoice.Cancel
                : await new UnsavedChangesDialog("closing the patch editor").ShowDialog<UnsavedChangesChoice>((Window)Owner);
            if (choice == UnsavedChangesChoice.Save)
            {
                Save_Click(this, new RoutedEventArgs());
            }
            else if (choice == UnsavedChangesChoice.Discard)
            {
                _closeApproved = true;
                Close();
            }
        });
        base.OnClosing(e);
    }
}
