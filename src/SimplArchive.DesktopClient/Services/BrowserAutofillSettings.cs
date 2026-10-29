using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The desktop's browser-autofill choice (ADR 0978): OFF until the user switches it on. The setting lives in the
/// machine's local data rather than the roaming profile, because what it enables — a loopback endpoint and a
/// browser manifest naming this machine's executable — is a fact about THIS machine.
/// </summary>
public sealed record BrowserAutofillSettings(bool Enabled = false, string ExtensionId = "");   // "" is CS1736-required: a default cannot be string.Empty

public static class BrowserAutofillSettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        SimplArchive.Localization.PersistedNames.DesktopAppData,
        "desktop-autofill.json");

    /// <summary>Overridable so a test points at a throwaway file (the layout store's pattern).</summary>
    public static string? PathOverride { get; set; }

    private static string Path_ => PathOverride ?? FilePath;

    /// <summary>The saved choice, or "off" when there is none or it cannot be read.</summary>
    public static BrowserAutofillSettings Load()
    {
        try
        {
            return File.Exists(Path_)
                ? JsonSerializer.Deserialize<BrowserAutofillSettings>(File.ReadAllText(Path_)) ?? new BrowserAutofillSettings()
                : new BrowserAutofillSettings();
        }
        catch
        {
            // Best-effort, as the layout store is: a corrupt file means "not enabled", which is the safe answer.
            return new BrowserAutofillSettings();
        }
    }

    public static void Save(BrowserAutofillSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(settings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The choice is a convenience; failing to persist it must not take the window down. The session keeps
            // whatever was decided until the app restarts.
        }
    }
}
