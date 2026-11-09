using System.Net.Http.Headers;
using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.Views;

namespace Alcifra.UiEndToEndTests;

// The desktop's "My keys" (#2278, ADR 1083) through the shared API client against the real application: a key offers
// revoke by its rel, is revoked with a reason, then reads as revoked with that reason and offers it no more.
[Collection(UiCollection.Name)]
public class DesktopKeysTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopKeysTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_key_is_revoked_with_a_reason_through_its_rel()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var token = await Ui.GetUserTokenAsync(_app.BaseUrl);
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var label = $"Desktop card {Guid.NewGuid():N}"[..21];
        await using var enrolment = await Alcifra.TestSupport.TestKeyEnrolment.EnrolAsync(http, SelfHostedAppFixture.AdminEmail, label);

        var api = new AlcifraApiClient(token);
        var key = Assert.Single(await api.EnrolledCertificates.GetMineAsync(), k => k.Label == label);
        Assert.NotNull(key.RevokeHref);

        Assert.Null(await api.EnrolledCertificates.RevokeAsync(key.RevokeHref!, "compromised", null));

        var revoked = Assert.Single(await api.EnrolledCertificates.GetMineAsync(), k => k.Label == label);
        Assert.NotNull(revoked.RevokedAt);
        Assert.Equal("compromised", revoked.RevocationReason);
        Assert.Null(revoked.RevokeHref);
        Assert.Contains("Stolen or compromised", KeysDialog.Describe(revoked));

        // Revoked once is revoked: the old address answers with the reason a person can read.
        Assert.Equal("CERTIFICATE_ALREADY_REVOKED", await api.EnrolledCertificates.RevokeAsync(key.RevokeHref!, "lost", null));
    }
}
