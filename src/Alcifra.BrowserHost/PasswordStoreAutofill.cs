using Alcifra.ApiClient.PasswordStore;

namespace Alcifra.BrowserHost;

/// <summary>
/// The autofill operations over a password store, ONCE for both hosts: a browser host is a browser host whether the
/// desktop or apass runs it, and two copies of "match on the index, open with the key, save to My Passwords" would
/// drift on exactly the edge the feature cannot afford — which entry a secret is written to.
/// </summary>
public static class PasswordStoreAutofill
{
    /// <summary>
    /// The entries whose indexed site (or app) matches, in every store the caller may read. Names only: nothing is
    /// opened, so no key and no PIN (ADR 0964/0973).
    /// </summary>
    public static async Task<IReadOnlyList<AutofillMatch>> MatchAsync(
        PasswordStoreClient passwords, string site, string? app, CancellationToken cancellationToken)
    {
        var wanted = NormaliseSite(site);
        var matches = new List<AutofillMatch>();

        var (stores, _) = await passwords.ListStoresAsync(cancellationToken);
        foreach (var summary in stores)
        {
            var store = await passwords.GetFolderAsync(summary.Href, cancellationToken);
            var href = store.Links.Href("descendants")
                ?? throw new InvalidOperationException(
                    "The store offers no descendant listing; the installation is older than this client.");
            var rows = PasswordTree.WithPaths(store.Id, await passwords.AllDescendantsAsync(href, cancellationToken));

            matches.AddRange(rows
                .Where(r => !r.IsFolder
                    && ((r.Site is { } siteValue && string.Equals(siteValue, wanted, StringComparison.OrdinalIgnoreCase))
                        || (app is { Length: > 0 } package && string.Equals(r.App, package, StringComparison.Ordinal))))
                .Select(r => new AutofillMatch(r.Id, r.Name, r.Path, r.UserName)));
        }

        return matches;
    }

    /// <summary>The chosen entry's credentials, opened with the caller's key; null when the entry is gone.</summary>
    public static async Task<AutofillCredentials?> OpenAsync(
        PasswordStoreClient passwords, Guid entryId, CancellationToken cancellationToken)
    {
        if (await FindAsync(passwords, entryId, cancellationToken) is not { } row)
        {
            return null;
        }

        var opened = await passwords.OpenAsync(await passwords.GetEntryAsync(row.Href, cancellationToken), cancellationToken);
        return new AutofillCredentials(
            opened.Content.Password,
            opened.Content.UserName,
            [.. opened.Content.Fields.Select(f => new AutofillField(f.Name, f.Value))]);
    }

    /// <summary>
    /// Creates the entry a submitted form describes in My Passwords, or updates the entry the browser named — under
    /// THAT entry's precondition, so a change made meanwhile is a refusal rather than an overwrite (ADR 0794).
    /// </summary>
    public static async Task<AutofillSaved> SaveAsync(
        PasswordStoreClient passwords, AutofillSave save, CancellationToken cancellationToken)
    {
        if (save.EntryId is { } entryId && await FindAsync(passwords, entryId, cancellationToken) is { } row)
        {
            var opened = await passwords.OpenAsync(await passwords.GetEntryAsync(row.Href, cancellationToken), cancellationToken);
            var saved = await passwords.SaveEntryAsync(
                opened,
                opened.Content with { Password = save.Password },
                cancellationToken);
            return new AutofillSaved(saved.Id, saved.Name, row.Path);
        }

        var mine = await passwords.EnsureMyPasswordsAsync(cancellationToken);
        var entry = await passwords.CreateEntryAsync(
            mine,
            save.Site,
            PasswordEntryContent.Of(save.Password, save.UserName, $"https://{NormaliseSite(save.Site)}"),
            cancellationToken);
        return new AutofillSaved(entry.Id, entry.Name, save.Site);
    }

    /// <summary>A site as the index stores it (ADR 0973): the host, lowercased, without a leading www.</summary>
    public static string NormaliseSite(string site)
    {
        var trimmed = site.Trim().ToLowerInvariant();
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (trimmed.StartsWith(scheme, StringComparison.Ordinal))
            {
                trimmed = trimmed[scheme.Length..];
            }
        }

        var slash = trimmed.IndexOf('/');
        if (slash >= 0)
        {
            trimmed = trimmed[..slash];
        }

        return trimmed.StartsWith("www.", StringComparison.Ordinal) ? trimmed[4..] : trimmed;
    }

    private static async Task<PasswordTreeEntry?> FindAsync(
        PasswordStoreClient passwords, Guid entryId, CancellationToken cancellationToken)
    {
        var (stores, _) = await passwords.ListStoresAsync(cancellationToken);
        foreach (var summary in stores)
        {
            var store = await passwords.GetFolderAsync(summary.Href, cancellationToken);
            if (store.Links.Href("descendants") is not { } href)
            {
                continue;
            }

            var rows = PasswordTree.WithPaths(store.Id, await passwords.AllDescendantsAsync(href, cancellationToken));
            if (PasswordTree.FindOrNull(rows, rows.FirstOrDefault(r => r.Id == entryId)?.Path ?? "\u0000") is { } row)
            {
                return row;
            }
        }

        return null;
    }
}
