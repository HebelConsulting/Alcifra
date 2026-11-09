using Alcifra.ApiClient.PasswordStore;

namespace Alcifra.Pass.Infrastructure;

/// <summary>Finds a CI/CD store and a secret in it by the names a person types (ADR 1084).</summary>
internal static class CiCdStores
{
    /// <summary>The store named <paramref name="name"/> (its own name or its full <c>Parent / Name</c>), with its secrets.</summary>
    public static async Task<PasswordFolderInfo> StoreAsync(Session session, string name, CancellationToken cancellationToken)
    {
        var stores = await session.CiCd.ListStoresAsync(cancellationToken);
        var matches = stores.Where(s => string.Equals(s.DisplayName, name, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } exact
            ? exact
            : [.. stores.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))];
        return matches switch
        {
            [var only] => await session.CiCd.GetStoreAsync(only.Href, cancellationToken),
            [] => throw new PassException(stores.Count == 0
                ? "You can read no CI/CD store here."
                : $"No CI/CD store is named '{name}'. Yours: {string.Join(", ", stores.Select(s => s.DisplayName))}."),
            _ => throw new PassException($"'{name}' names {matches.Count} CI/CD stores; use the full name: {string.Join(", ", matches.Select(s => s.DisplayName))}."),
        };
    }

    /// <summary>The secret named <paramref name="name"/> in <paramref name="store"/>, or null.</summary>
    public static PasswordItemInfo? Secret(PasswordFolderInfo store, string name) =>
        store.Items.FirstOrDefault(i => !i.IsFolder && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
}
