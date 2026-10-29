using Alcifra.ApiClient.PasswordStore;
using Alcifra.BrowserHost;

namespace Alcifra.Sapass.Infrastructure;

/// <summary>
/// The browser host's password store, as apass sees it: every store the signed-in user may read, matched on the
/// indexed site (ADR 0973), opened with the card, saved to My Passwords.
/// </summary>
/// <remarks>
/// <b>The session is resolved lazily, and a failure is an ANSWER.</b> The host is spawned by the browser whether or
/// not somebody signed in; <c>ping</c> must still work and everything else must come back as a sentence the
/// extension can show, so a missing installation is an error response, not a dead process.
/// </remarks>
internal sealed class SapassAutofillStore : IAutofillStore
{
    private readonly Lazy<Session> _session = new(() => Session.FromEnvironment(new CardOpener()));

    public Task<IReadOnlyList<AutofillMatch>> MatchAsync(string site, string? app, CancellationToken cancellationToken) =>
        PasswordStoreAutofill.MatchAsync(_session.Value.Passwords, site, app, cancellationToken);

    public Task<AutofillCredentials?> OpenAsync(Guid entryId, CancellationToken cancellationToken) =>
        PasswordStoreAutofill.OpenAsync(_session.Value.Passwords, entryId, cancellationToken);

    public Task<AutofillSaved> SaveAsync(AutofillSave save, CancellationToken cancellationToken) =>
        PasswordStoreAutofill.SaveAsync(_session.Value.Passwords, save, cancellationToken);

    public string Generate(int length) => Passwords.Generate(length);

}
