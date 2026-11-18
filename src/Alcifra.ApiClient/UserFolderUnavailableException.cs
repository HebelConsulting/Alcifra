namespace Alcifra.ApiClient;

/// <summary>
/// The platform has no path for a user folder the client needs, typically because the process runs without a home
/// directory (no <c>HOME</c>). Refused rather than answered with a path relative to the current directory (#2358).
/// </summary>
public sealed class UserFolderUnavailableException : Exception
{
    public UserFolderUnavailableException(Environment.SpecialFolder folder)
        : base($"The platform has no {folder} folder for this user (is HOME set?), so nothing is written relative to the current directory instead.") =>
        Folder = folder;

    /// <summary>Which folder was asked for.</summary>
    public Environment.SpecialFolder Folder { get; }
}
