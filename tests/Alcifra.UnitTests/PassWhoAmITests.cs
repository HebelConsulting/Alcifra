using Alcifra.ApiClient;
using Alcifra.Pass.Commands;

namespace Alcifra.UnitTests;

// `apass whoami`: which installation and person the shared session is, and whether a hardware key can open entries.
public class PassWhoAmITests
{
    private static WhoAmIInfo Me => new(Guid.NewGuid(), Guid.NewGuid(), "Kristall Holding AG", "Anna Muster",
        IsTenantAdmin: false, CanManageUsers: false, HasPhoto: false, CanViewAuditLog: false, MfaEnabled: true,
        CanResetMfa: false, CanLegalHold: false, CanManageClassification: false);

    [Fact]
    public void It_names_the_installation_the_person_the_tenant_and_the_usable_keys()
    {
        var keys = new[]
        {
            new EnrolledCertificatesClient.Enrolled("YubiKey 5C", "01", null, Usable: true, "attested", null, null),
            new EnrolledCertificatesClient.Enrolled("Spare key", "02", null, Usable: true, "attested", null, null) { RevokedAt = DateTimeOffset.UtcNow },
        };

        var text = WhoAmICommand.Describe(new WhoAmICommand.Identity("https://demo.alcifra.dev", Me, keys, ["My Passwords", "Team"]));

        Assert.Equal(
            "Installation    https://demo.alcifra.dev\n"
            + "Signed in as    Anna Muster\n"
            + "Tenant          Kristall Holding AG\n"
            + "Hardware keys   2 enrolled, 1 usable (YubiKey 5C, Spare key)\n"
            + "Stores          My Passwords, Team", text);
    }

    [Fact]
    public void Without_a_key_it_says_entries_cannot_be_opened()
    {
        var text = WhoAmICommand.Describe(new WhoAmICommand.Identity("https://demo.alcifra.dev", Me, [], ["My Passwords"]));

        Assert.Contains("Hardware keys   none enrolled: a store's entries cannot be opened", text);
    }

    // `apass ls` with no --store on an empty My Passwords names the stores that hold something, instead of printing nothing.
    [Fact]
    public void An_empty_default_store_names_the_stores_to_list_instead()
    {
        Assert.Equal("My Passwords is empty. The stores you may read: Team, Kiosk. List one with: apass ls -s \"<name>\"",
            LsCommand.EmptyDefaultHint(["Team", "Kiosk"]));
    }
}
