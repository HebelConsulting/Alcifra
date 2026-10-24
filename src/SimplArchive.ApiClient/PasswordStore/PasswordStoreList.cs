using System.Globalization;
using Microsoft.Extensions.Logging;
using SimplArchive.Localization;

namespace SimplArchive.ApiClient.PasswordStore;

/// <summary>A store row of the Passwords page or tab: a store, or My Passwords before it exists (it is created when first opened).</summary>
/// <param name="Href">The store's contents; null for My Passwords not yet created.</param>
public sealed record PasswordStoreRow(string Name, bool IsPersonal, string? Href);

/// <summary>
/// The Passwords page (Mobile ADR 0037, ADR 0964; both native clients since ADR 0968): My Passwords first — created on first use through the <c>my-passwords</c>
/// rel — then the shared stores the user reads, by name. Everything is reached by the rels the server advertised.
/// </summary>
public sealed class PasswordStoreList(PasswordStoreClient client)
{
    /// <summary>Whether this installation has the password store at all: its root rel.</summary>
    public static async Task<bool> OfferedAsync(ApiCore core, CancellationToken cancellationToken = default) =>
        (await core.GetRootLinksAsync(cancellationToken)).ContainsKey("passwordStores");

    /// <summary>Calls <paramref name="offered"/> once the installation says it has the store; quietly nothing where it does
    /// not, or cannot be asked now (the home page asks again on its next start).</summary>
    public static async Task WhenOfferedAsync(ApiCore core, Action offered)
    {
        try
        {
            if (await OfferedAsync(core))
            {
                offered();
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or SessionEndedException
            or System.Text.Json.JsonException)
        {
            ApiClientSettings.Logger.LogDebug(e, "Whether the installation offers the password store could not be read; no Passwords tile");
        }
    }

    public async Task<IReadOnlyList<PasswordStoreRow>> RowsAsync(CancellationToken cancellationToken = default)
    {
        var (stores, offersMyPasswords) = await client.ListStoresAsync(cancellationToken);
        return Rows(stores, offersMyPasswords);
    }

    /// <summary>My Passwords first (or the row that creates it), then every other store by name.</summary>
    public static IReadOnlyList<PasswordStoreRow> Rows(IReadOnlyList<PasswordStoreInfo> stores, bool offersMyPasswords)
    {
        var personal = stores.FirstOrDefault(s => s.IsPersonal) is { } mine
            ? [new PasswordStoreRow(mine.Name, true, mine.Href)]
            : offersMyPasswords ? new[] { new PasswordStoreRow(Strings.Get("PwMyPasswords"), true, null) } : [];
        return
        [
            .. personal,
            .. stores.Where(s => !s.IsPersonal)
                .OrderBy(s => s.Name, StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true))
                .Select(s => new PasswordStoreRow(s.Name, false, s.Href)),
        ];
    }

    /// <summary>
    /// The entry's DOCUMENT, as the offline copy keeps one the user opened (Mobile ADR 0037, ADR 0931): its <c>document</c>
    /// rel as the address the copy exports from; null where the row advertises none.
    /// </summary>
    public static LinkMap? DocumentLinks(PasswordItemInfo row) =>
        row.Links.Href("document") is { } document ? LinkMap.FromHrefs(new Dictionary<string, string> { ["self"] = document }) : null;

    /// <summary>The store's contents; My Passwords is created here the first time.</summary>
    public Task<PasswordFolderInfo> OpenAsync(PasswordStoreRow row, CancellationToken cancellationToken = default) =>
        row.Href is { } href ? client.GetFolderAsync(href, cancellationToken) : client.EnsureMyPasswordsAsync(cancellationToken);
}
