using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Smb3Editor.Core;

namespace Smb3Editor.App;

public sealed record PaletteSlotInfo(bool Objects, int Slot, string Name, bool IsModified, IReadOnlyList<byte> Colors)
{
    public override string ToString() => $"{Slot + 1}{(string.IsNullOrWhiteSpace(Name) ? string.Empty : $" - {Name}")} · {(IsModified ? "Modified" : "Stock")}";
}

/// <summary>Modeless editor for the fixed vanilla palette slots of one tileset.</summary>
public sealed class PaletteEditorWindow : Window
{
    private readonly Func<bool, int, PaletteSlotInfo?> _getSlot;
    private readonly Func<bool, int, PaletteSlotInfo?> _getStockSlot;
    private readonly Action<IReadOnlyList<PaletteSlotInfo>> _preview;
    private readonly Action<IReadOnlyList<PaletteSlotInfo>> _commit;
    private readonly Action _cancel;
    private readonly ComboBox _backgroundSlotBox = new();
    private readonly ComboBox _objectSlotBox = new();
    private readonly UniformGrid _backgroundColors = new() { Columns = 4, Rows = 4, Width = 88, Height = 88 };
    private readonly UniformGrid _objectColors = new() { Columns = 4, Rows = 4, Width = 88, Height = 88 };
    private readonly TextBox _libraryName = new();
    private readonly ComboBox _libraryBox = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private Popup? _colorPickerPopup;
    private bool _objects;
    private int _slot;
    private int _backgroundSlot;
    private int _objectSlot;
    private int _colorIndex;
    private bool _refreshing;
    private bool _saved;
    private Button? _undoButton;
    private Button? _redoButton;
    private bool _dirty;
    private bool _closeApproved;
    private readonly Dictionary<(bool Objects, int Slot), PaletteSlotInfo> _drafts = [];
    private readonly Stack<Dictionary<(bool Objects, int Slot), PaletteSlotInfo>> _undo = [];
    private readonly Stack<Dictionary<(bool Objects, int Slot), PaletteSlotInfo>> _redo = [];

