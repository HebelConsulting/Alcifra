namespace Alcifra.Presentation;

/// <summary>
/// The colours an annotation may be made in — TWO sets since the owner's decision of 2026-11-25 (#2469), shared by every
/// surface so the clients never drift apart (#2460, ADR 1107): a PASTEL set of soft washes, the default for the two kinds
/// that cover text, and a STRONG set of deep inks, the default for the drawing kinds and starting in black as ink does on
/// paper. Both sets are reachable for EVERY kind through the bar's toggle; the default only says where a newly chosen
/// tool starts.
/// </summary>
public static class AnnotationPalette
{
    /// <summary>The soft set — the default for Highlight and Note, the two kinds that cover text.</summary>
    /// <remarks>Purple, red, green, blue, orange, yellow — no black or navy: a wash that dark hides what it marks.</remarks>
    public static IReadOnlyList<string> Pastel { get; } =
        ["#CE93D8", "#FF8A80", "#A5D6A7", "#90CAF9", "#FFCC80", "#FFF59D"];

    /// <summary>The ink set — the default for every drawing kind, darkest to lightest.</summary>
    /// <remarks>Black, navy, purple, red, green, blue, orange, dark yellow: marks that must read as permanent ink.</remarks>
    public static IReadOnlyList<string> Strong { get; } =
        ["#000000", "#1A237E", "#7B1FA2", "#D32F2F", "#388E3C", "#1976D2", "#EF6C00", "#F9A825"];

    /// <summary>The set a newly chosen kind shows, and starts in. The toggle still reaches the other set for any kind.</summary>
    /// <param name="kind">The stored kind (core ADR 0525): 1 = Highlight, 0/unknown = Note — the only two pastel ones.</param>
    public static IReadOnlyList<string> ForKind(int kind) => kind is 1 or 0 ? Pastel : Strong;

    /// <summary>The colour a newly chosen kind starts in: its default set's first colour.</summary>
    public static string DefaultFor(int kind) => ForKind(kind)[0];

    /// <summary>The strongest set, for callers that predate the split and want a full palette: the drawing kinds' default.
    /// New code asks <see cref="ForKind"/>, so its tool starts where its kind says.</summary>
    public static IReadOnlyList<string> Colours => Strong;

    /// <summary>The drawing kinds' first colour — the pre-split default. New code asks <see cref="DefaultFor"/>.</summary>
    public static string Default => Strong[0];
}
