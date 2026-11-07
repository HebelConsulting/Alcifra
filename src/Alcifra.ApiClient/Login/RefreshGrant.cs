using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Alcifra.ApiClient.Login;

/// <summary>One refresh-token grant, for whoever holds a refresh token: the desktop's and phone's handler, and the tools' session file.</summary>
internal static class RefreshGrant
{
    /// <summary>The new session, or null when the server refused it (or answered no access token).</summary>
    /// <exception cref="HttpRequestException">The server is unreachable or could not answer: a throttle's 429, a 5xx, a
    /// proxy's 502 during a restart. None of those says anything about the token, so the session and its stored refresh
    /// token survive, and the connectivity path owns the case. A refusal is what OAuth refuses with: 400, 401 or 403.</exception>
    public static async Task<TokenSession?> RedeemAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        string apiRootUrl, string refreshToken, string clientId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{apiRootUrl.TrimEnd('/')}/connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId,
            }),
        };

        using var response = await send(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            ApiClientSettings.Logger.LogWarning("Renewing the session on {ApiRootUrl} answered {Status}; the session is kept", apiRootUrl, (int)response.StatusCode);
            throw new HttpRequestException($"Renewing the session answered {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return FromTokenResponse(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken), refreshToken);
    }

    /// <summary>A token endpoint's answer as a session, or null without an access token.</summary>
    /// <param name="tokens">The answer's body.</param>
    /// <param name="previousRefreshToken">Kept only where the answer names none. Rotation is on server-side, so a NEW refresh
    /// token normally comes back and the old one is spent: keeping the old one would guarantee the next renewal fails.</param>
    public static TokenSession? FromTokenResponse(JsonElement tokens, string? previousRefreshToken)
    {
        var accessToken = tokens.TryGetProperty("access_token", out var access) ? access.GetString() : null;
        if (string.IsNullOrEmpty(accessToken))
        {
            return null;
        }

        var rotated = tokens.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : previousRefreshToken;
        var lifetime = tokens.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;

        return new TokenSession(accessToken, rotated, DateTimeOffset.UtcNow + lifetime);
    }
}
