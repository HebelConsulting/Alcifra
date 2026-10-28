using System.Text.Json;
using SimplArchive.ApiClient;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.Cli.Commands;

/// <summary>
/// What the <c>passwords</c> commands do, kept out of the commands so a test can drive it (ADR 0966): find or create a
/// shared store, find a store the caller may see, and find or create an entry sealed on this machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>A shared store is an ordinary child of a folder</b> (ADR 0964: "a store may be created anywhere a folder may"), so
/// it is created the way every client creates one: through the <c>admits</c> entry the server lists for the parent, whose
/// mask id is sent back unread (ADR 0543). The tool knows no mask id; it picks the entry by the mask's name.
/// </para>
/// <para>
/// <b>An entry is sealed HERE, never on the server</b>, by the shared client the phone uses
/// (<see cref="PasswordStoreClient"/>) — moved, not copied. The server only ever sees the envelope.
/// </para>
/// </remarks>
public static class PasswordStoreSurface
{
    /// <summary>The name the Password Store mask is listed under in a folder's <c>admits</c> (ADR 0964's seeded mask).</summary>
    internal const string StoreMaskName = "Password Store";

    /// <summary>The store named <paramref name="name"/> directly under <paramref name="parentId"/>, created when absent.</summary>
    /// <exception cref="CliException">A child of that name is something other than a password store, or the parent
    /// does not offer one to this caller.</exception>
    public static async Task<(Guid Id, bool Created)> FindOrCreateStoreAsync(
        SimplArchiveApi api, Guid parentId, string name, CancellationToken cancellationToken)
    {
        var hypermedia = new Hypermedia(api);
        var parent = await LicenceFiling.ResolveAsync(api, hypermedia, parentId, cancellationToken);
        var parentLinks = Hypermedia.LinksOf(parent);
        var childrenHref = Hypermedia.Href(parentLinks, "children", $"Document {parentId:D}");

        if (await LicenceFiling.FindChildAsync(api, childrenHref, name, cancellationToken) is { } existing)
        {
            return await IsStoreAsync(api, hypermedia, existing, cancellationToken)
                ? (existing, false)
                : throw new CliException(
                    $"'{name}' already exists in {parentId:D} and is not a password store. Choose another name, or "
                    + "remove or rename what is there.");
        }

        var admitted = await AdmitsAsync(api, hypermedia, parentId, parentLinks, cancellationToken);
        var store = admitted.FirstOrDefault(a => a.TryGetProperty("name", out var n) && n.GetString() == StoreMaskName);
        if (store.ValueKind != JsonValueKind.Object)
        {
            throw new CliException(
                $"Document {parentId:D} does not offer a {StoreMaskName} to this caller: either the right to create "
                + "sub-items there is missing, or its mask admits no store. "
                + (admitted.Count == 0
                    ? "It offers nothing to create."
                    : $"It offers: {string.Join(", ", admitted.Select(a => a.GetProperty("name").GetString()))}."));
        }

        var created = await api.PostAsync(
            store.GetProperty("href").GetString()!,
            new { name, maskId = store.GetProperty("maskId").GetGuid() },
            cancellationToken);
        return (created.GetProperty("id").GetGuid(), true);
    }

    /// <summary>The store <paramref name="storeId"/> as the caller sees it, found in the root's <c>passwordStores</c>.</summary>
    public static async Task<PasswordFolderInfo> StoreAsync(
        PasswordStoreClient client, Guid storeId, CancellationToken cancellationToken)
    {
        var (stores, _) = await Translated(() => client.ListStoresAsync(cancellationToken));
        var store = stores.FirstOrDefault(s => s.Id == storeId)
            ?? throw new CliException(
                $"No password store {storeId:D} is visible to the signed-in principal. It needs at least the right to "
                + "see the store; 'alcifra passwords store create' prints a store's id.");
        return await Translated(() => client.GetFolderAsync(store.Href, cancellationToken));
    }

