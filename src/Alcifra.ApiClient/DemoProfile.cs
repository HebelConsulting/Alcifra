using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.ApiClient;

/// <summary>The demo's configuration profile from a device list (owner, 2026-11-02): <see cref="DemoDevice.Profile"/>'s
/// one rule, fed with the client library's types. Null where the user has no demo device.</summary>
public static class DemoProfile
{
    public static byte[]? TryBuild(DeviceList devices)
    {
        var s = devices.Servers;
        return DemoDevice.Profile(devices.Items.Select(d => (d.Kind, d.Name, d.Mail, d.Calendars, d.Contacts)), s.Username,
            s.ImapAvailable, s.ImapHost, s.ImapPort, s.ImapTlsPort, s.OutgoingHost, s.CalDavUrl, Strings.ProductName) is { } profile
            ? System.Text.Encoding.UTF8.GetBytes(profile)
            : null;
    }
}
