namespace Alcifra.Presentation;

/// <summary>
/// The demo seed's one device and its name (ADR 1030), which carries the public demo password so a visitor sees which
/// password it takes, and so a client can build the demo's configuration profile locally (owner, 2026-11-02). The seed
/// writes the name with <see cref="Name"/> and the clients read it with <see cref="TryPassword"/>: one contract.
/// </summary>
public static class DemoDevice
{
    private const string Prefix = "Demo device (password ";
    private const string Suffix = ")";

    /// <summary>The device's name for <paramref name="password"/>.</summary>
    public static string Name(string password) => $"{Prefix}{password}{Suffix}";

    /// <summary>The password a demo device's name carries; false for any other name.</summary>
    public static bool TryPassword(string name, out string password)
    {
        password = string.Empty;
        if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !name.EndsWith(Suffix, StringComparison.Ordinal)
            || name.Length <= Prefix.Length + Suffix.Length)
        {
            return false;
        }

        password = name[Prefix.Length..^Suffix.Length];
        return true;
    }

    /// <summary>The file name the demo's profile is offered under.</summary>
    public const string ProfileFileName = "Demo.mobileconfig";

    /// <summary>
    /// The demo's configuration profile for an iPhone or iPad, built on the client without the server (owner, 2026-11-02),
    /// from what the device list already says: the seeded device (its name carries the password) and the addresses. Null
    /// where the user has no demo device, so the menu item is not shown. ONE rule for the web, the desktop and the phone.
    /// </summary>
    /// <param name="devices">Each device's wire kind, name and the switches a profile covers.</param>
    /// <param name="productName">Titles the accounts, as the server's hand-over does.</param>
    public static string? Profile(
        IEnumerable<(string Kind, string Name, bool Mail, bool Calendars, bool Contacts)> devices, string username,
        bool imapAvailable, string? imapHost, int? imapPort, int? imapTlsPort, string outgoingHost, string calDavUrl, string productName)
    {
        var device = devices.FirstOrDefault(d => d.Kind == "seeded");
        if (device.Name is null || !TryPassword(device.Name, out var password) || !Uri.TryCreate(calDavUrl, UriKind.Absolute, out var web))
        {
            return null;
        }

        // The same ids every time for the same login, so installing it again replaces the earlier one.
        var id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"demo-profile:{username}"))[..16]);
        return ConfigurationProfile.Build(
            id, device.Name, productName, username, password,
            mail: device.Mail && imapAvailable && imapHost is not null, device.Calendars, device.Contacts,
            new ProfileServers(imapAvailable ? imapHost : null, imapTlsPort ?? imapPort ?? 993, imapTlsPort is not null, outgoingHost,
                web.Host, web.Port, web.Scheme == Uri.UriSchemeHttps));
    }
}
