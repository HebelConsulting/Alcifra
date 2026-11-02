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
    // #2090: the shared view states a device's calendars and contacts as all or chosen, with the ticks the collections
    // rel offers; a change made through it reads back.
    [Fact]
    public async Task A_device_is_switched_to_chosen_collections_through_the_collections_rel()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var view = new DevicesView(api.Devices);
        var name = $"Desktop scope {Guid.NewGuid():N}"[..24];
        await view.EnrolAsync(new DeviceRequest(name, Mail: false, Files: false, Calendars: true, Contacts: true, EncryptMail: false));
        var device = Assert.Single(view.Rows, r => r.Name == name).Device;
        Assert.True(device.OffersCollections);
        Assert.False(device.CollectionsChosen);

        var choices = await view.CollectionsAsync(device);
        await view.ChangeAsync(device, DevicesView.RequestOf(device) with
        {
            CollectionsChosen = true,
            ChosenCollections = [.. choices.Take(1).Select(c => c.Id)],
        });

        var changed = Assert.Single(view.Rows, r => r.Name == name).Device;
        Assert.True(changed.CollectionsChosen);
        Assert.Equal(choices.Take(1).Select(c => c.Id), changed.ChosenCollections!);
        await view.RevokeAsync(changed);
    }
}
