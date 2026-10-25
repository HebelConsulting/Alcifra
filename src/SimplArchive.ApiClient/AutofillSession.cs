using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SimplArchive.ApiClient;

/// <summary>
/// Opens the autofill extension's OWN session from the signed-in app's access token (ADR 0981): an RFC 8693 token
/// exchange at the token endpoint the discovery document names, for the <see cref="ClientId"/> client. The result is a
/// separate token pair that the extension renews and revokes with <see cref="ClientId"/>, never with the app's.
/// </summary>
/// <remarks>Beside <see cref="PkceSignIn"/> because it is the same step in another form: discovery, one POST to the
/// token endpoint, the same token response.</remarks>
public static class AutofillSession
{
    /// <summary>The autofill extension's public client; its refresh and revocation requests name it too.</summary>
    public const string ClientId = "simplarchive-mobile-autofill";

    /// <summary>What the mobile app's own sign-in asks for, so the extension's session can renew.</summary>
    public const string Scopes = "openid offline_access";

    private static readonly HttpClient Http = new();

    /// <summary>
    /// Exchanges <paramref name="appAccessToken"/> (the mobile app's own, still valid) for a new session, or null when
    /// the server refuses it: a token from another client, a revoked one, or a deactivated user. An unreachable server
    /// throws <see cref="HttpRequestException"/>, which is not a refusal.
    /// </summary>
    public static async Task<AuthResult?> OpenAsync(string apiRootUrl, string appAccessToken, CancellationToken cancellationToken = default)
    {
        var discovery = await Http.GetFromJsonAsync<JsonElement>($"{apiRootUrl.TrimEnd('/')}/.well-known/openid-configuration", cancellationToken);
        var tokenEndpoint = discovery.GetProperty("token_endpoint").GetString()!;

        using var response = await Http.PostAsync(tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["client_id"] = ClientId,
            ["subject_token"] = appAccessToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["scope"] = Scopes,
        }), cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Only the OAuth error code and its description are logged: a whitelist, never the body as a whole.
            var error = await ReadErrorAsync(response, cancellationToken);
            ApiClientSettings.Logger.LogWarning(
                "The server refused to open an autofill session ({Status} {Error}): {Description}",
                (int)response.StatusCode, error.Code, error.Description);
            return null;
        }

        return PkceSignIn.Parse(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken), DateTimeOffset.UtcNow);
    }

    private static async Task<(string? Code, string? Description)> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            return (
                body.TryGetProperty("error", out var code) ? code.GetString() : null,
                body.TryGetProperty("error_description", out var description) ? description.GetString() : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
