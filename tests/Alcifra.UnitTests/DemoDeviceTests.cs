using Alcifra.Presentation;

namespace Alcifra.UnitTests;

// The demo's profile for an iPhone or iPad (owner, 2026-11-02): built on the client from the device list, by ONE rule
// for the web, the desktop and the phone, with the password the seed put into the demo device's name (ADR 1030).
public class DemoDeviceTests
{
    private const string Password = "SimplDemo2026!";

    private static string? Build(params (string Kind, string Name, bool Mail, bool Calendars, bool Contacts)[] devices) =>
        DemoDevice.Profile(devices, "anna@demo.alcifra.dev", imapAvailable: true, "demo.alcifra.dev", 143, 993, "demo.alcifra.dev",
            "https://demo.alcifra.dev/caldav", "Alcifra");

    [Fact]
    public void The_seed_writes_the_name_the_clients_read_the_password_from()
    {
        Assert.True(DemoDevice.TryPassword(DemoDevice.Name(Password), out var password));
        Assert.Equal(Password, password);
        Assert.False(DemoDevice.TryPassword("Laptop", out _));
        Assert.False(DemoDevice.TryPassword("Demo device (password )", out _));
    }

    [Fact]
    public void The_profile_sets_up_mail_calendars_and_contacts_with_the_demo_password()
    {
        var profile = Build(("seeded", DemoDevice.Name(Password), true, true, true))!;

        Assert.Contains("com.apple.mail.managed", profile, StringComparison.Ordinal);
        Assert.Contains("com.apple.caldav.account", profile, StringComparison.Ordinal);
        Assert.Contains("com.apple.carddav.account", profile, StringComparison.Ordinal);
        Assert.Contains($"<string>{Password}</string>", profile, StringComparison.Ordinal);
        Assert.Contains("<integer>993</integer>", profile, StringComparison.Ordinal);   // the TLS port where there is one
        Assert.Equal(profile, Build(("seeded", DemoDevice.Name(Password), true, true, true)));   // the same ids: a reinstall replaces it
    }

    [Fact]
    public void Without_the_demo_device_there_is_no_profile_and_no_menu_item()
    {
        Assert.Null(Build(("enrolled", "Laptop", true, true, true), ("earlierDav", "Earlier WebDAV/calendar password", false, true, true)));
        Assert.Null(Build(("seeded", "A seeded device with another name", true, true, true)));
    }
}
