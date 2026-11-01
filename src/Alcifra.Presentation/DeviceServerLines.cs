using System.Globalization;

namespace Alcifra.Presentation;

/// <summary>One line of a device's server settings: a label, what it shows, what Copy puts on the clipboard (null: no Copy).</summary>
public sealed record DeviceServerLine(string Label, string Value, string? Copy, string? Note = null);

/// <summary>
/// The server settings a device is set up with, line by line, as the Devices view and the hand-over show them on every
/// client (#1970 slice 3; ADRs 0650/0651). The text lookup is the caller's (<paramref name="text"/> in
/// <see cref="Of"/>), since this leaf holds no languages.
/// </summary>
public static class DeviceServerLines
{
    public static IReadOnlyList<DeviceServerLine> Of(
        string username, bool imapAvailable, string? imapHost, int? imapPort, int? imapTlsPort, string outgoingHost,
        string webDavUrl, string calDavUrl, string cardDavUrl, Func<string, string> text)
    {
        var lines = new List<DeviceServerLine> { new(text("DvUsername"), username, username) };
        if (imapAvailable && imapHost is { } host)
        {
            var ports = string.Join(", ", new[]
            {
                imapTlsPort is { } tls ? $"{tls} ({text("DvImapTls")})" : null,
                imapPort?.ToString(CultureInfo.InvariantCulture),
            }.Where(p => p is not null));
            lines.Add(new(text("DvImap"), string.Format(CultureInfo.CurrentCulture, text("DvImapPorts"), host, ports), host));
            lines.Add(new(text("DvOutgoing"), outgoingHost, outgoingHost, text("DvOutgoingNote")));
        }
        else
        {
            lines.Add(new(text("DvImap"), text("DvImapOff"), null));
        }

        lines.Add(new(text("DvWebDav"), webDavUrl, webDavUrl));
        lines.Add(new(text("DvCalDav"), calDavUrl, calDavUrl));
        lines.Add(new(text("DvCardDav"), cardDavUrl, cardDavUrl));
        return lines;
    }
}