    public PaletteEditorWindow(
        Func<bool, int, PaletteSlotInfo?> getSlot,
        Func<bool, int, PaletteSlotInfo?> getStockSlot,
        Action<IReadOnlyList<PaletteSlotInfo>> preview,
        Action<IReadOnlyList<PaletteSlotInfo>> commit,
        Action cancel)
    {
        _getSlot = getSlot;
        _getStockSlot = getStockSlot;
        _preview = preview;
        _commit = commit;
        _cancel = cancel;
        Title = "Palette Editor";
        Width = 240;
        Height = 370;
        MinWidth = 230;
        MinHeight = 350;
        _libraryName.Width = 130;
        _libraryName.PlaceholderText = "Name to save";
        _libraryBox.Width = 150;

        ConfigurePaletteSelector(_backgroundSlotBox);
        ConfigurePaletteSelector(_objectSlotBox);
        _libraryBox.ItemTemplate = new FuncDataTemplate<SavedPalette>((item, _) => item is null
            ? new TextBlock()
            : PaletteRow(item.Name, item.Colors));
        _backgroundSlotBox.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _backgroundSlotBox.SelectedItem is PaletteSlotInfo info)
            {
                _backgroundSlot = info.Slot;
                ActivatePalette(false, _backgroundSlot, 0);
            }
        };
        _objectSlotBox.SelectionChanged += (_, _) =>
        {
            if (!_refreshing && _objectSlotBox.SelectedItem is PaletteSlotInfo info)
            {
                _objectSlot = info.Slot;
                ActivatePalette(true, _objectSlot, 0);
            }
        };
        KeyDown += PaletteEditorWindow_KeyDown;

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(12),
            RowSpacing = 8
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(PalettePanel("Background palette", _backgroundSlotBox, _backgroundColors));
        content.Children.Add(PalettePanel("Object / sprite palette", _objectSlotBox, _objectColors));
        root.Children.Add(content);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Spacing = 5 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 90 };
        cancelButton.Click += (_, _) => { _closeApproved = true; Close(); };
        _undoButton = new Button { Content = "↶", Width = 32 };
        _redoButton = new Button { Content = "↷", Width = 32 };
        ToolTip.SetTip(_undoButton, "Undo (Ctrl+Z)");
        ToolTip.SetTip(_redoButton, "Redo (Ctrl+Y)");
        _undoButton.Click += (_, _) => UndoDraft();
        _redoButton.Click += (_, _) => RedoDraft();
        actions.Children.Add(_undoButton);
        actions.Children.Add(_redoButton);
        var saveButton = new Button { Content = "💾", Width = 34 };
        ToolTip.SetTip(saveButton, "Save changes");
        saveButton.Click += (_, _) => { _commit(_drafts.Values.ToArray()); _saved = true; _closeApproved = true; Close(); };
        actions.Children.Add(cancelButton);
        actions.Children.Add(saveButton);
        Grid.SetRow(actions, 1);
        root.Children.Add(actions);
        Content = root;
        RefreshFromHost();
        UpdateHistoryButtons();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_closeApproved || _saved || !_dirty)
        {
            if (!_saved && _closeApproved) _cancel();
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            var choice = Owner is null
                ? UnsavedChangesChoice.Cancel
                : await new UnsavedChangesDialog("closing the palette editor").ShowDialog<UnsavedChangesChoice>((Window)Owner);
            if (choice == UnsavedChangesChoice.Save)
            {
                _commit(_drafts.Values.ToArray());
                _saved = true;
                _closeApproved = true;
                Close();
            }
            else if (choice == UnsavedChangesChoice.Discard)
            {
                _closeApproved = true;
                Close();
            }
        });
        base.OnClosing(e);
    }

    public void RefreshFromHost()
    {
        _refreshing = true;
        var backgrounds = Slots(false, 8);
        var objects = Slots(true, 4);
        _backgroundSlot = SelectSlot(_backgroundSlotBox, backgrounds, _backgroundSlot);
        _objectSlot = SelectSlot(_objectSlotBox, objects, _objectSlot);
        BuildPaletteColors(_backgroundColors, false, _backgroundSlot);
        BuildPaletteColors(_objectColors, true, _objectSlot);
        RefreshActiveDetails();
        _refreshing = false;
    }

    private static void ConfigurePaletteSelector(ComboBox selector)
    {
        // Keep the selected value compact; the expanded list retains its
        // color preview to make palette selection visual.
        selector.SelectionBoxItemTemplate = new FuncDataTemplate<PaletteSlotInfo>((item, _) => new TextBlock { Text = item?.ToString() ?? string.Empty });
        selector.ItemTemplate = new FuncDataTemplate<PaletteSlotInfo>((item, _) => item is null
            ? new TextBlock()
            : PaletteRow(item.ToString(), item.Colors));
    }

    private static StackPanel PalettePanel(string title, ComboBox selector, UniformGrid colors) => new()
    {
        Spacing = 5,
        Children =
        {
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
            selector,
            ColorGridBorder(colors)
        }
    };

    private PaletteSlotInfo[] Slots(bool objects, int count) =>
        Enumerable.Range(0, count).Select(slot => GetCurrentSlot(objects, slot)).Where(static item => item is not null).Cast<PaletteSlotInfo>().ToArray();

    private static int SelectSlot(ComboBox selector, PaletteSlotInfo[] slots, int preferredSlot)
    {
        var selected = slots.FirstOrDefault(item => item.Slot == preferredSlot) ?? slots.FirstOrDefault();
        selector.ItemsSource = slots;
        selector.SelectedItem = selected;
        return selected?.Slot ?? 0;
    }

    private void BuildPaletteColors(UniformGrid grid, bool objects, int slot)
    {
        grid.Children.Clear();
        var colors = GetCurrentSlot(objects, slot)?.Colors ?? [];
        for (var index = 0; index < 16; index++)
        {
            var color = index < colors.Count ? colors[index] : (byte)0;
            var isSelected = objects == _objects && slot == _slot && index == _colorIndex;
            var chip = new Border
            {
                Background = Brush(color),
                BorderBrush = isSelected ? Brushes.White : new SolidColorBrush(Color.Parse("#3A4655")),
                BorderThickness = new Thickness(isSelected ? 2 : 1)
            };
            ToolTip.SetTip(chip, $"Color {index + 1}: ${color:X2}");
            var selected = index;
            chip.PointerPressed += (_, e) => PaletteChipPointerPressed(chip, objects, slot, selected, e);
            grid.Children.Add(chip);
        }
    }

    private void PaletteChipPointerPressed(Border chip, bool objects, int slot, int colorIndex, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(chip).Properties.IsRightButtonPressed)
        {
            var source = GetCurrentSlot(_objects, _slot);
            if (source is not null && _colorIndex is >= 0 and < 16 && _objects == objects)
            {
                var sourceColor = source.Colors[_colorIndex];
                _objects = objects;
                _slot = slot;
                _colorIndex = colorIndex;
                SetColor(sourceColor);
                e.Handled = true;
                return;
            }
        }

        OpenColorPicker(chip, objects, slot, colorIndex);
    }

    private void OpenColorPicker(Control anchor, bool objects, int slot, int colorIndex)
    {
        // Do not refresh here: it would replace the clicked chip before the
        // popup can use it as an anchor, causing Avalonia to dismiss it.
        _objects = objects;
        _slot = slot;
        _colorIndex = colorIndex;
        var current = GetCurrentSlot(objects, slot);
        var selectedColor = current is not null && colorIndex < current.Colors.Count ? current.Colors[colorIndex] : (byte)0;
        _colorPickerPopup?.IsOpen = false;
        Popup? popup = null;
        popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            IsLightDismissEnabled = true,
            Child = new NesColorPickerPopup(selectedColor, value =>
            {
                SetColor(value);
                popup!.IsOpen = false;
            })
        };
        _colorPickerPopup = popup;
        popup.IsOpen = true;
    }

    private void ActivatePalette(bool objects, int slot, int colorIndex)
    {
        _objects = objects;
        _slot = slot;
        _colorIndex = colorIndex;
        RefreshFromHost();
    }

    private void RefreshActiveDetails()
    {
        var current = GetCurrentSlot(_objects, _slot);
        if (current is null) return;
        _status.Text = $"{(_objects ? "Object" : "Background")} slot {current.Slot + 1} of {(_objects ? 4 : 8)} — {(current.IsModified ? "modified" : "stock")}.";
        RefreshLibrary();
    }

    private void SetColor(byte value)
    {
        var current = GetCurrentSlot(_objects, _slot);
        if (current is null) return;
        var colors = current.Colors.Take(16).Concat(Enumerable.Repeat((byte)0, 16)).Take(16).ToArray();
        colors[_colorIndex] = value;
        SetDraft(current with { Colors = colors, IsModified = true });
        RefreshFromHost();
    }

    private void RefreshLibrary()
    {
        var loaded = PaletteLibraryStore.Load();
        _libraryBox.ItemsSource = loaded.IsSuccess
            ? loaded.Value!.Where(item => item.Objects == _objects).OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
    }

    private void SaveToLibrary_Click(object? sender, RoutedEventArgs e)
    {
        var current = GetCurrentSlot(_objects, _slot);
        var name = (_libraryName.Text ?? string.Empty).Trim();
        if (current is null || string.IsNullOrWhiteSpace(name)) return;
        var loaded = PaletteLibraryStore.Load();
        if (!loaded.IsSuccess) return;
        var entries = loaded.Value!.Where(item => !(item.Objects == _objects && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))).ToList();
        entries.Add(new SavedPalette(name, _objects, current.Colors.ToArray()));
        PaletteLibraryStore.Save(entries);
        RefreshLibrary();
    }

    private void ApplyLibrary_Click(object? sender, RoutedEventArgs e)
    {
        if (_libraryBox.SelectedItem is not SavedPalette palette) return;
        var current = GetCurrentSlot(_objects, _slot);
        if (current is null) return;
        SetDraft(current with { Colors = palette.Colors.ToArray(), IsModified = true });
        RefreshFromHost();
    }

    private void RemoveLibrary_Click(object? sender, RoutedEventArgs e)
    {
        if (_libraryBox.SelectedItem is not SavedPalette palette || palette.IsBuiltIn) return;
        var loaded = PaletteLibraryStore.Load();
        if (!loaded.IsSuccess) return;
        PaletteLibraryStore.Save(loaded.Value!.Where(item => !(item.Objects == palette.Objects && string.Equals(item.Name, palette.Name, StringComparison.OrdinalIgnoreCase))).ToArray());
        RefreshLibrary();
    }

    private void ResetToStock_Click(object? sender, RoutedEventArgs e)
    {
        var stock = _getStockSlot(_objects, _slot);
        if (stock is null) return;
        SetDraft(stock with { Name = GetCurrentSlot(_objects, _slot)?.Name ?? stock.Name, IsModified = false });
        RefreshFromHost();
    }

    private PaletteSlotInfo? GetCurrentSlot(bool objects, int slot) =>
        _drafts.TryGetValue((objects, slot), out var draft) ? draft : _getSlot(objects, slot);

    private void SetDraft(PaletteSlotInfo slot)
    {
        var current = GetCurrentSlot(slot.Objects, slot.Slot);
        if (current is not null && SameSlot(current, slot)) return;
        _undo.Push(CloneDrafts());
        _redo.Clear();
        _drafts[(slot.Objects, slot.Slot)] = slot;
        _dirty = true;
        _preview(_drafts.Values.ToArray());
        UpdateHistoryButtons();
    }

    private void UndoDraft()
    {
        if (_undo.Count == 0) return;
        _redo.Push(CloneDrafts());
        RestoreDrafts(_undo.Pop());
        UpdateHistoryButtons();
    }

    private void RedoDraft()
    {
        if (_redo.Count == 0) return;
        _undo.Push(CloneDrafts());
        RestoreDrafts(_redo.Pop());
        UpdateHistoryButtons();
    }

    private void RestoreDrafts(Dictionary<(bool Objects, int Slot), PaletteSlotInfo> drafts)
    {
        _drafts.Clear();
        foreach (var (key, value) in drafts) _drafts[key] = value;
        _preview(_drafts.Values.ToArray());
        RefreshFromHost();
    }

    private Dictionary<(bool Objects, int Slot), PaletteSlotInfo> CloneDrafts() =>
        _drafts.ToDictionary(static pair => pair.Key, static pair => pair.Value with { Colors = pair.Value.Colors.ToArray() });

    private void UpdateHistoryButtons()
    {
        if (_undoButton is not null) _undoButton.IsEnabled = _undo.Count > 0;
        if (_redoButton is not null) _redoButton.IsEnabled = _redo.Count > 0;
    }

    private void PaletteEditorWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Key == Key.Z) { UndoDraft(); e.Handled = true; }
        else if (e.Key == Key.Y) { RedoDraft(); e.Handled = true; }
    }

    private static bool SameSlot(PaletteSlotInfo left, PaletteSlotInfo right) =>
        left.Objects == right.Objects && left.Slot == right.Slot && left.Name == right.Name &&
        left.IsModified == right.IsModified && left.Colors.SequenceEqual(right.Colors);

    private static IBrush Brush(byte value) => new SolidColorBrush(Color.FromUInt32(NesPalette.Argb[value & 0x3F]));

    private static Border ColorGridBorder(Control grid) => new()
    {
        Width = grid.Width + 2,
        Height = grid.Height + 2,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        BorderBrush = new SolidColorBrush(Color.Parse("#718399")),
        BorderThickness = new Thickness(1),
        Child = grid
    };

    private static Control PaletteRow(string label, IReadOnlyList<byte> colors)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = label, MinWidth = 92, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new PalettePreview { Colors = PalettePreview.FromNesColors(colors) });
        return row;
    }
}

internal sealed class NesColorPickerPopup : Border
{
    public NesColorPickerPopup(byte selectedColor, Action<byte> select)
    {
        Background = new SolidColorBrush(Color.Parse("#172231"));
        BorderBrush = new SolidColorBrush(Color.Parse("#718399"));
        BorderThickness = new Thickness(1);
        Padding = new Thickness(8);
        // NES palette bytes are arranged as four brightness rows of sixteen hues.
        // Keeping that native order makes related colors easy to scan and compare.
        var colors = new UniformGrid { Columns = 16, Rows = 4, Width = 320, Height = 80 };
        for (byte color = 0; color < 64; color++)
        {
            var value = color;
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromUInt32(NesPalette.Argb[value & 0x3F])),
                BorderBrush = value == selectedColor ? Brushes.White : new SolidColorBrush(Color.Parse("#3A4655")),
                BorderThickness = new Thickness(value == selectedColor ? 2 : 1)
            };
            ToolTip.SetTip(chip, $"NES ${value:X2}");
            chip.PointerPressed += (_, _) => select(value);
            colors.Children.Add(chip);
        }
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new Border { BorderBrush = new SolidColorBrush(Color.Parse("#718399")), BorderThickness = new Thickness(1), Child = colors }
            }
        };
    }
}
