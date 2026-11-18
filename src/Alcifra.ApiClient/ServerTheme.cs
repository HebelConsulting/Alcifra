using System.Text.Json;
using Alcifra.Theming;
using Microsoft.Extensions.Logging;

namespace Alcifra.ApiClient;

/// <summary>
/// The palette an installation wears (#2319, ADR 0578): its anonymous <c>theme</c> rel, followed from the API root —
/// the shipped brand, or the palette variant the operator selected, with any <c>custom/theme.json</c> laid over it.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="ServerIdentity"/>: both ask a server's address a question <b>before anyone has
/// signed in</b>, and both answer nothing rather than an error when the server cannot be reached. A host's built-in
/// design is always a usable fallback, and a colour scheme is never worth failing a sign-in over.
/// </para>
/// <para>
/// The response is the installation's <b>effective</b> palette, so it is read as a whole rather than as an override
/// laid over the host's own: the operator's variant and custom file were already applied on the server, and a client
/// that re-derived them would disagree with the login page the person just left.
/// </para>
/// <para>
/// No cache lives here. How long a host keeps a server's palette — the desktop caches per server for the process, the
/// phone per server profile — is the host's business, and a shared client that cached would bind the palette to a
/// process rather than to a server.
/// </para>
/// </remarks>
public sealed class ServerTheme
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Short: this sits in front of a sign-in, and a server that is slow to answer must not delay the page it themes.
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly HttpClient _http;

    /// <summary>A reader over a caller-supplied client (a test, or a host with its own network stack).</summary>
    public ServerTheme(HttpClient http) => _http = http;

    /// <summary>A reader over the shared client: one connection pool, a five-second deadline.</summary>
    public ServerTheme()
        : this(SharedHttp)
    {
    }

    /// <summary>The server's palette, or null where it cannot be read or is not usable — never an exception.</summary>
    /// <param name="serverUrl">The installation's address, as a server profile holds it (the API root's origin).</param>
    public async Task<ThemeTokens?> ReadAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            var rootUrl = new Uri($"{serverUrl.TrimEnd('/')}/api");
            using var root = JsonDocument.Parse(await _http.GetStringAsync(rootUrl, cancellationToken));
            var href = ApiCore.FindLink(root.RootElement, "theme");
            if (href is null)
            {
                // Not an error: an installation older than the rel (or one that does not theme itself) simply has no
                // palette of its own to hand out, and the caller keeps the one it built with.
                return null;
            }

            var json = await _http.GetStringAsync(Resolve(rootUrl, href), cancellationToken);
            var tokens = JsonSerializer.Deserialize<ThemeTokens>(json, Options);
            if (tokens is null)
            {
                return null;
            }

            // The same contrast standard the server applies to its own custom theme (ADR 0578). A palette that fails it
            // is refused here rather than painted: legibility is the one thing a brand may not trade away.
            var failures = ThemeTokensReader.Validate(tokens).ToList();
            if (failures.Count > 0)
            {
                ApiClientSettings.Logger.LogWarning(
                    "The palette read from {Server} was refused and the built-in design is in use: {Reason}", serverUrl, failures[0]);
                return null;
            }

            return tokens;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or UriFormatException
            or InvalidOperationException or KeyNotFoundException or NotSupportedException)
        {
            ApiClientSettings.Logger.LogDebug("The palette could not be read from {Server}: {Reason}", serverUrl, e.Message);
            return null;
        }
    }

    // The root's hrefs are root-relative ("/api/theme"). A leading-slash path is ALSO a valid absolute file:// URI on
    // Unix, so the scheme has to be tested rather than "is it absolute" (the trap ApiCore.ResolveContentUrl records).
    private static Uri Resolve(Uri rootUrl, string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var absolute)
        && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            ? absolute
            : new Uri(rootUrl, href);
}
