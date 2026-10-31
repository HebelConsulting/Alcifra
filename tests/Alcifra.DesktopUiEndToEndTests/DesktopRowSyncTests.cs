using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Alcifra.DesktopClient.ViewModels;
using Avalonia.Controls.Selection;

namespace Alcifra.UiEndToEndTests;

// A reload brings the shown rows up to a fresh read in place (RowSync). The Messages tab refilled its conversation list
// instead: the selected row went, the list deselected, and the open conversation closed with its draft, then reopened,
// read, marked read and asked for the next reload — flickering for as long as the tab was open (2026-10-31). Avalonia's
// selection drops a row that is MOVED as well as one that is removed, which the selection tests below hold to.
public class DesktopRowSyncTests
{
    private sealed class Row(string key, int value)
    {
        public string Key { get; } = key;

        public int Value { get; set; } = value;

        public override string ToString() => $"{Key}={Value}";
    }

    private static void Sync(ObservableCollection<Row> rows, Row? keep, params (string Key, int Value)[] items) =>
        RowSync.InPlace(rows, items, r => r.Key, i => i.Key, i => new Row(i.Key, i.Value), (r, i) => r.Value = i.Value, keep);

    // The list control's own selection, as the Messages tab's ListBox holds it.
    private static SelectionModel<Row> Selecting(ObservableCollection<Row> rows, Row selected) =>
        new() { SingleSelect = true, Source = rows, SelectedItem = selected };

    [Fact]
    public void A_reload_that_only_changes_values_raises_nothing_and_keeps_the_selection()
    {
        var rows = new ObservableCollection<Row> { new("a", 1), new("b", 1) };
        var selection = Selecting(rows, rows[1]);
        var changes = 0;
        rows.CollectionChanged += (_, _) => changes++;

        Sync(rows, rows[1], ("a", 1), ("b", 0));

        Assert.Equal(0, changes);
        Assert.Same(rows[1], selection.SelectedItem);
        Assert.Equal(0, rows[1].Value);
    }

    [Fact]
    public void The_selection_survives_its_conversation_moving_to_the_top()
    {
        var rows = new ObservableCollection<Row> { new("a", 1), new("b", 1), new("c", 1) };
        var selected = rows[2];
        var selection = Selecting(rows, selected);

        Sync(rows, selected, ("c", 0), ("a", 1), ("d", 5), ("b", 1));

        Assert.Same(selected, selection.SelectedItem);
        Assert.Equal(["c", "a", "d", "b"], rows.Select(r => r.Key));
    }

    [Fact]
    public void The_selected_row_is_neither_removed_nor_moved_while_it_is_listed()
    {
        var rows = new ObservableCollection<Row> { new("a", 1), new("b", 1), new("c", 1) };
        var selected = rows[1];
        var touched = new List<Row>();
        rows.CollectionChanged += (_, e) =>
        {
            if (e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Move or NotifyCollectionChangedAction.Replace)
            {
                touched.AddRange(e.OldItems?.Cast<Row>() ?? []);
            }
        };

        Sync(rows, selected, ("c", 1), ("b", 2), ("a", 1));

        Assert.DoesNotContain(selected, touched);
        Assert.Equal(["c", "b", "a"], rows.Select(r => r.Key));
    }

    [Fact]
    public void Rows_follow_the_fresh_order_gone_ones_go_and_new_ones_come()
    {
        var rows = new ObservableCollection<Row> { new("a", 1), new("b", 1), new("c", 1) };

        Sync(rows, null, ("c", 1), ("d", 4), ("a", 2));

        Assert.Equal(["c=1", "d=4", "a=2"], rows.Select(r => r.ToString()));
    }

    [Fact]
    public void A_selected_row_no_longer_listed_goes()
    {
        var rows = new ObservableCollection<Row> { new("a", 1), new("b", 1) };

        Sync(rows, rows[1], ("a", 1));

        Assert.Equal(["a"], rows.Select(r => r.Key));
    }
}
