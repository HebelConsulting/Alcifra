namespace Alcifra.Presentation;

/// <summary>
/// What a client asks before it deletes a row, answered identically by the desktop and the phone (ADRs 0650/0651;
/// the phone's ⋮ menu, mobile slice 5). A reference is REMOVED from the folder and the item stays; a folder goes to
/// the recycle bin with everything inside it; a document goes to the recycle bin.
/// </summary>
/// <remarks>The desktop held these sentences as hard-coded English until the phone needed them.</remarks>
public static class DeleteConfirmation
{
    /// <summary>The localization key of the question; its <c>{0}</c> is the row's name.</summary>
    public static string QuestionKey(bool isReference, bool isFolder) => (isReference, isFolder) switch
    {
        (true, _) => "DelConfirmReference",
        (false, true) => "DelConfirmFolder",
        _ => "DelConfirmDocument",
    };

    /// <summary>The localization key of the confirming button.</summary>
    public static string ButtonKey(bool isReference) => isReference ? "Remove" : "UgDelete";
}
