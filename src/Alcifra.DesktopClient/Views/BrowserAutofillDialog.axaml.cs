using Avalonia.Controls;
using Avalonia.Interactivity;
using Alcifra.BrowserHost;
using Alcifra.DesktopClient.Services;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// Turns the desktop's browser autofill on or off (ADR 0978): the extension's id, which the manifest must name
/// exactly, and the switch itself.
/// </summary>
/// <remarks>
/// Saving writes the launcher and the manifest for both Chrome and Edge, then stores the choice; the caller applies
/// it, because only the shell knows which session the bridge would speak for. The dialog deliberately does not
/// promise the bridge is running — that depends on sign-in — and says so.
/// </remarks>
public partial class BrowserAutofillDialog : Window
{
    public BrowserAutofillDialog()
    {
        InitializeComponent();

        var settings = BrowserAutofillSettingsStore.Load();
        EnabledBox.IsChecked = settings.Enabled;
        ExtensionBox.Text = settings.ExtensionId;
    }

    /// <summary>Whether the user saved a change: the caller re-applies the setting to the session.</summary>
    public bool Saved { get; private set; }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var enabled = EnabledBox.IsChecked == true;
        var extensionId = (ExtensionBox.Text ?? string.Empty).Trim();

        if (enabled && extensionId.Length == 0)
        {
            ExtensionBox.Watermark = "The extension id is required when autofill is on.";
            return;
        }

        if (enabled)
        {
            var launcher = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Alcifra.Localization.PersistedNames.DesktopAppData,
                OperatingSystem.IsWindows() ? "browser-host.cmd" : "browser-host");

            // Chrome starts a manifest's path with NO arguments, so the launcher carries the verb (ADR 0978).
            HostRegistration.WriteLauncher(launcher, Environment.ProcessPath ?? string.Empty, "--browser-host-shim");
            foreach (var browser in HostRegistration.Browsers)
            {
                HostRegistration.Install(browser, BrowserAutofill.ClientName, extensionId, launcher, DesktopLog.Logger);
            }
        }

        BrowserAutofillSettingsStore.Save(new BrowserAutofillSettings(enabled, extensionId));
        Saved = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
