using Avalonia;
using Avalonia.Controls;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// A context menu shows a separator only BETWEEN two visible groups (#2073): never first, never last, never two in a row.
/// A menu's entries hide by the row's rels and flags, and a separator bound to one entry's flag drifted from the group it
/// fenced, so two separators met with nothing between them (Legal hold over Compare). The rule is applied once, when the
/// menu opens, over what is visible then; every context menu of the app has it (App.axaml).
/// </summary>
public static class MenuSeparators
{
    public static readonly AttachedProperty<bool> TidyProperty =
        AvaloniaProperty.RegisterAttached<ContextMenu, bool>("Tidy", typeof(MenuSeparators));

    public static void SetTidy(ContextMenu menu, bool value) => menu.SetValue(TidyProperty, value);

    public static bool GetTidy(ContextMenu menu) => menu.GetValue(TidyProperty);

    static MenuSeparators() => TidyProperty.Changed.AddClassHandler<ContextMenu>((menu, e) =>
    {
        // Opening when a person opens it (right click, the menu key); Opened too, because a menu opened in code
        // (Open(), the headless screenshot) raises only that one.
        if (e.NewValue is true)
        {
            menu.Opening += OnOpening;
            menu.Opened += OnOpened;
        }
        else
        {
            menu.Opening -= OnOpening;
            menu.Opened -= OnOpened;
        }
    });

    private static void OnOpened(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => OnOpening(sender, null);

    private static void OnOpening(object? sender, System.ComponentModel.CancelEventArgs? e)
    {
        if (sender is ItemsControl menu)
        {
            Apply(menu.Items.OfType<Control>().ToList());
        }
    }

    /// <summary>Shows each separator only where a visible entry comes before it (since the last one shown) and after it.</summary>
    public static void Apply(IReadOnlyList<Control> items)
    {
        Separator? pending = null;
        var entryBefore = false;
        foreach (var item in items)
        {
            if (item is Separator separator)
            {
                separator.IsVisible = false;
                if (entryBefore)
                {
                    pending = separator;   // shown only once a visible entry follows it
                }
            }
            else if (item.IsVisible)
            {
                if (pending is not null)
                {
                    pending.IsVisible = true;
                    pending = null;
                }

                entryBefore = true;
            }
        }
    }
}
