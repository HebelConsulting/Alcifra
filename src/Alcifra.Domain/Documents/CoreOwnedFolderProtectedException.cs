namespace Alcifra.Domain.Documents;

/// <summary>
/// A folder the core keeps for itself (ADR 1066: the tenant's <c>Administration</c> root and its <c>Users</c> folder) is
/// refused every change a person could make to it: it is not deleted, renamed or moved, and only the core places or removes
/// a reference in it. Thrown by <c>SaveChanges</c>; the API translates it to its own refusal.
/// </summary>
public sealed class CoreOwnedFolderProtectedException : InvalidOperationException
{
    private CoreOwnedFolderProtectedException(string message)
        : base(message)
    {
    }

    public static CoreOwnedFolderProtectedException CannotChange(string documentName, string what) =>
        new($"'{documentName}' is kept by the archive itself and cannot be {what}: it is where an administrator "
            + "finds every user's folder, and the archive keeps it in place and up to date.");

    public static CoreOwnedFolderProtectedException ReferencesAreTheArchives(string folderName) =>
        new($"References in '{folderName}' are placed and removed by the archive alone, one per user's folder; "
            + "nothing else is filed there.");
}
