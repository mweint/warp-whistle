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
    private readonly ComboBox _kindBox = new();
    private readonly ComboBox _slotBox = new();
    private readonly UniformGrid _paletteColors = new() { Columns = 4, Rows = 4, Width = 192, Height = 192 };
    private readonly UniformGrid _nesColors = new() { Columns = 16, Rows = 4, Width = 384, Height = 96 };
    private readonly TextBox _nameBox = new();
    private readonly TextBox _libraryName = new();
    private readonly ComboBox _libraryBox = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _undoButton = new() { Content = "Undo", MinWidth = 72 };
    private readonly Button _redoButton = new() { Content = "Redo", MinWidth = 72 };
    private bool _objects;
    private int _slot;
    private int _colorIndex;
    private bool _refreshing;
    private bool _saved;
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
        Width = 980;
        Height = 620;
        MinWidth = 820;
        MinHeight = 480;

        _kindBox.ItemsSource = new[] { "Background palettes (8 slots)", "Object / sprite palettes (4 slots)" };
        _slotBox.ItemTemplate = new FuncDataTemplate<PaletteSlotInfo>((item, _) => item is null
            ? new TextBlock()
            : PaletteRow(item.ToString(), item.Colors));
        _libraryBox.ItemTemplate = new FuncDataTemplate<SavedPalette>((item, _) => item is null
            ? new TextBlock()
            : PaletteRow(item.Name, item.Colors));
        _kindBox.SelectedIndex = 0;
        _kindBox.SelectionChanged += (_, _) => { if (!_refreshing) { _objects = _kindBox.SelectedIndex == 1; _slot = 0; RefreshFromHost(); } };
        _slotBox.SelectionChanged += (_, _) => { if (!_refreshing && _slotBox.SelectedItem is PaletteSlotInfo info) { _slot = info.Slot; RefreshFromHost(); } };
        _nameBox.LostFocus += (_, _) => UpdateName();
        _nameBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) UpdateName(); };
        KeyDown += PaletteEditorWindow_KeyDown;

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(16),
            RowSpacing = 12
        };
        root.Children.Add(new StackPanel
        {
            Spacing = 5,
            Children =
            {
                new TextBlock { Text = "Palette Editor", FontSize = 20, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Changes update the level immediately. A slot is shared by every level using that tileset slot.", TextWrapping = TextWrapping.Wrap },
                _kindBox
            }
        });

        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("404,*,220"), ColumnSpacing = 18 };
        Grid.SetRow(content, 1);
        content.Children.Add(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "NES color picker", FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Select a palette chip, then choose a NES color.", TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                ColorGridBorder(_nesColors)
            }
        });

        var editor = new StackPanel { Spacing = 9 };
        Grid.SetColumn(editor, 1);
        editor.Children.Add(new TextBlock { Text = "Palette", FontWeight = FontWeight.SemiBold });
        editor.Children.Add(_slotBox);
        editor.Children.Add(new TextBlock { Text = "Slot name (project-only)", FontWeight = FontWeight.SemiBold });
        editor.Children.Add(_nameBox);
        editor.Children.Add(_status);
        editor.Children.Add(new TextBlock { Text = "Palette colors", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        editor.Children.Add(ColorGridBorder(_paletteColors));
        content.Children.Add(editor);

        var library = new StackPanel { Spacing = 7 };
        Grid.SetColumn(library, 2);
        library.Children.Add(new TextBlock { Text = "Palette library", FontWeight = FontWeight.SemiBold });
        library.Children.Add(new TextBlock { Text = "Saved palettes are local; they do not consume ROM slots.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        library.Children.Add(_libraryBox);
        library.Children.Add(new TextBlock { Text = "Library name" });
        library.Children.Add(_libraryName);
        var save = new Button { Content = "Save to Library" };
        save.Click += SaveToLibrary_Click;
        var apply = new Button { Content = "Apply Selected" };
        apply.Click += ApplyLibrary_Click;
        var remove = new Button { Content = "Remove Selected" };
        remove.Click += RemoveLibrary_Click;
        var reset = new Button { Content = "Reset Slot to Stock" };
        reset.Click += ResetToStock_Click;
        library.Children.Add(save);
        library.Children.Add(apply);
        library.Children.Add(remove);
        library.Children.Add(reset);
        content.Children.Add(library);

        root.Children.Add(content);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        _undoButton.Click += (_, _) => UndoDraft();
        _redoButton.Click += (_, _) => RedoDraft();
        var cancelButton = new Button { Content = "Cancel", MinWidth = 90 };
        cancelButton.Click += (_, _) => Close();
        var saveButton = new Button { Content = "Save changes", MinWidth = 110 };
        saveButton.Click += (_, _) => { _commit(_drafts.Values.ToArray()); _saved = true; Close(); };
        actions.Children.Add(_undoButton);
        actions.Children.Add(_redoButton);
        actions.Children.Add(cancelButton);
        actions.Children.Add(saveButton);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);
        Content = root;
        BuildNesColorGrid();
        RefreshFromHost();
        UpdateUndoButtons();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_saved) _cancel();
        base.OnClosing(e);
    }

    public void RefreshFromHost()
    {
        _refreshing = true;
        _kindBox.SelectedIndex = _objects ? 1 : 0;
        var count = _objects ? 4 : 8;
        var slots = Enumerable.Range(0, count).Select(slot => GetCurrentSlot(_objects, slot)).Where(static item => item is not null).Cast<PaletteSlotInfo>().ToArray();
        _slotBox.ItemsSource = slots;
        _slotBox.SelectedItem = slots.FirstOrDefault(item => item.Slot == _slot) ?? slots.FirstOrDefault();
        var current = GetCurrentSlot(_objects, _slot) ?? slots.FirstOrDefault();
        if (current is not null)
        {
            _slot = current.Slot;
            _nameBox.Text = current.Name;
            _status.Text = $"{(current.IsModified ? "Modified in this project" : "Stock ROM palette")} - slot {current.Slot + 1} of {count}.";
            BuildPaletteColors(current.Colors);
        }
        RefreshLibrary();
        _refreshing = false;
    }

    private void BuildPaletteColors(IReadOnlyList<byte> colors)
    {
        _paletteColors.Children.Clear();
        for (var index = 0; index < 16; index++)
        {
            var color = index < colors.Count ? colors[index] : (byte)0;
            var chip = new Border
            {
                Background = Brush(color),
                BorderBrush = index == _colorIndex ? Brushes.White : new SolidColorBrush(Color.Parse("#3A4655")),
                BorderThickness = new Thickness(index == _colorIndex ? 2 : 1)
            };
            ToolTip.SetTip(chip, $"Color {index + 1}: ${color:X2}");
            var selected = index;
            chip.PointerPressed += (_, _) => { _colorIndex = selected; BuildPaletteColors(GetCurrentSlot(_objects, _slot)?.Colors ?? []); };
            _paletteColors.Children.Add(chip);
        }
    }

    private void BuildNesColorGrid()
    {
        for (byte color = 0; color < 64; color++)
        {
            var value = color;
            var chip = new Border
            {
                Background = Brush(value),
                BorderBrush = new SolidColorBrush(Color.Parse("#3A4655")),
                BorderThickness = new Thickness(1)
            };
            ToolTip.SetTip(chip, $"NES ${value:X2}");
            chip.PointerPressed += (_, _) => SetColor(value);
            _nesColors.Children.Add(chip);
        }
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

    private void UpdateName()
    {
        if (_refreshing) return;
        var current = GetCurrentSlot(_objects, _slot);
        if (current is null) return;
        SetDraft(current with { Name = (_nameBox.Text ?? string.Empty).Trim() });
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
        if (_libraryBox.SelectedItem is not SavedPalette palette) return;
        if (palette.IsBuiltIn) return;
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
        _preview(_drafts.Values.ToArray());
        UpdateUndoButtons();
    }

    private void UndoDraft()
    {
        if (_undo.Count == 0) return;
        _redo.Push(CloneDrafts());
        RestoreDrafts(_undo.Pop());
    }

    private void RedoDraft()
    {
        if (_redo.Count == 0) return;
        _undo.Push(CloneDrafts());
        RestoreDrafts(_redo.Pop());
    }

    private void RestoreDrafts(Dictionary<(bool Objects, int Slot), PaletteSlotInfo> drafts)
    {
        _drafts.Clear();
        foreach (var (key, value) in drafts) _drafts[key] = value;
        _preview(_drafts.Values.ToArray());
        RefreshFromHost();
        UpdateUndoButtons();
    }

    private Dictionary<(bool Objects, int Slot), PaletteSlotInfo> CloneDrafts() =>
        _drafts.ToDictionary(static pair => pair.Key, static pair => pair.Value with { Colors = pair.Value.Colors.ToArray() });

    private void UpdateUndoButtons()
    {
        _undoButton.IsEnabled = _undo.Count > 0;
        _redoButton.IsEnabled = _redo.Count > 0;
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
        row.Children.Add(new TextBlock { Text = label, MinWidth = 105, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new PalettePreview { Colors = PalettePreview.FromNesColors(colors) });
        return row;
    }
}
