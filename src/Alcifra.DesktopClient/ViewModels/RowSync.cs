using System.Collections.ObjectModel;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// Brings a shown list up to a fresh read IN PLACE: a row whose item is still listed is updated, not replaced, and the
/// <c>keep</c> row (the selection) is never removed or moved while it is listed. A list control deselects a row that is
/// removed AND one that is moved (Avalonia's selection model drops it on both), and the Messages tab refilled its list
/// on every reload, so the open conversation closed with its draft and reopened, its own "read" asking for the next
/// reload: it flickered for as long as the tab was open (2026-10-31).
/// </summary>
public static class RowSync
{
    public static void InPlace<TRow, TItem, TKey>(ObservableCollection<TRow> rows, IReadOnlyList<TItem> items,
        Func<TRow, TKey> rowKey, Func<TItem, TKey> itemKey, Func<TItem, TRow> create, Action<TRow, TItem> update, TRow? keep)
        where TRow : class
        where TKey : notnull
    {
        var shown = rows.ToDictionary(rowKey);
        var target = new List<TRow>(items.Count);
        foreach (var item in items)
        {
            if (shown.TryGetValue(itemKey(item), out var row))
            {
                update(row, item);
                target.Add(row);
            }
            else
            {
                target.Add(create(item));
            }
        }

        if (rows.SequenceEqual(target, ReferenceEqualityComparer.Instance))
        {
            return;   // the usual reload: only values changed, and the list itself raises nothing
        }

        var anchor = keep is not null && target.Contains(keep) ? keep : null;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(rows[i], anchor))
            {
                rows.RemoveAt(i);
            }
        }

        var at = anchor is null ? target.Count : target.IndexOf(anchor);
        for (var i = 0; i < at; i++)
        {
            rows.Insert(i, target[i]);
        }

        for (var i = at + 1; i < target.Count; i++)
        {
            rows.Add(target[i]);
        }
    }
}
