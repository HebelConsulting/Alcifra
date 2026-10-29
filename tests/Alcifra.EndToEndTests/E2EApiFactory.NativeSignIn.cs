using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace Alcifra.EndToEndTests;

// A native app's sign-in, shared by the tests that need a desktop's or a phone's own token pair (refresh, revocation,
// the autofill session exchange of ADR 0981) rather than the web client's access token alone.
public sealed partial class E2EApiFactory
{
    /// <summary>A native app's authorization-code + PKCE flow, asking for "openid offline_access" as both apps do; the token response.</summary>
    public async Task<JsonElement> SignInNativeAppAsync(string email, string password, string clientId, string redirect)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));

        var authorize = "/connect/authorize?" + string.Join('&', new[]
        {
            $"client_id={clientId}", "response_type=code",
            $"redirect_uri={Uri.EscapeDataString(redirect)}",
            $"scope={Uri.EscapeDataString("openid offline_access")}",
            $"code_challenge={challenge}", "code_challenge_method=S256", "state=x",
        });

        var loginPath = (await client.GetAsync(authorize)).Headers.Location!.ToString();
        var loginHtml = await client.GetStringAsync(loginPath);
        var antiforgery = Regex.Match(loginHtml, @"__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;
        var returnUrl = QueryHelpers.ParseQuery(new Uri("http://localhost" + loginPath).Query)["ReturnUrl"].ToString();

        var login = await client.PostAsync(loginPath, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = antiforgery,
        }));

        var next = login.Headers.Location!.ToString();
        string? code = null;
        for (var i = 0; i < 8 && code is null; i++)
        {
            var response = await client.GetAsync(next);
            if (response.Headers.Location is not { } location)
            {
                break;
            }

            var absolute = location.IsAbsoluteUri ? location : new Uri(new Uri("http://localhost"), location);
            code = QueryHelpers.ParseQuery(absolute.Query).TryGetValue("code", out var c) ? c.ToString() : null;
            next = absolute.ToString();
        }

        Assert.False(string.IsNullOrEmpty(code), "the authorization flow returned no code.");

        var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["redirect_uri"] = redirect,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        }));

        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        return JsonDocument.Parse(await token.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
