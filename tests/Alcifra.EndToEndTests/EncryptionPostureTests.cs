using System.Net;
using System.Text.Json;

namespace Alcifra.EndToEndTests;

// The tenant's encryption posture (#1864, ADR 0955), reached by the `encryption` rel on /me: the mode's name and its
// four membership answers, read by any signed-in user of the tenant — None for an unlisted tenant, Storage for the
// crypto tenant, Strict for the strict one, as the fixture configures them.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class EncryptionPostureTests
{
    private readonly E2EApiFactory _factory;

    public EncryptionPostureTests(E2EApiFactory factory) => _factory = factory;

    [Theory]
    [InlineData(null, "None", false, false, false, false)]
    [InlineData(E2EApiFactory.CryptoTenantName, "Storage", true, false, false, false)]
    [InlineData(E2EApiFactory.StrictTenantName, "Strict", true, true, true, true)]
    public async Task Any_user_of_the_tenant_reads_the_mode_and_its_four_answers(
        string? tenantName, string mode, bool atRest, bool refusesDoors, bool envelopes, bool mail)
    {
        var tenantId = await _factory.SeedTenantNamedAsync(tenantName ?? $"posture-{Guid.NewGuid():N}");
        var email = $"posture-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "ep-1234", "Plain User");   // no administrative right
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "ep-1234"));

        // Followed from /me, never composed (ADR 0543).
        var me = await TestJson.Get(api, "/api/me");
        var href = me.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "encryption").GetProperty("href").GetString()!;

        using var get = await api.GetAsync(href);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var tag = get.Headers.ETag;
        Assert.NotNull(tag);
        var posture = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(mode, posture.GetProperty("mode").GetString());
        Assert.Equal(
            (atRest, refusesDoors, envelopes, mail),
            (posture.GetProperty("wrapsAtRest").GetBoolean(), posture.GetProperty("refusesPlaintextDoors").GetBoolean(),
             posture.GetProperty("deliversEnvelopes").GetBoolean(), posture.GetProperty("envelopesMail").GetBoolean()));

        // Nothing beyond the posture rides here: no service address, no key, no licence state.
        Assert.Equal(
            ["deliversEnvelopes", "envelopesMail", "links", "mode", "refusesPlaintextDoors", "wrapsAtRest"],
            posture.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        using var head = await api.SendAsync(new HttpRequestMessage(HttpMethod.Head, href));
        Assert.Equal(HttpStatusCode.NoContent, head.StatusCode);
        Assert.Equal(tag, head.Headers.ETag);
    }

    [Fact]
    public async Task An_unauthenticated_caller_reaches_neither_the_rel_nor_the_resource()
    {
        using var anonymous = _factory.CreateClient();

        using var me = await anonymous.GetAsync("/api/me");
        using var posture = await anonymous.GetAsync("/api/me/encryption");
        using var head = await anonymous.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/api/me/encryption"));

        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, posture.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, head.StatusCode);
    }

    [Fact]
    public async Task A_service_account_has_no_posture_to_read()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var me = await TestJson.Get(api, "/api/me");

        Assert.DoesNotContain(me.GetProperty("links").EnumerateArray(), l => l.GetProperty("rel").GetString() == "encryption");
        using var posture = await api.GetAsync("/api/me/encryption");
        Assert.Equal(HttpStatusCode.Forbidden, posture.StatusCode);
    }
}
