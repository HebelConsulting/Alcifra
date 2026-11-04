namespace Alcifra.Presentation;

/// <summary>What Move to… may do with a selection: the answer both clients must give alike (#2146).</summary>
public enum MoveOffer
{
    /// <summary>Nothing is selected.</summary>
    Nothing,

    /// <summary>Every selected row may move.</summary>
    Allowed,

    /// <summary>A selected row's caller may not move it (its <c>canMove</c> flag, ADR 0728).</summary>
    NoRight,

    /// <summary>
    /// A reference is selected. Move would move either the shortcut or the original, and the two are easy to confuse, so it is
    /// not offered: delete the reference and place a new one.
    /// </summary>
    HasReference,
}

/// <summary>
/// Whether a selection can be moved, and why not. All or nothing, as the bulk bar's rule is: a button promising N moves and
/// performing some is not honest (ADR 0543). A reference names the reason first, since no right would change it.
/// </summary>
public static class MoveSelection
{
    public static MoveOffer Of<T>(IReadOnlyCollection<T> rows, Func<T, bool> isReference, Func<T, bool> canMove) =>
        rows.Count == 0 ? MoveOffer.Nothing
        : rows.Any(isReference) ? MoveOffer.HasReference
        : rows.All(canMove) ? MoveOffer.Allowed
        : MoveOffer.NoRight;

    /// <summary>The menu shows Move for what could move or says why; a lone reference simply has none, as on every client.</summary>
    public static bool ShowsInMenu<T>(IReadOnlyCollection<T> rows, Func<T, bool> isReference, Func<T, bool> canMove) =>
        Of(rows, isReference, canMove) switch
        {
            MoveOffer.Allowed => true,
            MoveOffer.HasReference => rows.Count > 1,
            _ => false,
        };

    /// <summary>The localization key of the sentence saying why not; null where it can move or nothing is selected.</summary>
    public static string? WhyNotKey(MoveOffer offer) => offer switch
    {
        MoveOffer.NoRight => "MoveWhyNoRight",
        MoveOffer.HasReference => "MoveWhyReference",
        _ => null,
    };
}
