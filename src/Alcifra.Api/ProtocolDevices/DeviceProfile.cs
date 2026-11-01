using System.Security;
using System.Text;

namespace Alcifra.Api.ProtocolDevices;

/// <summary>Where a device reaches the installation: what a profile and the hand-over name (#1970 slice 2).</summary>
/// <param name="ImapHost">IMAP's published host.</param>
/// <param name="ImapPort">Its published port: the TLS port when there is one.</param>
/// <param name="ImapTls">Whether that port is TLS.</param>
/// <param name="OutgoingHost">The outgoing server a mail account names: the installation's own MX, port 25, without a
/// sign-in, until authenticated submission exists (#2046; owner, 2026-10-31).</param>
/// <param name="WebHost">The web address's host, which CalDAV, CardDAV and WebDAV share.</param>
/// <param name="WebPort">Its port.</param>
/// <param name="WebTls">Whether it is HTTPS.</param>
public sealed record DeviceServers(
    string? ImapHost, int ImapPort, bool ImapTls, string OutgoingHost, string WebHost, int WebPort, bool WebTls)
{
    /// <summary>The installation's published addresses: <c>App:BaseUrl</c> for the web protocols, IMAP's published host
    /// and ports (null host when IMAP is off), and <c>Mail:OutgoingHost</c>, else IMAP's host.</summary>
    public static DeviceServers From(IConfiguration configuration, Alcifra.Api.Imap.ImapOptions imap)
    {
        var web = new Uri(configuration["App:BaseUrl"] is { Length: > 0 } baseUrl ? baseUrl : "http://localhost:8080");
        var imapHost = imap.Enabled ? imap.PublicHost ?? web.Host : null;
        return new DeviceServers(
            imapHost,
            imap.AdvertisedTlsPort ?? imap.AdvertisedPort ?? 993,
            imap.AdvertisedTlsPort is not null,
            configuration["Mail:OutgoingHost"] is { Length: > 0 } outgoing ? outgoing : imapHost ?? web.Host,
            web.Host,
            web.Port,
            web.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>The web address's origin, which the DAV addresses hang off.</summary>
    public string WebOrigin => $"{(WebTls ? "https" : "http")}://{WebHost}{(WebPort is 443 or 80 ? string.Empty : $":{WebPort}")}";
}

/// <summary>
/// The configuration profile that sets up a device's mail, calendars and contacts in one tap (#1970, owner 2026-10-29):
/// one account per protocol the device's switches open. Files (WebDAV) has no profile payload; it is shown as an
/// address instead.
/// </summary>
/// <remarks>
/// The password is IN the profile, which is why it is only ever stored sealed and served once. Every value is XML
/// escaped: a device or user name is the user's own text.
/// </remarks>
public static class DeviceProfile
{
    public const string ContentType = "application/x-apple-aspen-config";

    public static string Build(
        Guid deviceId, string deviceName, string email, string password, DeviceSwitches switches, DeviceServers servers)
    {
        var payloads = new StringBuilder();
        if (switches.Mail && servers.ImapHost is not null)
        {
            payloads.Append(Payload("com.apple.mail.managed", deviceId, "mail", $"{deviceName}: mail", [
                ("EmailAccountType", "string", "EmailTypeIMAP"),
                ("EmailAccountDescription", "string", $"{Alcifra.Localization.Strings.ProductName} ({deviceName})"),
                ("EmailAddress", "string", email),
                ("IncomingMailServerHostName", "string", servers.ImapHost),
                ("IncomingMailServerPortNumber", "integer", servers.ImapPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("IncomingMailServerUseSSL", servers.ImapTls ? "true" : "false", string.Empty),
                ("IncomingMailServerAuthentication", "string", "EmailAuthPassword"),
                ("IncomingMailServerUsername", "string", email),
                ("IncomingPassword", "string", password),
                ("OutgoingMailServerHostName", "string", servers.OutgoingHost),
                ("OutgoingMailServerPortNumber", "integer", "25"),
                ("OutgoingMailServerUseSSL", "false", string.Empty),
                ("OutgoingMailServerAuthentication", "string", "EmailAuthNone"),
                ("OutgoingMailServerUsername", "string", email),
            ]));
        }

        if (switches.Calendars)
        {
            payloads.Append(Payload("com.apple.caldav.account", deviceId, "caldav", $"{deviceName}: calendars", [
                ("CalDAVAccountDescription", "string", $"{Alcifra.Localization.Strings.ProductName} ({deviceName})"),
                ("CalDAVHostName", "string", servers.WebHost),
                ("CalDAVPort", "integer", servers.WebPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("CalDAVUseSSL", servers.WebTls ? "true" : "false", string.Empty),
                ("CalDAVUsername", "string", email),
                ("CalDAVPassword", "string", password),
            ]));
        }

        if (switches.Contacts)
        {
            payloads.Append(Payload("com.apple.carddav.account", deviceId, "carddav", $"{deviceName}: contacts", [
                ("CardDAVAccountDescription", "string", $"{Alcifra.Localization.Strings.ProductName} ({deviceName})"),
                ("CardDAVHostName", "string", servers.WebHost),
                ("CardDAVPort", "integer", servers.WebPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("CardDAVUseSSL", servers.WebTls ? "true" : "false", string.Empty),
                ("CardDAVUsername", "string", email),
                ("CardDAVPassword", "string", password),
            ]));
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>PayloadContent</key>
                <array>{payloads}</array>
                <key>PayloadDisplayName</key><string>{Escape($"{Alcifra.Localization.Strings.ProductName}: {deviceName}")}</string>
                <key>PayloadIdentifier</key><string>{Alcifra.Presentation.WireNames.DeviceProfileIdentifierPrefix}.{deviceId}</string>
                <key>PayloadType</key><string>Configuration</string>
                <key>PayloadUUID</key><string>{Uuid(deviceId, "profile")}</string>
                <key>PayloadVersion</key><integer>1</integer>
            </dict>
            </plist>
            """;
    }

    // A value is (key, type, text): type "string" or "integer" carries the text; "true"/"false" is a boolean element.
    private static string Payload(string type, Guid deviceId, string part, string displayName, (string Key, string Type, string Value)[] values)
    {
        var body = new StringBuilder();
        foreach (var (key, kind, value) in values)
        {
            body.Append($"<key>{key}</key>");
            body.Append(kind is "true" or "false" ? $"<{kind}/>" : $"<{kind}>{Escape(value)}</{kind}>");
        }

        return $"""

                    <dict>{body}
                        <key>PayloadDisplayName</key><string>{Escape(displayName)}</string>
                        <key>PayloadIdentifier</key><string>{Alcifra.Presentation.WireNames.DeviceProfileIdentifierPrefix}.{deviceId}.{part}</string>
                        <key>PayloadType</key><string>{type}</string>
                        <key>PayloadUUID</key><string>{Uuid(deviceId, part)}</string>
                        <key>PayloadVersion</key><integer>1</integer>
                    </dict>
            """;
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;

    // Stable per device and part, so installing a device's profile again replaces it rather than adding a second.
    private static string Uuid(Guid deviceId, string part) =>
        new Guid(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{deviceId}:{part}"))[..16]).ToString().ToUpperInvariant();
}
