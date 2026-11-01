using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Alcifra.Cli.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Cli.Commands;

public sealed class DeviceNameSettings : TenantSessionSettings
{
    [CommandArgument(0, "<name>")]
    [Description("The device's name, as the list shows it.")]
    public string Name { get; init; } = string.Empty;

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when string.IsNullOrWhiteSpace(Name) => ValidationResult.Error("Name the device: alcifra me device <verb> <name>."),
        _ => ValidationResult.Success(),
    };
}

public sealed class DeviceEnrolSettings : TenantSessionSettings
{
    [CommandArgument(0, "<name>")]
    [Description("A name for the device, unique among yours (e.g. 'Laptop').")]
    public string Name { get; init; } = string.Empty;

    [CommandOption("--no-mail")]
    [Description("Do not let this device reach mail (IMAP).")]
    public bool NoMail { get; init; }

    [CommandOption("--no-files")]
    [Description("Do not let this device reach files (WebDAV).")]
    public bool NoFiles { get; init; }

    [CommandOption("--no-calendars")]
    [Description("Do not let this device reach calendars (CalDAV).")]
    public bool NoCalendars { get; init; }

    [CommandOption("--no-contacts")]
    [Description("Do not let this device reach contacts (CardDAV).")]
    public bool NoContacts { get; init; }

    [CommandOption("--encrypt-mail")]
    [Description("Seal this device's mail to your hardware keys. Refused, with the reason, without an enrolled hardware key.")]
    public bool EncryptMail { get; init; }

    public override ValidationResult Validate() => this switch
    {
        _ when base.Validate() is { Successful: false } failed => failed,
        _ when string.IsNullOrWhiteSpace(Name) => ValidationResult.Error("Name the device: alcifra me device enrol <name>."),
        _ => ValidationResult.Success(),
    };
}

/// <summary>Lists the signed-in user's protocol devices (#1970), through <c>me</c>'s <c>protocolDevices</c> rel.</summary>
public sealed class DeviceListCommand(ILogger<DeviceListCommand> logger) : AsyncCommand<TenantSessionSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, TenantSessionSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new AlcifraApi(http);
        foreach (var device in (await DeviceSurface.ReadAsync(api, cancellationToken)).GetProperty("items").EnumerateArray())
        {
            logger.LogInformation("{Name} ({Kind}): {Protocols}; mail {Encryption}; last used {LastUsed}",
                device.GetProperty("name").GetString(), device.GetProperty("kind").GetString(), DeviceSurface.Protocols(device),
                device.GetProperty("encryptMail").GetBoolean() ? "encrypted" : "not encrypted",
                device.GetProperty("lastUsedAt").ValueKind == JsonValueKind.Null ? "never" : device.GetProperty("lastUsedAt").GetString());
        }

        return 0;
    }
}

/// <summary>
/// Enrols a device for the signed-in user (#1970) and prints its password ONCE on stdout, the tool's data output (ADR
/// 0906), so <c>pw=$(alcifra me device enrol Laptop)</c> captures it. Everything else goes to the log.
/// </summary>
public sealed class DeviceEnrolCommand(ILogger<DeviceEnrolCommand> logger) : AsyncCommand<DeviceEnrolSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DeviceEnrolSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new AlcifraApi(http);
        var enrolled = await api.PostAsync(await DeviceSurface.HrefAsync(api, cancellationToken), new
        {
            name = settings.Name,
            mail = !settings.NoMail,
            files = !settings.NoFiles,
            calendars = !settings.NoCalendars,
            contacts = !settings.NoContacts,
            encryptMail = settings.EncryptMail,
        }, cancellationToken);

        logger.LogInformation("Enrolled {Name} for {Username}: {Protocols}. The password below is shown once.",
            enrolled.GetProperty("name").GetString(), enrolled.GetProperty("username").GetString(), DeviceSurface.Protocols(enrolled));

        // Data output (ADR 0906): the device's password, its one and only sighting; only its hash is stored. Named, so
        // the secret-output guard sees it and its carve-out names a real site.
        var password = enrolled.GetProperty("password").GetString();
        Console.Out.WriteLine(password);
        return 0;
    }
}

/// <summary>Revokes one of the signed-in user's devices (#1970), with the device's ETag as <c>If-Match</c>.</summary>
public sealed class DeviceRevokeCommand(ILogger<DeviceRevokeCommand> logger) : AsyncCommand<DeviceNameSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, DeviceNameSettings settings, CancellationToken cancellationToken)
    {
        using var http = CertificateEndpoint.Client(settings);
        var api = new AlcifraApi(http);
        if (DeviceSurface.Find(await DeviceSurface.ReadAsync(api, cancellationToken), settings.Name) is not { } device)
        {
            logger.LogInformation("You have no device named {Name}; nothing to revoke.", settings.Name);
            return 0;
        }

        await api.DeleteWithETagAsync(Hypermedia.Href(Hypermedia.LinksOf(device), "self", settings.Name),
            device.GetProperty("eTag").GetString(), cancellationToken);
        logger.LogInformation("Revoked {Name}: whatever signed in with its password stops at its next request.", settings.Name);
        return 0;
    }
}

internal static class DeviceSurface
{
    /// <summary>Root → <c>me</c> → <c>protocolDevices</c>, followed, never composed (ADR 0543).</summary>
    internal static async Task<string> HrefAsync(AlcifraApi api, CancellationToken cancellationToken)
    {
        var hypermedia = new Hypermedia(api);
        var me = await hypermedia.RootHrefAsync("me", cancellationToken);
        return Hypermedia.Href(await hypermedia.LinksOfAsync(me, cancellationToken), "protocolDevices", "This installation");
    }

    internal static async Task<JsonElement> ReadAsync(AlcifraApi api, CancellationToken cancellationToken) =>
        await api.GetAsync(await HrefAsync(api, cancellationToken), cancellationToken);

    internal static JsonElement? Find(JsonElement devices, string name) =>
        devices.GetProperty("items").EnumerateArray()
            .Where(d => string.Equals(d.GetProperty("name").GetString(), name.Trim(), StringComparison.Ordinal))
            .Select(d => (JsonElement?)d)
            .FirstOrDefault();

    internal static string Protocols(JsonElement device) =>
        string.Join(", ", new[] { ("mail", "mail"), ("files", "files"), ("calendars", "calendars"), ("contacts", "contacts") }
            .Where(p => device.GetProperty(p.Item1).GetBoolean()).Select(p => p.Item2)) is { Length: > 0 } list ? list : "no protocols";
}
