using Alcifra.ApiClient.Login;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.BrowserHost;

using Alcifra.ApiClient.Bookmarks;

namespace Alcifra.Pass.Infrastructure;

/// <summary>
/// The browser host's password store, as apass sees it: every store the signed-in user may read, matched on the
/// indexed site (ADR 0973), opened with the card, saved to My Passwords.
/// </summary>
/// <remarks>
/// <b>The session is resolved lazily, and a failure is an ANSWER.</b> The host is spawned by the browser whether or
/// not somebody signed in; <c>ping</c> must still work and everything else must come back as a sentence the
/// extension can show, so a missing installation is an error response, not a dead process.
/// </remarks>
internal sealed class PassAutofillStore : IAutofillStore
{
    private readonly CardOpener _opener = new();
    private Session? _session;
    private string? _token;

    public async Task<IReadOnlyList<AutofillMatch>> MatchAsync(string site, string? app, CancellationToken cancellationToken) =>
        await PasswordStoreAutofill.MatchAsync((await CurrentAsync(cancellationToken)).Passwords, site, app, cancellationToken);

    public async Task<AutofillCredentials?> OpenAsync(Guid entryId, CancellationToken cancellationToken) =>
        await PasswordStoreAutofill.OpenAsync((await CurrentAsync(cancellationToken)).Passwords, entryId, cancellationToken);

    public async Task<AutofillSaved> SaveAsync(AutofillSave save, CancellationToken cancellationToken) =>
        await PasswordStoreAutofill.SaveAsync((await CurrentAsync(cancellationToken)).Passwords, save, cancellationToken);

    public string Generate(int length) => Passwords.Generate(length);

    public IBookmarkRemote Bookmarks => new BookmarksClient(CurrentAsync(CancellationToken.None).GetAwaiter().GetResult().Core);

    // The browser starts this process with no environment of ours and keeps it running for as long as it likes, so each
    // request loads the session file again, which also renews it (ADR 1071). The opener outlives a renewal, and with it what
    // it holds for the card.
    private async Task<Session> CurrentAsync(CancellationToken cancellationToken)
    {
        await CliSession.LoadIntoEnvironmentAsync(renew: true, cancellationToken);
        var token = Environment.GetEnvironmentVariable(Session.TokenVariable);
        if (_session is null || token != _token)
        {
            _session?.Dispose();
            _session = Session.FromEnvironment(_opener);
            _token = token;
        }

        return _session;
    }
}
