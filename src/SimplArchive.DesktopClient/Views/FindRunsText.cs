using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// Draws find-highlighted runs into a text block (ADR 0968: an opened password entry's name, labels and plain values),
/// in the same colours the text preview lights its hits with — one definition for both, so the two finds look alike.
/// A text block cannot bind its inlines, hence the attached property.
/// </summary>
public static class FindRunsText
{
    // The web pane's mark colors (PreviewPane.razor), so the two clients light the same hits the same way.
    public static readonly IBrush HitBrush = new SolidColorBrush(Color.Parse("#ffe58a"));
    public static readonly IBrush ActiveHitBrush = new SolidColorBrush(Color.Parse("#f6a821"));
    public static readonly IBrush HitForeground = Brushes.Black;

    public static readonly AttachedProperty<IReadOnlyList<FindRun>?> RunsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<FindRun>?>("Runs", typeof(FindRunsText));

    public static void SetRuns(TextBlock element, IReadOnlyList<FindRun>? value) => element.SetValue(RunsProperty, value);

    public static IReadOnlyList<FindRun>? GetRuns(TextBlock element) => element.GetValue(RunsProperty);

    static FindRunsText()
    {
        RunsProperty.Changed.AddClassHandler<TextBlock>((block, e) =>
        {
            var inlines = new InlineCollection();
            foreach (var run in e.NewValue as IReadOnlyList<FindRun> ?? [])
            {
                inlines.Add(run.IsHit
                    ? new Run(run.Text) { Background = run.IsActive ? ActiveHitBrush : HitBrush, Foreground = HitForeground }
                    : new Run(run.Text));
            }

            block.Text = null;
            block.Inlines = inlines;
        });
    }
}
