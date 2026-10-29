using SimplArchive.Theming;

namespace SimplArchive.DesktopClient.Services;

// The desktop client's per-user local temp folder (ADR "S3-backed inbox", phase 2; ADR 0513):
// `~/{FileStem}/{TenantName}/{UserName}/temp` (macOS/Linux) or `%AppData%\SimplArchive\{TenantName}\{UserName}\temp`
// (Windows). The home folder is one a user sees, so it carries the brand's file stem; the one under AppData is
// hidden and stays an identifier (ADR 0997). `temp` backs native-open — download a file there, then open it in its OS app (NativeFileOpener).
// The former local `intray`/`checkout` working-copy folders were retired when those flows moved to WebDAV (ADR 0513).
public sealed class LocalFolders
{
    public LocalFolders(string tenantName, string userName)
    {
        var basePath = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), SimplArchive.Localization.PersistedNames.DesktopAppData)
            : HomeFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Brand.Shipped.FileStem);

        Root = Path.Combine(basePath, Sanitize(tenantName), Sanitize(userName));
        TempDirectory = Path.Combine(Root, "temp");

        Directory.CreateDirectory(TempDirectory);
    }

    public string Root { get; }

    public string TempDirectory { get; }

    /// <summary>
    /// The brand's folder in the home folder. A <c>SimplArchive</c> folder from before the name followed the brand is
    /// moved there once; it holds only temp copies, so if the move fails the new folder is simply started empty.
    /// </summary>
    internal static string HomeFolder(string home, string fileStem)
    {
        var folder = Path.Combine(home, fileStem);
        var earlier = Path.Combine(home, SimplArchive.Localization.PersistedNames.EarlierProduct);
        if (folder != earlier && Directory.Exists(earlier) && !Directory.Exists(folder))
        {
            try
            {
                Directory.Move(earlier, folder);
                DesktopLog.Info("Moved the local folder {Earlier} to {Folder}", earlier, folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DesktopLog.Warn(ex, "Could not move the local folder {Earlier} to {Folder}; starting it empty", earlier, folder);
            }
        }

        return folder;
    }

    private static string Sanitize(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string((name ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "unknown" : cleaned;
    }
}
