using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SimplArchive.EndToEndTests;

// ADR 0981: the mobile app's autofill extension runs in its own process, so it gets its OWN session — the app exchanges
// its access token (RFC 8693) for a new authorization of the simplarchive-mobile-autofill client. Driven through the
// real sign-in and the real OpenIddict server, because what matters is what the issued tokens then do: work, renew,
// and live and die independently of the app's.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class AutofillSessionTests
{
    private const string AutofillClientId = "simplarchive-mobile-autofill";
    private const string MobileClientId = "simplarchive-mobile";
    private const string MobileRedirect = "dev.simplarchive.mobile:/oauth2redirect";

    private readonly E2EApiFactory _factory;

    public AutofillSessionTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_mobile_apps_token_opens_an_autofill_session_that_acts_as_the_user_and_renews()
    {
        var (email, password, userId) = await SeedUserAsync();
        var app = await _factory.SignInNativeAppAsync(email, password, MobileClientId, MobileRedirect);

        var autofill = await ExchangeOkAsync(Access(app));

        Assert.Equal("Bearer", autofill.GetProperty("token_type").GetString());
        Assert.True(autofill.GetProperty("expires_in").GetInt32() > 0);
        Assert.NotEqual(Refresh(app), Refresh(autofill));
        Assert.Equal(userId, await WhoAmIUserAsync(Access(autofill)));

        using var client = _factory.CreateClient();
        using var renewed = await PostRefreshAsync(client, Refresh(autofill), AutofillClientId);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        var renewedTokens = await ReadAsync(renewed);
        Assert.Equal(userId, await WhoAmIUserAsync(Access(renewedTokens)));
    }

    [Fact]
    public async Task A_token_from_any_other_client_is_refused_and_an_autofill_token_cannot_chain()
    {
        var (email, password, _) = await SeedUserAsync();
        var desktop = await _factory.SignInNativeAppAsync(email, password, "simplarchive-desktop", "http://127.0.0.1:8765/callback");
        var web = await _factory.GetUserTokenAsync(email, password);
        var autofill = await ExchangeOkAsync(Access(await _factory.SignInNativeAppAsync(email, password, MobileClientId, MobileRedirect)));

        foreach (var subject in new[] { Access(desktop), web, Access(autofill) })
        {
            await AssertRefusedAsync(await ExchangeAsync(subject));
        }
    }

    [Fact]
    public async Task An_impersonation_request_through_the_autofill_client_is_refused()
    {
        var (email, password, userId) = await SeedUserAsync();
        var app = await _factory.SignInNativeAppAsync(email, password, MobileClientId, MobileRedirect);

        using var response = await ExchangeAsync(Access(app), ("requested_subject", userId.ToString()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await ReadAsync(response)).GetProperty("error").GetString());
    }

    // Proves: revoking the autofill session's tokens (RFC 7009) ends THAT session — its access token answers 401 and its
    // refresh token is refused — while the app's access token still answers 200 and the app's refresh token still renews.
    [Fact]
    public async Task Revoking_the_autofill_session_leaves_the_apps_session_working()
    {
        var (email, password, _) = await SeedUserAsync();
        var app = await _factory.SignInNativeAppAsync(email, password, MobileClientId, MobileRedirect);
        var autofill = await ExchangeOkAsync(Access(app));

        await RevokeAsync(autofill, AutofillClientId);

        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIStatusAsync(Access(autofill)));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRefreshAsync(client, Refresh(autofill), AutofillClientId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await WhoAmIStatusAsync(Access(app)));
        Assert.Equal(HttpStatusCode.OK, (await PostRefreshAsync(client, Refresh(app), MobileClientId)).StatusCode);
    }

    // Proves the converse, twice over: (1) revoking the app's tokens leaves the autofill access token answering 200 and
    // its refresh token renewing; (2) the case that motivated the separate session — the app's refresh token REPLAYED
    // past the reuse leeway, which revokes the app's whole authorization — leaves the autofill session working too.
    [Fact]
    public async Task Ending_the_apps_session_leaves_the_autofill_session_working()
    {
        var (email, password, _) = await SeedUserAsync();
        using var client = _factory.CreateClient();

        // (1) Revocation.
        var app = await _factory.SignInNativeAppAsync(email, password, MobileClientId, MobileRedirect);
        var autofill = await ExchangeOkAsync(Access(app));
        await RevokeAsync(app, MobileClientId);
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIStatusAsync(Access(app)));
        Assert.Equal(HttpStatusCode.OK, await WhoAmIStatusAsync(Access(autofill)));
        using (var renewed = await PostRefreshAsync(client, Refresh(autofill), AutofillClientId))
        {
            Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        }

        // (2) Reuse detection on the app's refresh token.
        var second = await _factory.SignInNativeAppAsync(email, password, MobileClientId, MobileRedirect);
        var secondAutofill = await ExchangeOkAsync(Access(second));
        using (var rotated = await PostRefreshAsync(client, Refresh(second), MobileClientId))
        {
            Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        }

        await Task.Delay(TimeSpan.FromSeconds(2));   // past the test host's one-second leeway
        Assert.Equal(HttpStatusCode.BadRequest, (await PostRefreshAsync(client, Refresh(second), MobileClientId)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await WhoAmIStatusAsync(Access(second)));
        Assert.Equal(HttpStatusCode.OK, await WhoAmIStatusAsync(Access(secondAutofill)));
        using (var renewed = await PostRefreshAsync(client, Refresh(secondAutofill), AutofillClientId))
        {
            Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        }
    }

    [Fact]
    public async Task A_deactivated_user_cannot_open_an_autofill_session()
    {
        var (email, password, userId) = await SeedUserAsync();
        var app = await _factory.SignInNativeAppAsync(email, password, MobileClientId, MobileRedirect);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>();
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
            user.IsActive = false;
            await db.SaveChangesAsync();
        }

        await AssertRefusedAsync(await ExchangeAsync(Access(app)));
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private async Task<(string Email, string Password, Guid UserId)> SeedUserAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"autofill-{Guid.NewGuid():N}@e2e.local";
        const string password = "autofill-1234";
        var userId = await _factory.SeedUserAsync(tenantId, email, password, "Autofill User");
        return (email, password, userId);
    }

    private static string Access(JsonElement tokens) => tokens.GetProperty("access_token").GetString()!;

    private static string Refresh(JsonElement tokens) => tokens.GetProperty("refresh_token").GetString()!;

    private async Task<HttpResponseMessage> ExchangeAsync(string subjectToken, params (string Name, string Value)[] extra)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["client_id"] = AutofillClientId,
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["scope"] = "openid offline_access",
        };
        foreach (var (name, value) in extra)
        {
            form[name] = value;
        }

        using var client = _factory.CreateClient();
        return await client.PostAsync("/connect/token", new FormUrlEncodedContent(form));
    }

    private async Task<JsonElement> ExchangeOkAsync(string subjectToken)
    {
        using var response = await ExchangeAsync(subjectToken);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_grant", (await ReadAsync(response)).GetProperty("error").GetString());
        }
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static Task<HttpResponseMessage> PostRefreshAsync(HttpClient client, string refreshToken, string clientId) =>
        client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = clientId,
        }));

    private async Task RevokeAsync(JsonElement tokens, string clientId)
    {
        using var client = _factory.CreateClient();
        foreach (var (token, hint) in new[] { (Refresh(tokens), "refresh_token"), (Access(tokens), "access_token") })
        {
            using var revoked = await client.PostAsync("/connect/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = token,
                ["token_type_hint"] = hint,
                ["client_id"] = clientId,
            }));
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        }
    }

    private async Task<HttpStatusCode> WhoAmIStatusAsync(string accessToken)
    {
        using var client = _factory.CreateAuthedClient(accessToken);
        using var response = await client.GetAsync("/api/diagnostics/whoami");
        return response.StatusCode;
    }

    private async Task<Guid> WhoAmIUserAsync(string accessToken)
    {
        using var client = _factory.CreateAuthedClient(accessToken);
        return (await TestJson.Get(client, "/api/diagnostics/whoami")).GetProperty("userId").GetGuid();
    }
}