    /// <summary>
    /// The entry named <paramref name="name"/> in the store, created and sealed to the store's readers when absent. An
    /// entry already there is left alone (no new version), unless an earlier run left it without content, which is
    /// then completed.
    /// </summary>
    public static async Task<(Guid Id, EntryOutcome Outcome)> FindOrCreateEntryAsync(
        PasswordStoreClient client, Guid storeId, string name, PasswordEntryContent content, CancellationToken cancellationToken)
    {
        var store = await StoreAsync(client, storeId, cancellationToken);
        var existing = store.Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.Ordinal));
        if (existing is { IsFolder: true })
        {
            throw new CliException($"'{name}' in '{store.Name}' is a folder, not an entry. Choose another name.");
        }

        if (existing is not null)
        {
            var entry = await Translated(() => client.GetEntryAsync(existing.Href, cancellationToken));
            if (entry.HasContent)
            {
                return (entry.Id, EntryOutcome.Unchanged);
            }

            await RequireRecipientsAsync(client, store, cancellationToken);
            return ((await Translated(() => client.CompleteEntryAsync(entry, content, cancellationToken))).Id, EntryOutcome.Completed);
        }

        // The store says whether a new entry is offered, and why not (ADR 0982); the right alone is not enough.
        if (!store.CanCreateEntries)
        {
            await RequireRecipientsAsync(client, store, cancellationToken);   // names the readers without a key, where that is why
            throw new CliException(store.NewEntryUnavailable == NewEntryUnavailability.None
                ? $"The signed-in principal may not create entries in '{store.Name}' (it lacks the right to create sub-items there)."
                : new PasswordEntryCreationWithheldException(store.Name, store.NewEntryUnavailable).Message);
        }

        await RequireRecipientsAsync(client, store, cancellationToken);   // a server from before ADR 0982 offers the rel regardless
        return ((await Translated(() => client.CreateEntryAsync(store, name, content, cancellationToken))).Id, EntryOutcome.Created);
    }

    /// <summary>What <see cref="FindOrCreateEntryAsync"/> did.</summary>
    public enum EntryOutcome
    {
        Created,

        /// <summary>The entry existed without content (an interrupted run); its first content was filed now.</summary>
        Completed,

        /// <summary>The entry existed with content; nothing was written.</summary>
        Unchanged,
    }

    /// <summary>The refusal when nothing can be sealed: nobody who reads the store holds an enrolled certificate.</summary>
    internal static string NoRecipients(string storeName, IReadOnlyList<(Guid UserId, string DisplayName)> readersWithoutCertificate) =>
        $"Nothing can be sealed to '{storeName}': none of its readers holds an enrolled reader certificate, so an entry "
        + "would be readable by nobody. "
        + (readersWithoutCertificate.Count == 0
            ? "It has no readers at all: grant users read on it first (alcifra acl set/grant)."
            : $"Readers without a certificate: {string.Join(", ", readersWithoutCertificate.Select(r => r.DisplayName))}. "
              + "Enrol a certificate for at least one of them first.");

    // Checked before the entry is created, so a refusal leaves no empty entry behind. The store already says how many
    // certificates its readers hold; the recipients are read only to name who lacks one.
    private static async Task RequireRecipientsAsync(PasswordStoreClient client, PasswordFolderInfo store, CancellationToken cancellationToken)
    {
        if (store.ReaderCertificateCount > 0)
        {
            return;
        }

        var recipients = await Translated(() => client.GetRecipientsAsync(store.Links, cancellationToken));
        throw new CliException(NoRecipients(store.Name, recipients.ReadersWithoutCertificate));
    }

    // The shared client speaks in its own exceptions; this tool's handler reports a CliException as a sentence and
    // anything else as a bug, so each is turned into the sentence it already carries.
    private static async Task<T> Translated<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception e) when (e is PasswordStoreHasNoRecipientsException or PasswordEntryCreationWithheldException)
        {
            throw new CliException(e.Message);
        }
        catch (Exception e) when (e is ApiActionException or DocumentNameTakenException or PasswordEntryChangedException or InvalidOperationException)
        {
            throw new CliException(e.Message);
        }
    }

    private static async Task<bool> IsStoreAsync(SimplArchiveApi api, Hypermedia hypermedia, Guid id, CancellationToken cancellationToken)
    {
        var listing = await api.GetAsync(await hypermedia.RootHrefAsync("passwordStores", cancellationToken), cancellationToken);
        return listing.TryGetProperty("stores", out var stores)
            && stores.EnumerateArray().Any(s => s.GetProperty("id").GetGuid() == id);
    }

    // A folder's admits ride on its ROW, in the listing it appears in: a repository's in the root's `repositories`, any
    // other folder's in its parent's `children` (#673). Read from there, never assembled.
    private static async Task<IReadOnlyList<JsonElement>> AdmitsAsync(
        SimplArchiveApi api, Hypermedia hypermedia, Guid parentId, IReadOnlyDictionary<string, string> parentLinks,
        CancellationToken cancellationToken)
    {
        var (first, rows) = parentLinks.TryGetValue("parent", out var grandparent)
            ? (Hypermedia.Href(await hypermedia.LinksOfAsync(grandparent, cancellationToken), "children", "The parent's parent"), "children")
            : (await hypermedia.RootHrefAsync("repositories", cancellationToken), "repositories");

        for (string? page = first; page is not null;)
        {
            var listing = await api.GetAsync(page, cancellationToken);
            if (listing.TryGetProperty(rows, out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in items.EnumerateArray().Where(r => r.GetProperty("id").GetGuid() == parentId))
                {
                    return row.TryGetProperty("admits", out var admits) && admits.ValueKind == JsonValueKind.Array
                        ? [.. admits.EnumerateArray()]
                        : [];
                }
            }

            page = Hypermedia.LinksOf(listing).GetValueOrDefault("next");
        }

        return [];
    }
}
