namespace Alcifra.Api.ProtocolDevices;

/// <summary>Where a device reaches the installation: what the Devices view, a profile and the hand-over name (#1970).</summary>
/// <param name="ImapHost">IMAP's published host; null when IMAP is off.</param>
/// <param name="ImapPort">Its published plain port, if any.</param>
/// <param name="ImapTlsPort">Its published TLS port, if any; a profile prefers it.</param>
/// <param name="OutgoingHost">The outgoing server a mail account names: the installation's own MX, port 25, without a
/// sign-in, until authenticated submission exists (#2046; owner, 2026-10-31).</param>
/// <param name="WebHost">The web address's host, which CalDAV, CardDAV and WebDAV share.</param>
/// <param name="WebPort">Its port.</param>
/// <param name="WebTls">Whether it is HTTPS.</param>
public sealed record DeviceServers(
    string? ImapHost, int? ImapPort, int? ImapTlsPort, string OutgoingHost, string WebHost, int WebPort, bool WebTls)
{
    /// <summary>The installation's published addresses: <c>App:BaseUrl</c> for the web protocols, IMAP's published host
    /// and ports (null host when IMAP is off), and <c>Mail:OutgoingHost</c>, else IMAP's host.</summary>
    /// <param name="listening">
    /// The running IMAP endpoint: a port configured as -1 is chosen by the OS at start (test hosts), and what a user is
    /// shown is the port it actually bound, never the -1.
    /// </param>
    public static DeviceServers From(IConfiguration configuration, Alcifra.Api.Imap.ImapOptions imap, Alcifra.Api.Imap.ImapServer? listening = null)
    {
        var web = new Uri(configuration["App:BaseUrl"] is { Length: > 0 } baseUrl ? baseUrl : "http://localhost:8080");
        var imapHost = imap.Enabled ? imap.PublicHost ?? web.Host : null;
        return new DeviceServers(
            imapHost,
            imap.Enabled ? Bound(imap.AdvertisedPort, listening?.BoundPort) : null,
            imap.Enabled ? Bound(imap.AdvertisedTlsPort, listening?.BoundTlsPort) : null,
            configuration["Mail:OutgoingHost"] is { Length: > 0 } outgoing ? outgoing : imapHost ?? web.Host,
            web.Host,
            web.Port,
            web.Scheme == Uri.UriSchemeHttps);
    }

    // -1 is "ephemeral": the bound port where the endpoint is listening, else nothing to show.
    private static int? Bound(int? advertised, int? bound) => advertised == -1 ? bound : advertised;

    /// <summary>The web address's origin, which the DAV addresses hang off.</summary>
    public string WebOrigin => $"{(WebTls ? "https" : "http")}://{WebHost}{(WebPort is 443 or 80 ? string.Empty : $":{WebPort}")}";

    /// <summary>The port a mail client should use, and whether it is TLS: the TLS port when there is one.</summary>
    public (int Port, bool Tls) MailPort => ImapTlsPort is { } tls ? (tls, true) : (ImapPort ?? 993, false);
}

/// <summary>
/// The configuration profile of an enrolled device (#1970): the shared builder (<see cref="Alcifra.Presentation.ConfigurationProfile"/>)
/// fed with the server's addresses and the product's name.
/// </summary>
public static class DeviceProfile
{
    /// <summary>The <c>Content-Type</c> a configuration profile is served with.</summary>
    public const string ContentType = Alcifra.Presentation.ConfigurationProfile.ContentType;

    /// <inheritdoc cref="Alcifra.Presentation.ConfigurationProfile.Build"/>
    public static string Build(
        Guid deviceId, string deviceName, string email, string password, DeviceSwitches switches, DeviceServers servers,
        string? ingestCertificatePem = null) =>
        Alcifra.Presentation.ConfigurationProfile.Build(
            deviceId, deviceName, Alcifra.Localization.Strings.ProductName, email, password, switches.Mail, switches.Calendars, switches.Contacts,
            new Alcifra.Presentation.ProfileServers(servers.ImapHost, servers.MailPort.Port, servers.MailPort.Tls, servers.OutgoingHost,
                servers.WebHost, servers.WebPort, servers.WebTls),
            ingestCertificatePem);
}
