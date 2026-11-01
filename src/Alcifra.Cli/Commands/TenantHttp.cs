using System.Net.Http.Headers;
using Alcifra.Cli.Infrastructure;

namespace Alcifra.Cli.Commands;

/// <summary>The HTTP client a command talks to the installation through, carrying the session's bearer token.</summary>
/// <remarks>
/// It began as the helper of <c>alcifra me certificate</c> and served every command since; it moved here when that
/// command retired with the self-service certificate (#1970 slice 4).
/// </remarks>
internal static class TenantHttp
{
    internal static HttpClient Client(TenantSessionSettings settings)
    {
        var http = new HttpClient { BaseAddress = new Uri(settings.ResolvedUrl.TrimEnd('/') + "/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResolvedToken);

        return http;
    }
}
