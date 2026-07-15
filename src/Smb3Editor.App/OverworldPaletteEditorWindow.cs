using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Smb3Editor.Core;

namespace Smb3Editor.App;

public sealed record OverworldPaletteDraft(int Palette, bool Sprites, IReadOnlyList<byte> Colors);
internal sealed record OverworldPaletteChoice(int Value)
{
    public override string ToString() => $"Palette {Value + 1}";
}

/// <summary>Compact live editor for the shared palette IDs used by stock overworlds.</summary>
public sealed class OverworldPaletteEditorWindow : Window
{
    private readonly Func<int, bool, IReadOnlyList<byte>> _read;
    private readonly Action<int, bool, IReadOnlyList<byte>> _preview;
    private readonly Action<IReadOnlyList<OverworldPaletteDraft>> _commit;
    private readonly Action _cancel;
    private readonly ComboBox _tilePaletteBox = new();
    private readonly ComboBox _spritePaletteBox = new();
    private readonly UniformGrid _tiles = Grid();
    private readonly UniformGrid _sprites = Grid();
    private readonly Dictionary<(int Palette, bool Sprites), byte[]> _drafts = [];
    private int _tilePalette;
    private int _spritePalette;
    private bool _refreshing;
    private bool _dirty;
    private bool _approved;
    private bool _saved;
    private Popup? _picker;
    private Button? _undoButton;
    private Button? _redoButton;
    private readonly Stack<Dictionary<(int Palette, bool Sprites), byte[]>> _undo = [];
    private readonly Stack<Dictionary<(int Palette, bool Sprites), byte[]>> _redo = [];

