using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Alcifra.DesktopClient.Views;

// Renders a search-highlight snippet (ADR "Search result highlighting") into a TextBlock's Inlines: the
// server sends the fragment with matched terms wrapped in <em>…</em> and everything else HTML-escaped; the shared
// SearchSnippet splits and decodes it (the phone renders the same runs), and the emphasized runs are made bold here. Avalonia's TextBlock can't bind
// Inlines directly, hence this attached property.
public static partial class InlineHighlighter
{
    public static readonly AttachedProperty<string?> HtmlProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Html", typeof(InlineHighlighter));

    public static void SetHtml(TextBlock element, string? value) => element.SetValue(HtmlProperty, value);

    public static string? GetHtml(TextBlock element) => element.GetValue(HtmlProperty);

    static InlineHighlighter()
    {
        HtmlProperty.Changed.AddClassHandler<TextBlock>((textBlock, e) =>
        {
            textBlock.Inlines?.Clear();
            var html = e.NewValue as string;
            if (string.IsNullOrEmpty(html))
            {
                return;
            }

            foreach (var (text, isHit) in Alcifra.Presentation.SearchSnippet.Parts(html))
            {
                textBlock.Inlines?.Add(isHit ? new Run(text) { FontWeight = FontWeight.Bold } : new Run(text));
            }
        });
    }
}
