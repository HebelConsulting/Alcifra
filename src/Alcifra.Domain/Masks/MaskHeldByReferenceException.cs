namespace Alcifra.Domain.Masks;

/// <summary>
/// A document's mask may not change while a folder or a reference field holds it and would not take the new one
/// (#2214, ADR 1074). Thrown from <c>SaveChanges</c> and translated at the Api boundary like the other mask invariants.
/// </summary>
/// <remarks>
/// Its own type for the reason <see cref="StructuralMaskImmutableException"/> gives: the mask endpoint's catch assumes a
/// missing required field, and the remedy here is a different one, to take the document out of what holds it.
/// </remarks>
public sealed class MaskHeldByReferenceException : InvalidOperationException
{
    private MaskHeldByReferenceException(string message)
        : base(message)
    {
    }

    /// <summary>Referenced from a folder whose admission binds its references, which does not admit the new mask.</summary>
    public static MaskHeldByReferenceException ByFolder(string documentName, string maskName, string folderName) =>
        new($"'{documentName}' cannot become a '{maskName}': the folder '{folderName}' holds it and admits no '{maskName}'. "
            + "Remove it from that folder first.");

    /// <summary>Named by a reference field that accepts only some masks, the new one not among them.</summary>
    public static MaskHeldByReferenceException ByField(string documentName, string maskName, string fieldName, string holderName) =>
        new($"'{documentName}' cannot become a '{maskName}': the field '{fieldName}' of '{holderName}' names it and accepts "
            + $"no '{maskName}'. Clear that field first.");
}
