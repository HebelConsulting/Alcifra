using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Alcifra.BrowserHost;

/// <summary>Where a host was registered, for the sentence the user reads.</summary>
public sealed record HostRegistrationResult(string Browser, string ManifestPath, string LauncherPath);

/// <summary>
/// Registers a client as a browser's native-messaging host: the launcher the browser spawns and the manifest that
/// names it (ADR 0978).
/// </summary>
/// <remarks>
/// <para>
/// <b>Chrome starts the manifest's path with NO arguments</b>, so the client's own executable cannot be named
/// directly — a one-line launcher is what reaches the host verb (or the shim).
/// </para>
/// <para>
/// <b>The extension id must be exact</b>: the manifest lists the one origin allowed to talk to the host. Each client
/// uses its OWN host name, so both can be installed and neither repoints the other.
/// </para>
/// <para>
/// <b>On Windows a .reg file is written rather than a registry key</b>: the manifest path is registered under HKCU,
/// and writing it here would need the registry package on a cross-platform tool. One documented step replaces it.
/// </para>
/// </remarks>
public static class HostRegistration
{
    /// <summary>The browsers this registers for. Safari needs a containing app and a Swift handler (not this path).</summary>
    public static readonly IReadOnlyList<string> Browsers = ["chrome", "edge"];

    /// <summary>The host name a client's manifest declares and the extension asks for.</summary>
    public static string HostNameFor(string clientName) => $"com.alcifra.autofill.{clientName}";

    /// <summary>Writes the launcher and the manifest for one browser, returning where they went.</summary>
    /// <param name="launcher">The executable the browser spawns; it must reach the host verb with no arguments.</param>
    public static HostRegistrationResult Install(
        string browser, string clientName, string extensionId, string launcher, ILogger logger)
    {
        var hostName = HostNameFor(clientName);
        var json = Manifest(clientName, extensionId, launcher);

        if (OperatingSystem.IsWindows())
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sapass");
            Directory.CreateDirectory(folder);
            var manifestPath = Path.Combine(folder, $"{hostName}.json");
            var reg = Path.Combine(folder, $"{hostName}.reg");
            File.WriteAllText(manifestPath, json);
            File.WriteAllText(reg,
                "Windows Registry Editor Version 5.00\r\n\r\n"
                + $"[HKEY_CURRENT_USER\\Software\\{RegistryKey(browser)}\\{hostName}]\r\n"
                + $"@=\"{manifestPath.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\r\n");
            logger.LogInformation("Wrote {Reg}. Run it once to register the host, or import it with regedit.", reg);
            return new HostRegistrationResult(browser, manifestPath, launcher);
        }

        var target = Path.Combine(NativeMessagingHostsFolder(browser), $"{hostName}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, json);
        logger.LogInformation("Registered the {Client} autofill host for {Browser}: {Manifest}", clientName, browser, target);
        return new HostRegistrationResult(browser, target, launcher);
    }

    /// <summary>
    /// The native-messaging manifest for one client: its host name, the launcher the browser spawns, and the ONE
    /// extension origin allowed to talk to it. Public so the extension's end-to-end test registers exactly what an
    /// installation registers, into a throwaway browser profile.
    /// </summary>
    public static string Manifest(string clientName, string extensionId, string launcher) => JsonSerializer.Serialize(
        new
        {
            name = HostNameFor(clientName),
            description = $"{Alcifra.Localization.Strings.ProductName} password store autofill ({clientName})",
            path = launcher,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{extensionId}/" },
        },
        new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Writes the launcher a shim needs: a script that runs <paramref name="command"/> with no arguments.</summary>
    /// <param name="path">Where the launcher lives; the manifest names this path.</param>
    /// <param name="command">The executable, and the verb, as the shell would type it.</param>
    public static string WriteLauncher(string path, string executable, string verb)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, OperatingSystem.IsWindows()
            ? $"@echo off\r\n\"{executable.Replace("%", "%%", StringComparison.Ordinal)}\" {verb} %*\r\n"
            : $"#!/bin/sh\nexec '{executable.Replace("'", "'\\''", StringComparison.Ordinal)}' {verb}\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private static string NativeMessagingHostsFolder(string browser) => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support",
            browser == "edge" ? "Microsoft Edge" : "Google Chrome", "NativeMessagingHosts")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config",
            browser == "edge" ? "microsoft-edge" : "google-chrome", "NativeMessagingHosts");

    private static string RegistryKey(string browser) =>
        browser == "edge" ? "Microsoft\\Edge\\NativeMessagingHosts" : "Google\\Chrome\\NativeMessagingHosts";
}
