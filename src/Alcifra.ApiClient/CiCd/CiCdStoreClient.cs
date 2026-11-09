using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.ApiClient.PasswordStore;
using Microsoft.Extensions.Logging;

namespace Alcifra.ApiClient.CiCd;

/// <summary>A CI/CD store as the server lists it (ADR 1084).</summary>
public sealed record CiCdStoreSummary(Guid Id, string Name, string DisplayName, string Href);

/// <summary>A CI/CD secret opened with the reader's key: its resource as read, and its content.</summary>
public sealed record OpenedCiCdEntry(PasswordEntryInfo Entry, CiCdEntryContent Content);

/// <summary>
/// The CI/CD store from a client (ADR 1084): stores from the root's <c>cicdStores</c> rel, secrets sealed on the device to
/// the store's audience. A CI/CD store is a sealed store like a Password Store, so the seal, the upload, the open and the
/// re-seal are the shared <see cref="PasswordStoreClient"/>'s; this adds only the CI/CD content and its index.
/// </summary>
public sealed class CiCdStoreClient(ApiCore core, IEnvelopeOpener? opener = null)
{
    private readonly PasswordStoreClient _store = new(core, opener);

    /// <summary>The shared sealed-store client, for what both kinds do alike (a store resource, re-seal, move, delete).</summary>
    public PasswordStoreClient Store => _store;

    /// <summary>The caller's CI/CD stores: those they can decrypt (ADR 0983); empty where the server offers none.</summary>
    public async Task<IReadOnlyList<CiCdStoreSummary>> ListStoresAsync(CancellationToken cancellationToken = default)
    {
        if (!(await core.GetRootLinksAsync(cancellationToken)).TryGetValue("cicdStores", out var href))
        {
            return [];
        }

        var json = await core.Http.GetFromJsonAsync<JsonElement>(href, cancellationToken);
        return [.. json.GetProperty("stores").EnumerateArray().Select(s => new CiCdStoreSummary(
            s.GetProperty("id").GetGuid(),
            s.GetProperty("name").GetString() ?? string.Empty,
            s.GetProperty("displayName").GetString() ?? string.Empty,
            ApiCore.RequireRel(s, "self", "A CI/CD store")))];
    }

    /// <summary>A store with its secrets, each row carrying its plain index (targets, key names, variables).</summary>
    public Task<PasswordFolderInfo> GetStoreAsync(string href, CancellationToken cancellationToken = default) =>
        _store.GetFolderAsync(href, cancellationToken);

    public Task<PasswordEntryInfo> GetEntryAsync(string href, CancellationToken cancellationToken = default) =>
        _store.GetEntryAsync(href, cancellationToken);

    /// <summary>A new secret in <paramref name="store"/>, its values sealed to the store's audience, its index beside them.</summary>
    /// <exception cref="PasswordEntryCreationWithheldException">The store offers no new entries to this caller (ADR 0982).</exception>
    /// <exception cref="CiCdEntryInvalidException">The content breaks the shared rules; nothing was sent.</exception>
    public async Task<PasswordEntryInfo> CreateAsync(PasswordFolderInfo store, string name, CiCdEntryContent content, CancellationToken cancellationToken = default)
    {
        if (!store.CanCreateEntries)
        {
            throw new PasswordEntryCreationWithheldException(store.Name, store.NewEntryUnavailable);
        }

        Validate(name, content);
        var sealedBytes = await _store.SealBytesAsync(store.Links, content.ToPayload(), cancellationToken);
        var created = await _store.CreateEmptyEntryAsync(store, name, cancellationToken);
        await _store.UploadVersionAsync(created, sealedBytes, content.IndexParts(), ifMatch: null, commentKind: null, passwordChanged: null, cancellationToken);
        ApiClientSettings.Logger.LogInformation("Created CI/CD secret {EntryId}", created.Id);
        return await _store.GetEntryAsync(created.Links.Href("self")!, cancellationToken);
    }

    /// <summary>Opens a secret with the host's key: its values, beside its plain index.</summary>
    /// <exception cref="EnvelopeNotOpenedException">No key at hand opens it.</exception>
    public async Task<OpenedCiCdEntry> OpenAsync(PasswordEntryInfo entry, CancellationToken cancellationToken = default) =>
        new(entry, CiCdEntryContent.From(entry.CiCd ?? new CiCdIndexInfo([], [], [], []), (await _store.OpenBytesAsync(entry, cancellationToken)).Bytes));

    /// <summary>
    /// Saves an edit as one new version under the tag of the read that filled the form (ADR 0794): values and index in one
    /// commit. A changed value is said, which is what clears "Exposed since".
    /// </summary>
    /// <exception cref="PasswordEntryChangedException">Somebody changed the secret since it was opened.</exception>
    public async Task<PasswordEntryInfo> SaveAsync(OpenedCiCdEntry opened, CiCdEntryContent edited, CancellationToken cancellationToken = default)
    {
        Validate(opened.Entry.Name, edited);
        var sealedBytes = await _store.SealBytesAsync(opened.Entry.Links, edited.ToPayload(), cancellationToken);
        var valueChanged = !opened.Content.ToJson().AsSpan().SequenceEqual(edited.ToJson());
        await _store.UploadVersionAsync(opened.Entry, sealedBytes, edited.IndexParts(), opened.Entry.ETag, commentKind: null, passwordChanged: valueChanged, cancellationToken);
        return await _store.GetEntryAsync(opened.Entry.Links.Href("self")!, cancellationToken);
    }

    /// <summary>
    /// Tells the server what was done with an opened value (ADR 1084): <c>revealed</c>, or <c>applied</c> to a target.
    /// Key names and targets only. Silent where the rel is not offered (an older server), so it never blocks the act.
    /// </summary>
    public async Task ReportAsync(PasswordEntryInfo entry, string action, string key, string? target, CancellationToken cancellationToken = default)
    {
        if (!entry.Links.Has("cicd-report"))
        {
            return;
        }

        using var response = await core.SendRelAsync(entry.Links, "cicd-report", new { action, key, target }, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, $"What was done with '{entry.Name}' could not be recorded.", cancellationToken);
    }

    private static void Validate(string name, CiCdEntryContent content)
    {
        if (content.Problems is { Count: > 0 } problems)
        {
            throw new CiCdEntryInvalidException(name, problems);
        }
    }
}

/// <summary>A CI/CD entry the shared rules refuse, refused before anything is sealed or sent.</summary>
public sealed class CiCdEntryInvalidException(string name, IReadOnlyList<Alcifra.Presentation.CiCdEntryProblem> problems)
    : Exception($"'{name}' cannot be saved: {string.Join(", ", problems)}.")
{
    public IReadOnlyList<Alcifra.Presentation.CiCdEntryProblem> Problems { get; } = problems;
}
