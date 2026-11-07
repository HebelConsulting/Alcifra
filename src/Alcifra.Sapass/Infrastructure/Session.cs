using System.Net;
using System.Net.Http.Headers;
using Alcifra.ApiClient;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.Presentation;

namespace Alcifra.Sapass.Infrastructure;

/// <summary>
/// The signed-in installation, taken from the environment — the one place "which installation am I pointed at?"
/// is answered (as alcifra's session is, ADR 0823).
/// </summary>
/// <remarks>
/// <para>
/// <b>The neutral variables come first, and alcifra's are still read for one release</b> (owner, 2026-10-25): the
/// session was named after one of the two tools, so a person who signed in with either tool should not have to
/// sign in again with the other while the names settle.
/// </para>
/// <para>
/// <b>Nothing is written to disk.</b> The session lives in the environment, so there is no credential file to
/// find on a host and no second place for "which installation" to be answered differently from the shell that
/// is typing.
/// </para>
/// </remarks>
internal sealed class Session : IDisposable
{
    public const string UrlVariable = WireNames.UrlVariable;
    public const string TokenVariable = WireNames.TokenVariable;

    private Session(string url, HttpClient http, ApiCore core, PasswordStoreClient passwords)
    {
        Url = url;
        Http = http;
        Core = core;
        Passwords = passwords;
    }

    public string Url { get; }

    public HttpClient Http { get; }

    public ApiCore Core { get; }

    public PasswordStoreClient Passwords { get; }

    /// <summary>The session the environment describes, or a sentence saying how to get one.</summary>
    /// <param name="opener">The key path, for a command that opens an entry; null for one that only lists names.</param>
    public static Session FromEnvironment(IEnvelopeOpener? opener = null)
    {
        var url = Variable(UrlVariable)
            ?? throw new SapassException($"No installation. Run: eval \"$(apass login --url …)\" to set ${UrlVariable}.");
        var token = Variable(TokenVariable)
            ?? throw new SapassException($"Not signed in. Run: eval \"$(apass login --url {url})\" to set ${TokenVariable}.");

        // The shared client resolves a RELATIVE content address (the sealed tier's /api/encrypted-content?t=…) against
        // ApiClientSettings.ApiRootUrl, which each host sets once. apass never did, so an entry's content went to the
        // default http://localhost:8080 while everything else went to the installation (#2042).
        ApiClientSettings.ApiRootUrl = url.TrimEnd('/');
        var http = new HttpClient(new SessionExpiry(url.TrimEnd('/')) { InnerHandler = new HttpClientHandler() }) { BaseAddress = new Uri($"{url.TrimEnd('/')}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var core = ApiCore.Over(http);
        return new Session(url.TrimEnd('/'), http, core, new PasswordStoreClient(core, opener));
    }

    /// <summary>
    /// A session over a client the host already holds — addressed and authenticated — so the tool's own tree walk
    /// can be exercised against a real installation without a real URL (the test seam, as <see cref="ApiCore.Over"/>
    /// is the desktop's).
    /// </summary>
    internal static Session Over(HttpClient http, IEnvelopeOpener? opener = null)
    {
        var core = ApiCore.Over(http);
        return new Session(http.BaseAddress?.ToString().TrimEnd('/') ?? string.Empty, http, core, new PasswordStoreClient(core, opener));
    }

    public void Dispose() => Http.Dispose();

    private static string? Variable(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;
}

/// <summary>
/// Says what a 401 means. It has one cause here: the session in the environment has expired, and there is no refresh
/// token by design (ADR 0823). Left to the shared client, an expired session reads "The password store could not be
/// read", because a 401 carries no problem body, and sends the reader looking at the store. Held at the client so every
/// command says the same.
/// </summary>
internal sealed class SessionExpiry(string url) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        throw new SapassException($"The session has expired. Run: eval \"$(apass login --url {url})\". This tool holds no refresh token by design.");
    }
}
