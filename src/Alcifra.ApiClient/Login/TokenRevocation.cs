using Microsoft.Extensions.Logging;

namespace Alcifra.ApiClient.Login;

/// <summary>Revokes a session's tokens on the server (RFC 7009, ADR 0895): best effort, because signing out locally must not wait on it.</summary>
internal static class TokenRevocation
{
    public static async Task RevokeAsync(string apiRootUrl, string? refreshToken, string? accessToken, string clientId)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var (token, hint) in new[] { (refreshToken, "refresh_token"), (accessToken, "access_token") })
        {
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            try
            {
                using var response = await http.PostAsync($"{apiRootUrl.TrimEnd('/')}/connect/revoke", new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        ["token"] = token,
                        ["token_type_hint"] = hint,
                        ["client_id"] = clientId,
                    }));
                if (!response.IsSuccessStatusCode)
                {
                    ApiClientSettings.Logger.LogWarning("Sign-out could not revoke the {Hint} on the server ({Status}); it stays valid until it expires",
                        hint, (int)response.StatusCode);
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                ApiClientSettings.Logger.LogWarning("Sign-out could not reach the server to revoke the {Hint} ({Reason}); it stays valid until it expires",
                    hint, e.Message);
            }
        }
    }
}
