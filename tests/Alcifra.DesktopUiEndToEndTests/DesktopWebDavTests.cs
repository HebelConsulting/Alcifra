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
    public async Task Generate_status_and_revoke_round_trips()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var before = await api.Profile.GetWebDavStatusAsync();
        Assert.False(string.IsNullOrEmpty(before.Username));
        Assert.EndsWith(("/" + global::Alcifra.Presentation.WireNames.WebDavSegment), before.Url); // the single "Alcifra" mount (ADR 0509)

        var generated = await api.Profile.GenerateWebDavPasswordAsync();
        Assert.False(string.IsNullOrEmpty(generated.Password));
        Assert.True(generated.Enabled);

        Assert.True((await api.Profile.GetWebDavStatusAsync()).Enabled);

        await api.Profile.RevokeWebDavPasswordAsync();
        Assert.False((await api.Profile.GetWebDavStatusAsync()).Enabled);
    }
}
