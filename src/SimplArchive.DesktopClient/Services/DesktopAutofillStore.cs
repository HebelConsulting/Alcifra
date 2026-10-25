using SimplArchive.ApiClient;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.BrowserHost;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// The desktop's answer to the browser's autofill requests (ADR 0978): the same operations sapass serves, over THIS
/// client's session and its own key funnel — one implementation of match/open/save in the host library, two hosts.
/// </summary>
/// <remarks>
/// The opener is the shared settings' (<see cref="ApiClientSettings.EnvelopeOpener"/>), which the desktop sets to its
/// content funnel: opening an entry from the browser asks for the PIN exactly as opening a document does.
/// </remarks>
public sealed class DesktopAutofillStore(SimplArchiveApiClient api) : IAutofillStore
{
    private PasswordStoreClient? _passwords;

    private PasswordStoreClient Passwords => _passwords ??= new PasswordStoreClient(api.Core, ApiClientSettings.EnvelopeOpener);

    public Task<IReadOnlyList<AutofillMatch>> MatchAsync(string site, string? app, CancellationToken cancellationToken) =>
        PasswordStoreAutofill.MatchAsync(Passwords, site, app, cancellationToken);

    public Task<AutofillCredentials?> OpenAsync(Guid entryId, CancellationToken cancellationToken) =>
        PasswordStoreAutofill.OpenAsync(Passwords, entryId, cancellationToken);

    public Task<AutofillSaved> SaveAsync(AutofillSave save, CancellationToken cancellationToken) =>
        PasswordStoreAutofill.SaveAsync(Passwords, save, cancellationToken);

    public string Generate(int length) =>
        PasswordGenerator.Generate(new PasswordGeneratorOptions(Length: length, AvoidAmbiguous: true));
}
