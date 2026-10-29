using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Alcifra.BrowserHost;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Sapass.Commands;

public sealed class BrowserInstallSettings : CommandSettings
{
    [CommandArgument(0, "<browser>")]
    [Description("chrome or edge.")]
    public string Browser { get; init; } = string.Empty;

    [CommandOption("--extension-id <ID>")]
    [Description("The extension's id, shown by the browser when the unpacked extension is loaded (chrome://extensions).")]
    public string ExtensionId { get; init; } = string.Empty;

    [CommandOption("--apass <PATH>")]
    [Description("The apass executable the host launcher runs. Defaults to this one.")]
    public string? SapassPath { get; init; }

    public override ValidationResult Validate() => this switch
    {
        _ when Browser is not ("chrome" or "edge") => ValidationResult.Error("Supported browsers: chrome, edge."),
        _ when string.IsNullOrWhiteSpace(ExtensionId) => ValidationResult.Error(
            "Give the extension's id with --extension-id (load the unpacked extension first; chrome://extensions shows it)."),
        _ => ValidationResult.Success(),
    };
}

/// <summary>
/// Registers apass as an autofill host for Chrome or Edge: the launcher and manifest a browser's native messaging
/// needs, under its own host name, `sapass` (ADR 0978).
/// </summary>
/// <remarks>
/// The desktop registers its OWN name the same way, so both can be installed and the extension tries the desktop
/// first. The writing itself is the shared <see cref="HostRegistration"/>; this command only knows where sapass's
/// executable is and what to tell the person.
/// </remarks>
public sealed class BrowserInstallCommand(ILogger<BrowserInstallCommand> logger) : Command<BrowserInstallSettings>
{
    protected override int Execute(CommandContext context, BrowserInstallSettings settings, CancellationToken cancellationToken)
    {
        var apass = settings.SapassPath is { Length: > 0 } given ? given : Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot tell this tool's own path; pass --apass <PATH>.");

        var launcher = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sapass",
            OperatingSystem.IsWindows() ? "browser-host.cmd" : "browser-host");
        HostRegistration.WriteLauncher(launcher, apass, "__browser-host");

        var result = HostRegistration.Install(settings.Browser, "sapass", settings.ExtensionId, launcher, logger);
        logger.LogInformation("Launcher: {Launcher}", result.LauncherPath);
        logger.LogInformation("The desktop client registers its own host name; the extension asks for it first.");
        logger.LogInformation("Restart the browser, then the extension will find the host.");
        return 0;
    }
}
