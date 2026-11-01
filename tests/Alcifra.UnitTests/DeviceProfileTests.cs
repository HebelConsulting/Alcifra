using System.Xml.Linq;
using Alcifra.Api.ProtocolDevices;

namespace Alcifra.UnitTests;

// #1970 slice 2: the configuration profile sets up exactly the accounts a device's switches open, carries the device's
// password, stays a valid property list whatever the user named things, and keeps its identifiers across installs.
public class DeviceProfileTests
{
    private static readonly DeviceServers Servers = new("imap.example.test", 993, true, "mail.example.test", "archive.example.test", 443, true);

    private static XDocument Parse(string profile) =>
        XDocument.Parse(profile, LoadOptions.None);

    private static List<string> PayloadTypes(XDocument plist) =>
        [.. plist.Descendants("key").Where(k => k.Value == "PayloadType").Select(k => ((XElement)k.NextNode!).Value)];

    [Fact]
    public void Every_switched_on_protocol_gets_its_account_and_files_gets_none()
    {
        var plist = Parse(DeviceProfile.Build(Guid.NewGuid(), "Phone", "anna@example.test", "abcdefgh.secret",
            new DeviceSwitches(Mail: true, Files: true, Calendars: true, Contacts: true, EncryptMail: false), Servers));

        Assert.Equal(
            ["com.apple.mail.managed", "com.apple.caldav.account", "com.apple.carddav.account", "Configuration"],
            PayloadTypes(plist));
        Assert.Contains(plist.Descendants("string"), s => s.Value == "abcdefgh.secret");
        Assert.Contains(plist.Descendants("string"), s => s.Value == "mail.example.test");
    }

    [Fact]
    public void A_switched_off_protocol_has_no_account()
    {
        var plist = Parse(DeviceProfile.Build(Guid.NewGuid(), "Laptop", "anna@example.test", "p",
            new DeviceSwitches(Mail: false, Files: true, Calendars: true, Contacts: false, EncryptMail: false), Servers));

        Assert.Equal(["com.apple.caldav.account", "Configuration"], PayloadTypes(plist));
    }

    [Fact]
    public void Without_IMAP_the_profile_has_no_mail_account()
    {
        var plist = Parse(DeviceProfile.Build(Guid.NewGuid(), "Phone", "anna@example.test", "p",
            new DeviceSwitches(true, true, true, true, false), Servers with { ImapHost = null }));

        Assert.DoesNotContain("com.apple.mail.managed", PayloadTypes(plist));
    }

    [Fact]
    public void A_name_with_markup_stays_text()
    {
        var name = "Anna's <phone> & \"tablet\"";
        var plist = Parse(DeviceProfile.Build(Guid.NewGuid(), name, "anna@example.test", "p",
            new DeviceSwitches(true, false, false, false, false), Servers));

        Assert.Contains(plist.Descendants("string"), s => s.Value.EndsWith($": {name}", StringComparison.Ordinal));
    }

    [Fact]
    public void The_same_device_keeps_its_identifiers_so_a_reinstall_replaces_the_profile()
    {
        var device = Guid.NewGuid();
        string Uuids(string password) => string.Join(",", Parse(DeviceProfile.Build(device, "Phone", "a@example.test", password,
                new DeviceSwitches(true, true, true, true, false), Servers))
            .Descendants("key").Where(k => k.Value == "PayloadUUID").Select(k => ((XElement)k.NextNode!).Value));

        Assert.Equal(Uuids("first"), Uuids("second"));
        Assert.Contains(Alcifra.Presentation.WireNames.DeviceProfileIdentifierPrefix, DeviceProfile.Build(device, "Phone",
            "a@example.test", "p", new DeviceSwitches(true, true, true, true, false), Servers));
    }
}
