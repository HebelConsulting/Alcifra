using System.Net.Http.Headers;
using System.Text.Json;

namespace Alcifra.Cli.Infrastructure;

/// <summary>
/// Acting as another user (RFC 8693 token exchange, ADR "User impersonation"): how an administrator moves a user's
/// Messenger conversations (core #1813, ADR 0947). Reached by hypermedia — the root's <c>users</c>, the user's row, and
/// the row's <c>impersonate</c> rel, which the server offers only where the caller may act as that user — never by a
/// composed address. The server refuses an administrator as the target, and records every act as done on behalf.
/// </summary>
public static class Impersonation
{
    /// <summary>An access token acting as the user with <paramref name="email"/>, exchanged for the session's own.</summary>
    public static async Task<string> TokenForAsync(TenantSessionSettings settings, string email, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);
        return await TokenForAsync(http, settings.ResolvedToken, email, cancellationToken);
    }

    /// <summary>
    /// The same, on a client already signed in with <paramref name="subjectToken"/> — the part the end-to-end tests run
    /// against a real server.
    /// </summary>
    public static async Task<string> TokenForAsync(HttpClient http, string subjectToken, string email, CancellationToken cancellationToken)
    {
        var api = new AlcifraApi(http);
        for (string? href = await new Hypermedia(api).RootHrefAsync("users", cancellationToken); href is not null;)
        {
            var page = await api.GetAsync(href, cancellationToken);
            foreach (var user in page.GetProperty("users").EnumerateArray())
            {
                if (string.Equals(user.GetProperty("email").GetString(), email, StringComparison.OrdinalIgnoreCase))
                {
                    var exchange = Hypermedia.Href(Hypermedia.LinksOf(user), "impersonate", $"The user {email}");
                    return await ExchangeAsync(http, exchange, subjectToken, user.GetProperty("id").GetString()!, email, cancellationToken);
                }
            }

            href = Hypermedia.LinksOf(page).GetValueOrDefault("next");
        }

        throw new CliException($"There is no user {email} on this tenant.");
    }

    private static async Task<string> ExchangeAsync(
        HttpClient http, string href, string subjectToken, string userId, string email, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["client_id"] = CliIdentity.ClientId,
            ["subject_token"] = subjectToken,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["requested_subject"] = userId,
        });
        using var response = await http.PostAsync(href.TrimStart('/'), form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // The token is never echoed; the server's own reason is (an administrator target, a missing right).
            var reason = TryReason(body);
            throw new CliException($"The installation refused to let you act as {email} ({(int)response.StatusCode}){(reason is null ? "." : $": {reason}")}");
        }

        return JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()
            ?? throw new CliException("The token endpoint answered without an access_token.");
    }

    private static string? TryReason(string body)
    {
        try
        {
            return JsonDocument.Parse(body).RootElement.TryGetProperty("error_description", out var description) ? description.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
