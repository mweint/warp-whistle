using System.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Smb3Editor.App;

public sealed partial class SearchableDropDown : UserControl
{
    private IReadOnlyList<object> _items = [];
    private object? _selectedItem;

    public SearchableDropDown() => InitializeComponent();

    public event EventHandler? SelectionChanged;

    public IEnumerable? ItemsSource
    {
        get => _items;
        set
        {
            _items = value?.Cast<object>().ToArray() ?? [];
            ApplyFilter();
        }
    }

    public object? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value)) return;
            _selectedItem = value;
            SelectedText.Text = value?.ToString() ?? "Select…";
            ItemsList.SelectedItem = value;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DropDownButton_Click(object? sender, RoutedEventArgs e)
    {
        DropDownPopup.IsOpen = true;
        FilterBox.Text = string.Empty;
        ApplyFilter();
        Dispatcher.UIThread.Post(() => FilterBox.Focus(), DispatcherPriority.Input);
    }

    private void FilterBox_TextChanged(object? sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = FilterBox?.Text?.Trim() ?? string.Empty;
        ItemsList.ItemsSource = string.IsNullOrEmpty(query)
            ? _items
            : _items.Where(item => (item.ToString() ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void ItemsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Keyboard navigation only highlights. Enter or a pointer release commits.
    }

    private void ItemsList_PointerReleased(object? sender, PointerReleasedEventArgs e) => CommitHighlightedItem();

    private void FilterBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DropDownPopup.IsOpen = false; e.Handled = true; }
        else if (e.Key == Key.Down && ItemsList.ItemCount > 0)
        {
            ItemsList.SelectedIndex = 0;
            ItemsList.Focus();
            e.Handled = true;
        }
    }

    private void ItemsList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DropDownPopup.IsOpen = false; e.Handled = true; }
        else if (e.Key == Key.Enter && ItemsList.SelectedItem is not null)
        {
            CommitHighlightedItem();
            e.Handled = true;
        }
    }

    private void CommitHighlightedItem()
    {
        if (ItemsList.SelectedItem is null) return;
        SelectedItem = ItemsList.SelectedItem;
        DropDownPopup.IsOpen = false;
    }
}
