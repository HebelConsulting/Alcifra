using SimplArchive.ApiClient;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.Domain.Masks;
using SimplArchive.Sapass.Infrastructure;

namespace SimplArchive.EndToEndTests;

// The tool's OWN tree walk against a real installation: the descendants listing, its paging, the store lookup by
// name, and the paths a person types. The console layer and the stream split are covered in UnitTests; this is the
// wire half — and it calls the same StoreCatalog a command calls, not a replay of its HTTP.
//
// Folders only: an ENTRY needs a sealed first version, which needs an enrolled certificate (PasswordStoreTests).
// The listing is the same query either way, and its entry rows are covered there.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class SapassCatalogTests
{
    private readonly E2EApiFactory _factory;

    public SapassCatalogTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_tree_walk_resolves_a_store_by_name_and_builds_folder_paths()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"sapass-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "sp-1234", "Sapass Person", canManageRepositories: true);
        using var http = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "sp-1234"));

        var repoId = (await TestJson.Post(http, "/api/repositories", new { name = $"SP {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        var storeId = (await TestJson.Post(http, $"/api/documents/{repoId}/children",
            new { name = "Team", maskId = WellKnownMaskIds.PasswordStore })).GetProperty("id").GetGuid();

        var passwords = new PasswordStoreClient(ApiCore.Over(http));
        var store = await passwords.GetFolderAsync($"/api/password-folders/{storeId}");
        var mail = await passwords.CreateFolderAsync(store, "Mail");
        var banking = await passwords.CreateFolderAsync(mail, "Banking");

        using var session = Session.Over(http);
        var catalog = new StoreCatalog(session);
        var resolved = await catalog.StoreAsync("Team", CancellationToken.None);
        var rows = await catalog.RowsAsync(resolved, refresh: true, CancellationToken.None);

        Assert.Equal("Mail", rows.Single(r => r.Id == mail.Id).Path);
        Assert.Equal("Mail/Banking", rows.Single(r => r.Id == banking.Id).Path);
        Assert.Equal(["Banking"], PasswordTree.Children(rows, "Mail").Select(r => r.Name));
    }
}
