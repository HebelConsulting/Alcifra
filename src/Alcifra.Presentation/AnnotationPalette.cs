namespace Alcifra.Presentation;

/// <summary>
/// The colours an annotation may be made in, shared by every surface so the clients never drift apart (#2460,
/// ADR 1107): darkest to lightest, so a newly chosen tool starts in BLACK, as ink does on paper. Black and navy
/// joined the list with the drawing sessions. The desktop, the web and the phone all read THIS list — it was copied
/// in four places before.
/// </summary>
public static class AnnotationPalette
{
    /// <summary>The palette, darkest to lightest: black, navy, purple, red, green, blue, orange, yellow.</summary>
    public static IReadOnlyList<string> Colours { get; } =
        ["#000000", "#1A237E", "#CE93D8", "#FF8A80", "#8BC34A", "#4FC3F7", "#FFB74D", "#FFEB3B"];

    /// <summary>The colour a newly chosen tool draws in (the palette's darkest).</summary>
    public static string Default => Colours[0];
}
