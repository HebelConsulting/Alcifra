using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;

namespace Alcifra.UiEndToEndTests;

// The desktop half of the WebDAV gateway (ADR "WebDAV gateway"): the real AlcifraApiClient reads the
// WebDAV status, generates the app-specific password (returned once), and revokes it.
[Collection(UiCollection.Name)]
public class DesktopWebDavTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopWebDavTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_files_device_makes_the_mount_reachable_and_revoking_it_takes_that_back()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var view = new Alcifra.ApiClient.DevicesView(api.Devices);

        await view.LoadAsync();
        Assert.False(string.IsNullOrEmpty(view.List!.Servers.Username));
        // The account's own address under /webdav (#2128): tenant id, user id, then "Name (Tenant)".
        Assert.Matches($"/{(global::Alcifra.Presentation.WireNames.WebDavSegment)}/[A-Za-z0-9_-]{{22}}/[A-Za-z0-9_-]{{22}}/[^/]+$", view.List.Servers.WebDavUrl);

        var name = $"wd-{Guid.NewGuid():N}"[..20];
        var enrolled = await view.EnrolAsync(new Alcifra.ApiClient.DeviceRequest(name, Mail: false, Files: true, Calendars: false, Contacts: false, EncryptMail: false));
        Assert.False(string.IsNullOrEmpty(enrolled.Password));
        Assert.True(view.List!.ReachesFiles);

        await view.RevokeAsync(Assert.Single(view.Rows, r => r.Name == name).Device);
        Assert.DoesNotContain(view.Rows, r => r.Name == name);
    }
}
