using SimplArchive.ApiClient;

namespace SimplArchive.UnitTests.ApiClient;

// ADR 0981: the client half of the autofill session exchange — the token endpoint taken from discovery, the exact form
// the server expects, and a refusal answered as null rather than an exception.
[Collection(ApiClientSettingsCollection.Name)]
public sealed class AutofillSessionClientTests : ApiClientSettingsTest
{
    private static LoopbackServer IdentityServer(int tokenStatus, string tokenBody)
    {
        var self = string.Empty;
        var server = new LoopbackServer((_, path, _) => path switch
        {
            "/.well-known/openid-configuration" => (200, $$"""{"token_endpoint":"{{self}}connect/token"}"""),
            "/connect/token" => (tokenStatus, tokenBody),
            _ => (404, "{}"),
        });
        self = server.BaseUrl;
        return server;
    }

    [Fact]
    public async Task The_app_token_is_exchanged_for_a_pair_of_the_autofill_client()
    {
        using var server = IdentityServer(200, """{"access_token":"a2","refresh_token":"r2","expires_in":900,"token_type":"Bearer"}""");

        var session = await AutofillSession.OpenAsync(server.BaseUrl, "app-access");

        Assert.NotNull(session);
        Assert.Equal("a2", session.AccessToken);
        Assert.Equal("r2", session.RefreshToken);
        var form = Assert.Single(server.Requests, r => r.Path == "/connect/token").Body;
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Atoken-exchange", form);
        Assert.Contains("client_id=simplarchive-mobile-autofill", form);
        Assert.Contains("subject_token=app-access", form);
        Assert.Contains("subject_token_type=urn%3Aietf%3Aparams%3Aoauth%3Atoken-type%3Aaccess_token", form);
        Assert.Contains("scope=openid+offline_access", form);
    }

    [Fact]
    public async Task A_refusal_is_null_not_an_exception()
    {
        using var server = IdentityServer(400, """{"error":"invalid_grant"}""");

        Assert.Null(await AutofillSession.OpenAsync(server.BaseUrl, "desktop-access"));
    }
}
