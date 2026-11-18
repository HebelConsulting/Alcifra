namespace Alcifra.ApiClient;

/// <summary>
/// The user's platform folders (data, local data, home) for every client that keeps files there: the desktop, the
/// command-line tools and the browser host. The one place that asks the platform (#2358).
/// </summary>
/// <remarks>
/// <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> on its own answers an EMPTY string for a folder
/// that does not exist yet: a fresh Linux account has no <c>~/.config</c> until something creates it. Every path built
/// on that answer was then relative to the current directory, so the command-line session file, which carries tokens,
/// could land in whatever folder a command was run from. Asking with <see cref="Environment.SpecialFolderOption.DoNotVerify"/>
/// gives the path whether or not the folder exists (callers create their own sub-folder when they write); an answer
/// that is STILL empty means the platform has none to give, and is refused rather than turned into a relative path.
/// </remarks>
public static class UserFolders
{
    /// <summary>The absolute path of <paramref name="folder"/>, whether or not it exists yet.</summary>
    /// <exception cref="UserFolderUnavailableException">The platform has no such folder for this user.</exception>
    public static string Get(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } path
            ? path
            : throw new UserFolderUnavailableException(folder);
}
