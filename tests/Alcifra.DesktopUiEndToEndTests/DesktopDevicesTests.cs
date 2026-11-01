using Alcifra.ApiClient;
using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;

namespace Alcifra.UiEndToEndTests;

// The desktop's Devices view (#1970 slice 3) through the shared DevicesView, against the real application: the list with
// its server addresses, enrolment with its once-shown password and hand-over, the user's mail view, and revocation, all
// through the rels the resources advertise. It replaces the IMAP access dialog's test (ADR 0594's surface retired).
[Collection(UiCollection.Name)]
public class DesktopDevicesTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopDevicesTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_device_is_enrolled_shown_and_revoked_through_advertised_rels()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var view = new DevicesView(api.Devices);

        await view.LoadAsync();
        Assert.True(view.List!.Servers.ImapAvailable);   // the fixture runs the endpoint on an ephemeral port
        Assert.NotEmpty(view.List.Servers.Username);
        Assert.Contains(view.ServerLines, l => l.Value == view.List.Servers.WebDavUrl);

        var name = $"Desktop test {Guid.NewGuid():N}"[..24];
        var enrolled = await view.EnrolAsync(new DeviceRequest(name, Mail: true, Files: true, Calendars: true, Contacts: true, EncryptMail: false));
        Assert.Matches(@"^[a-z2-7]{8}\.[0-9a-f]{32}$", enrolled.Password);
        Assert.StartsWith("data:image/png;base64,", enrolled.HandOver.PasswordQr);
        Assert.NotNull(enrolled.HandOver.ProfileUrl);
        var row = Assert.Single(view.Rows, r => r.Name == name);
        Assert.True(row.CanEdit);

        await view.SetShowAllDocumentsAsync(true);
        Assert.True(view.List!.ShowAllDocuments);
        await view.SetShowAllDocumentsAsync(false);

        await view.RevokeAsync(row.Device);
        Assert.DoesNotContain(view.Rows, r => r.Name == name);
    }
}
