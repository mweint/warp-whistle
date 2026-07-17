using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Smb3Editor.Core;

namespace Smb3Editor.App;

public sealed partial class PatchManagerWindow : Window
{
    private readonly Dictionary<string, (CheckBox Include, ComboBox? Default)> _rows = new(StringComparer.Ordinal);
    private readonly PatchSettings _initial;
    private readonly IReadOnlyList<string> _initialExternalPatches;
    private readonly IReadOnlyList<PatchDefinition> _definitions;
    private readonly Action<PatchSettings, IReadOnlyList<string>> _commit;
    private readonly bool _enhancedOutput;
    private bool _dirty;
    private bool _closeApproved;

    public PatchManagerWindow() : this(PatchSettings.None, [], false, (_, _) => { })
    {
    }

    public PatchManagerWindow(PatchSettings initial, IReadOnlyList<string> initialExternalPatches, bool enhancedOutput,
        Action<PatchSettings, IReadOnlyList<string>> commit)
    {
        InitializeComponent();
        _initial = initial;
        _initialExternalPatches = initialExternalPatches;
        _enhancedOutput = enhancedOutput;
        _commit = commit;
        var catalog = PatchCatalog.Discover();
        _definitions = catalog.IsSuccess ? catalog.Value!.SelectMany(static package => package.Features).ToArray() : [];
        BuildRows();
    }

    private void BuildRows()
    {
        foreach (var definition in _definitions)
        {
            var current = _initial.Get(definition.Id);
            var include = new CheckBox { Content = "Include", IsChecked = current is not null, VerticalAlignment = VerticalAlignment.Center };
            var requiresEnhanced = definition.Id == "enhanced-autosave-storage";
            include.IsEnabled = !requiresEnhanced || _enhancedOutput || current is not null;
            ComboBox? defaultBox = null;
            if (definition.SupportsLevelOverrides)
            {
                defaultBox = new ComboBox
                {
                    Width = 135,
                    ItemsSource = new[] { "Default off", "Default on" },
                    SelectedIndex = current is not null ? (current.EnabledByDefault ? 1 : 0) : (definition.RecommendedDefault ? 1 : 0),
                    IsEnabled = current is not null,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            include.Click += (_, _) =>
            {
                _dirty = true;
                if (defaultBox is not null)
                {
                    defaultBox.IsEnabled = include.IsChecked == true;
                    if (include.IsChecked == true && current is null)
                        defaultBox.SelectedIndex = definition.RecommendedDefault ? 1 : 0;
                }
            };
            if (defaultBox is not null) defaultBox.SelectionChanged += (_, _) => _dirty = true;

            var description = definition.Id == "enhanced-autosave-storage"
                ? "Creates battery-backed automatic checkpoint storage. Requires Enhanced MMC3 output, selected in Project settings."
                : definition.Description;
            var title = new TextBlock
            {
                Text = definition.DisplayName,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTip.SetTip(title, $"{description}\nVerified: {string.Join(", ", definition.SupportedProfiles ?? [])}");
            ToolTip.SetTip(include, requiresEnhanced && !_enhancedOutput && current is null
                ? "Requires Enhanced MMC3 output. Enable it in File > Project settings first."
                : description);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(definition.SupportsLevelOverrides ? "*,Auto,Auto,Auto" : "*,Auto"), ColumnSpacing = 10 };
            grid.Children.Add(title);
            grid.Children.Add(include);
            Grid.SetColumn(include, 1);
            if (defaultBox is not null)
            {
                var defaultLabel = new TextBlock { Text = "Default", VerticalAlignment = VerticalAlignment.Center };
                grid.Children.Add(defaultLabel);
                grid.Children.Add(defaultBox);
                Grid.SetColumn(defaultLabel, 2);
                Grid.SetColumn(defaultBox, 3);
            }
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
                Padding = new Avalonia.Thickness(10, 6),
                Child = grid
            };
            PatchList.Children.Add(row);
            _rows[definition.Id] = (include, defaultBox);
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        PatchSetting? Setting(string id)
        {
            var row = _rows[id];
            if (row.Include.IsChecked != true) return null;
            return new PatchSetting(row.Default?.SelectedIndex == 1 || row.Default is null, _initial.Get(id)?.LevelOverrides);
        }

        var settings = _initial;
        foreach (var definition in _definitions) settings = settings.With(definition.Id, Setting(definition.Id));
        _commit(settings, _initialExternalPatches);
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
