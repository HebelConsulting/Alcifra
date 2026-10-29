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
        // The names before the rename are read after the current one until WireNames.Legacy retires (v0.56.0).
        var url = First(UrlVariable, WireNames.Legacy.SessionUrlVariable, WireNames.Legacy.CliUrlVariable)
            ?? throw new SapassException($"No installation. Run: eval \"$(apass login --url …)\" to set ${UrlVariable}.");
        var token = First(TokenVariable, WireNames.Legacy.SessionTokenVariable, WireNames.Legacy.CliTokenVariable)
            ?? throw new SapassException($"Not signed in. Run: eval \"$(apass login --url {url})\" to set ${TokenVariable}.");

        var http = new HttpClient { BaseAddress = new Uri($"{url.TrimEnd('/')}/") };
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

    private static string? First(params string[] names) =>
        names.Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
