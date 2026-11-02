using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Alcifra.UiEndToEndTests;

// The demo logins can mount the drive and add the mailbox with the password they already have.
//
// WebDAV/CalDAV/CardDAV and IMAP use APP-SPECIFIC passwords that normally do not exist until a user generates
// one — right for a product, wrong for a demo, where a credential you must find two dialogs to create is one
// nobody tries. The seeder gives each demo login one device with the known demo password (owner, 2026-11-02).
//
// Asserted against the real seeded app rather than the seeder's code, because "enabled" is a property of what
// the running system reports, and the endpoint the clients read is the only thing that makes it true for a user.
//
// Deliberately on ANNA, not the admin. DesktopWebDavTests and DesktopImapAccessTests both revoke the ADMIN's
// credentials as their last act, so a test asserting the admin still has one passes or fails on which ran
// first — written that way once, and it passed alone and failed three times in the full suite.
[Collection(UiCollection.Name)]
public class DesktopDemoProtocolPasswordsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopDemoProtocolPasswordsTests(SelfHostedAppFixture app) => _app = app;

    /// <summary>The seeder derives the extra logins from the ADMIN's domain, and they share its password.</summary>
    private static string AnnaEmail =>
        $"anna@{SelfHostedAppFixture.AdminEmail.Split('@')[1]}";

    [Fact]
    public async Task The_demo_login_has_one_device_for_everything_and_no_earlier_passwords()
    {
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl, AnnaEmail, SelfHostedAppFixture.AdminPassword));

        // ONE device with the demo password (owner, 2026-11-02): a demo visitor mounts and adds the mailbox at once, and
        // the device says which password it takes. The two earlier passwords the seed made before are gone.
        var items = (await http.GetFromJsonAsync<JsonElement>("/api/me/devices")).GetProperty("items").EnumerateArray().ToList();

        var device = Assert.Single(items);
        Assert.Equal("seeded", device.GetProperty("kind").GetString());
        Assert.Equal($"Demo device (password {SelfHostedAppFixture.AdminPassword})", device.GetProperty("name").GetString());
        Assert.All(new[] { "mail", "files", "calendars", "contacts", "canEdit" }, p => Assert.True(device.GetProperty(p).GetBoolean(), p));
    }

    [Fact]
    public async Task The_seeded_password_is_the_one_that_is_published()
    {
        // The status endpoint says a credential EXISTS; it cannot say which one. So this signs in over the
        // WebDAV endpoint itself with the published demo password — the only check that would catch the
        // credential being seeded to something nobody was told.
        using var dav = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{AnnaEmail}:{SelfHostedAppFixture.AdminPassword}"));
        dav.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), ("/" + global::Alcifra.Presentation.WireNames.WebDavSegment));
        request.Headers.Add("Depth", "0");

        var response = await dav.SendAsync(request);

        Assert.Equal(207, (int)response.StatusCode); // 207 Multi-Status: authenticated and listing
    }
}