    public OverworldPaletteEditorWindow(
        IReadOnlyList<int> tilePalettes,
        IReadOnlyList<int> spritePalettes,
        int activeTilePalette,
        int activeSpritePalette,
        Func<int, bool, IReadOnlyList<byte>> read,
        Action<int, bool, IReadOnlyList<byte>> preview,
        Action<IReadOnlyList<OverworldPaletteDraft>> commit,
        Action cancelDrafts)
    {
        _read = read; _preview = preview; _commit = commit; _cancel = cancelDrafts;
        _tilePalette = activeTilePalette; _spritePalette = activeSpritePalette;
        Title = "Overworld Palettes";
        Width = 285; Height = 470; MinWidth = 275; MinHeight = 455;
        _tilePaletteBox.ItemsSource = tilePalettes.Distinct().Order().Select(static value => new OverworldPaletteChoice(value)).ToArray();
        _spritePaletteBox.ItemsSource = spritePalettes.Distinct().Order().Select(static value => new OverworldPaletteChoice(value)).ToArray();
        _tilePaletteBox.SelectedItem = ((IEnumerable<OverworldPaletteChoice>)_tilePaletteBox.ItemsSource).FirstOrDefault(item => item.Value == _tilePalette);
        _spritePaletteBox.SelectedItem = ((IEnumerable<OverworldPaletteChoice>)_spritePaletteBox.ItemsSource).FirstOrDefault(item => item.Value == _spritePalette);
        _tilePaletteBox.SelectionChanged += (_, _) => Switch(false);
        _spritePaletteBox.SelectionChanged += (_, _) => Switch(true);
        KeyDown += OnKeyDown;

        var content = new StackPanel { Margin = new Thickness(12), Spacing = 10 };
        content.Children.Add(new TextBlock { Text = "Overworld palettes", FontWeight = FontWeight.SemiBold, FontSize = 18 });
        content.Children.Add(new TextBlock { Text = "Choose a shared palette, then click a color to choose a NES color. Preview is live; Save keeps it.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        content.Children.Add(Panel("Map tiles", _tilePaletteBox, _tiles));
        content.Children.Add(Panel("Map sprites", _spritePaletteBox, _sprites));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelButton = new Button { Content = "Cancel" }; cancelButton.Click += (_, _) => { _approved = true; Close(); };
        _undoButton = new Button { Content = "↶", Width = 32 }; ToolTip.SetTip(_undoButton, "Undo (Ctrl+Z)");
        _redoButton = new Button { Content = "↷", Width = 32 }; ToolTip.SetTip(_redoButton, "Redo (Ctrl+Y)");
        _undoButton.Click += (_, _) => UndoDraft();
        _redoButton.Click += (_, _) => RedoDraft();
        var save = new Button { Content = "💾", Width = 36 }; ToolTip.SetTip(save, "Save changes");
        save.Click += (_, _) => SaveAndClose();
        buttons.Children.Add(_undoButton); buttons.Children.Add(_redoButton); buttons.Children.Add(cancelButton); buttons.Children.Add(save); content.Children.Add(buttons);
        Content = content;
        Refresh();
        UpdateHistoryButtons();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_approved || !_dirty) { if (_approved && _dirty && !_saved) _cancel(); base.OnClosing(e); return; }
        e.Cancel = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            var choice = Owner is null ? UnsavedChangesChoice.Cancel : await new UnsavedChangesDialog("closing the overworld palette editor").ShowDialog<UnsavedChangesChoice>((Window)Owner);
            if (choice == UnsavedChangesChoice.Save) SaveAndClose();
            else if (choice == UnsavedChangesChoice.Discard) { _approved = true; Close(); }
        });
        base.OnClosing(e);
    }

    private void SaveAndClose()
    {
        if (_dirty) _commit(_drafts.Select(item => new OverworldPaletteDraft(item.Key.Palette, item.Key.Sprites, item.Value)).ToArray());
        _saved = true; _approved = true; Close();
    }

    private void Switch(bool sprites)
    {
        if (_refreshing) return;
        var choice = (sprites ? _spritePaletteBox : _tilePaletteBox).SelectedItem as OverworldPaletteChoice;
        if (choice is null) return;
        if (sprites) _spritePalette = choice.Value; else _tilePalette = choice.Value;
        Refresh();
    }

    private void Refresh()
    {
        _refreshing = true;
        Populate(_tiles, false, Current(_tilePalette, false));
        Populate(_sprites, true, Current(_spritePalette, true));
        _refreshing = false;
    }

    private byte[] Current(int palette, bool sprites) => _drafts.TryGetValue((palette, sprites), out var colors) ? colors : Normalize(_read(palette, sprites));

    private void Populate(UniformGrid grid, bool sprites, IReadOnlyList<byte> colors)
    {
        grid.Children.Clear();
        for (var index = 0; index < 16; index++)
        {
            var at = index; var chip = new Border { Background = Brush(colors[index]), BorderBrush = new SolidColorBrush(Color.Parse("#718399")), BorderThickness = new Thickness(1) };
            ToolTip.SetTip(chip, $"NES ${colors[index]:X2}");
            chip.PointerPressed += (_, _) => Pick(chip, sprites, at);
            grid.Children.Add(chip);
        }
    }

    private void Pick(Control anchor, bool sprites, int index)
    {
        var palette = sprites ? _spritePalette : _tilePalette;
        var colors = Current(palette, sprites).ToArray();
        _picker?.IsOpen = false;
        Popup? popup = null;
        popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, IsLightDismissEnabled = true,
            Child = new NesColorPickerPopup(colors[index], color =>
            {
                if (colors[index] == color) { popup!.IsOpen = false; return; }
                _undo.Push(CloneDrafts()); _redo.Clear();
                colors[index] = color; _drafts[(palette, sprites)] = colors; _dirty = true;
                _preview(palette, sprites, colors); Refresh(); popup!.IsOpen = false;
                UpdateHistoryButtons();
            }) };
        _picker = popup; popup.IsOpen = true;
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

    private void RestoreDrafts(Dictionary<(int Palette, bool Sprites), byte[]> drafts)
    {
        _drafts.Clear();
        foreach (var (key, value) in drafts) _drafts[key] = value.ToArray();
        _dirty = _drafts.Count > 0;
        _cancel();
        foreach (var (key, value) in _drafts) _preview(key.Palette, key.Sprites, value);
        Refresh();
        UpdateHistoryButtons();
    }

    private Dictionary<(int Palette, bool Sprites), byte[]> CloneDrafts() =>
        _drafts.ToDictionary(static item => item.Key, static item => item.Value.ToArray());

    private void UpdateHistoryButtons()
    {
        if (_undoButton is not null) _undoButton.IsEnabled = _undo.Count > 0;
        if (_redoButton is not null) _redoButton.IsEnabled = _redo.Count > 0;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.Key == Key.Z) { UndoDraft(); e.Handled = true; }
        else if (e.Key == Key.Y) { RedoDraft(); e.Handled = true; }
    }

    private static UniformGrid Grid() => new() { Columns = 4, Rows = 4, Width = 88, Height = 88 };
    private static Border Panel(string title, ComboBox selector, Control grid) => new() { Child = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, selector, new Border { Width = 90, Height = 90, HorizontalAlignment = HorizontalAlignment.Left, BorderBrush = new SolidColorBrush(Color.Parse("#718399")), BorderThickness = new Thickness(1), Child = grid } } } };
    private static byte[] Normalize(IReadOnlyList<byte> colors) => colors.Take(16).Concat(Enumerable.Repeat((byte)0, 16)).Take(16).ToArray();
    private static IBrush Brush(byte color) => new SolidColorBrush(Color.FromUInt32(NesPalette.Argb[color & 0x3F]));
}
