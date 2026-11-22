namespace Alcifra.Presentation;

/// <summary>
/// The names an annotation is listed under — the one mapping from a stored kind (core ADR 0525) to the
/// localization key a surface shows, so a list of annotations reads the same on every client.
/// </summary>
/// <remarks>
/// The stored kind is an int and append-only; the renderer's shape is a different question (a stamp and a text
/// box both draw as a captioned box), so this maps the KIND, not the drawn shape. An unknown value reads as a
/// note, which is how the renderers draw it.
/// </remarks>
public static class AnnotationKinds
{
    /// <summary>The localization key naming the given stored kind.</summary>
    public static string NameKey(int kind) => kind switch
    {
        1 => "AnnotationKindHighlight",
        2 => "AnnotationKindRectangle",
        3 => "AnnotationKindArrow",
        4 => "AnnotationKindStamp",
        5 => "AnnotationKindStrikethrough",
        6 => "AnnotationKindTextBox",
        7 => "AnnotationKindFreehand",
        _ => "AnnotationKindNote",
    };
}
