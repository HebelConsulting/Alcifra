namespace SimplArchive.Api.Documents;

/// <summary>
/// A document's name qualified by its parent's, <c>Parent / Name</c> (ADR 0619): what a flat list shows so that two
/// collections or stores of the same name in different places can be told apart — two users' "My Calendar", or an
/// administrator seeing another user's "My Passwords" beside their own. A personal space's root is named after its
/// owner (ADR 0671), so in a personal space the qualifier is the owner's name. A root has no parent and keeps its name.
/// </summary>
/// <remarks>One rule for every list that needs it (the DAV collections, the password stores), never a copy per list.</remarks>
public static class ParentQualifiedName
{
    public const string Separator = " / ";

    public static string For(string? parentName, string name) => parentName is null ? name : $"{parentName}{Separator}{name}";
}
