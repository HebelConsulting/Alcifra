namespace Alcifra.Presentation;

/// <summary>
/// Find-in-document over a PDF's or a scan's word boxes: which words a query lights up. One rule for every client
/// (the desktop's preview and the phone's, SimplArchiveMobile #14), so both find the same things; a text document's
/// find is <see cref="TextFind"/> instead, over the characters.
/// </summary>
/// <remarks>
/// <b>Per word, by terms:</b> the query splits on whitespace into terms, and a word matches when it contains any term,
/// ignoring case, so "invoice" also lights up "Invoices". A blank query matches nothing.
/// </remarks>
public static class WordFind
{
    /// <summary>The query's terms; empty for a blank query.</summary>
    public static IReadOnlyList<string> Terms(string? query) =>
        (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Whether <paramref name="word"/> contains any of <paramref name="terms"/>, ignoring case.</summary>
    public static bool Matches(string? word, IReadOnlyList<string> terms) =>
        !string.IsNullOrEmpty(word) && terms.Any(t => word.Contains(t, StringComparison.OrdinalIgnoreCase));
}
