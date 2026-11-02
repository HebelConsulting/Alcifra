using System.Security;
using System.Text;

namespace Alcifra.Presentation;

/// <summary>Where a device reaches the installation, as a configuration profile names it.</summary>
/// <param name="ImapHost">IMAP's published host; null when the installation offers no mail.</param>
/// <param name="ImapPort">The port a mail client uses: the TLS port where there is one.</param>
/// <param name="OutgoingHost">The outgoing server a mail account names (port 25, no sign-in).</param>
/// <param name="WebHost">The web address's host, which CalDAV and CardDAV share.</param>
public sealed record ProfileServers(string? ImapHost, int ImapPort, bool ImapTls, string OutgoingHost, string WebHost, int WebPort, bool WebTls);

/// <summary>
/// The configuration profile that sets up a device's mail, calendars and contacts in one tap (#1970, owner 2026-10-29),
/// built in ONE place for the server's hand-over and the clients' demo profile (owner, 2026-11-02):
/// one account per protocol the device's switches open. Files (WebDAV) has no profile payload; it is shown as an
/// address instead.
/// </summary>
/// <remarks>
/// The password is IN the profile, which is why it is only ever stored sealed and served once. Every value is XML
/// escaped: a device or user name is the user's own text.
/// </remarks>
public static class ConfigurationProfile
{
    public const string ContentType = "application/x-apple-aspen-config";

    /// <param name="ingestCertificatePem">
    /// The tenant's mail-in certificate (ADR 0820), carried where the device reaches mail, so composing TO the archive's
    /// ingest address offers encryption with no setup: iOS consults profile-installed certificates for S/MIME recipient
    /// lookup. It rode the retired self-service identity's profile before (#1970 slice 4).
    /// </param>
    /// <param name="productName">The product's name as the reader sees it, which titles the accounts.</param>
    public static string Build(
        Guid deviceId, string deviceName, string productName, string email, string password, bool mail, bool calendars, bool contacts,
        ProfileServers servers, string? ingestCertificatePem = null)
    {
        var payloads = new StringBuilder();
        if (mail && servers.ImapHost is not null)
        {
            payloads.Append(Payload("com.apple.mail.managed", deviceId, "mail", $"{deviceName}: mail", [
                ("EmailAccountType", "string", "EmailTypeIMAP"),
                ("EmailAccountDescription", "string", $"{productName} ({deviceName})"),
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

        if (mail && servers.ImapHost is not null && ingestCertificatePem is { Length: > 0 })
        {
            using var ingest = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(ingestCertificatePem);
            payloads.Append($"""

                    <dict>
                        <key>PayloadContent</key>
                        <data>{Convert.ToBase64String(ingest.RawData)}</data>
                        <key>PayloadDisplayName</key><string>{Escape($"{productName} mail-in certificate")}</string>
                        <key>PayloadIdentifier</key><string>{WireNames.DeviceProfileIdentifierPrefix}.{deviceId}.ingest</string>
                        <key>PayloadType</key><string>com.apple.security.root</string>
                        <key>PayloadUUID</key><string>{Uuid(deviceId, "ingest")}</string>
                        <key>PayloadVersion</key><integer>1</integer>
                    </dict>
            """);
        }

        if (calendars)
        {
            payloads.Append(Payload("com.apple.caldav.account", deviceId, "caldav", $"{deviceName}: calendars", [
                ("CalDAVAccountDescription", "string", $"{productName} ({deviceName})"),
                ("CalDAVHostName", "string", servers.WebHost),
                ("CalDAVPort", "integer", servers.WebPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("CalDAVUseSSL", servers.WebTls ? "true" : "false", string.Empty),
                ("CalDAVUsername", "string", email),
                ("CalDAVPassword", "string", password),
            ]));
        }

        if (contacts)
        {
            payloads.Append(Payload("com.apple.carddav.account", deviceId, "carddav", $"{deviceName}: contacts", [
                ("CardDAVAccountDescription", "string", $"{productName} ({deviceName})"),
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
                <key>PayloadDisplayName</key><string>{Escape($"{productName}: {deviceName}")}</string>
                <key>PayloadIdentifier</key><string>{WireNames.DeviceProfileIdentifierPrefix}.{deviceId}</string>
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
                        <key>PayloadIdentifier</key><string>{WireNames.DeviceProfileIdentifierPrefix}.{deviceId}.{part}</string>
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
