using Alcifra.ApiClient;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.Domain.Masks;
using Alcifra.Pass.Infrastructure;

namespace Alcifra.EndToEndTests;

// The tool's OWN tree walk against a real installation: the descendants listing, its paging, the store lookup by
// name, and the paths a person types. The console layer and the stream split are covered in UnitTests; this is the
// wire half — and it calls the same StoreCatalog a command calls, not a replay of its HTTP.
//
// Folders only: an ENTRY needs a sealed first version, which needs an enrolled certificate (PasswordStoreTests).
// The listing is the same query either way, and its entry rows are covered there.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class PassCatalogTests
{
    private readonly E2EApiFactory _factory;

    public PassCatalogTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_tree_walk_resolves_a_store_by_name_and_builds_folder_paths()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"apass-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "sp-1234", "Pass Person", canManageRepositories: true);
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

    // Completion answers from the cache before it asks the installation, so a TAB still works with a lapsed session as
    // long as the last command's tree is fresh.
    [Fact]
    public async Task Completion_reads_the_tree_the_last_command_cached_without_a_request()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"apass-cache-{Guid.NewGuid():N}");
        var before = Environment.GetEnvironmentVariable("ALCIFRA_CACHE_DIR");
        Environment.SetEnvironmentVariable("ALCIFRA_CACHE_DIR", cache);
        try
        {
            var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
            var email = $"apass-cache-{Guid.NewGuid():N}@e2e.local";
            await _factory.SeedUserAsync(tenantId, email, "sp-1234", "Pass Cache", canManageRepositories: true);
            using var http = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "sp-1234"));
            var repoId = (await TestJson.Post(http, "/api/repositories", new { name = $"SC {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
            var storeId = (await TestJson.Post(http, $"/api/documents/{repoId}/children",
                new { name = "Vault", maskId = WellKnownMaskIds.PasswordStore })).GetProperty("id").GetGuid();
            var passwords = new PasswordStoreClient(ApiCore.Over(http));
            var mail = await passwords.CreateFolderAsync(await passwords.GetFolderAsync($"/api/password-folders/{storeId}"), "Mail");

            using (var session = Session.Over(http))
            {
                var catalog = new StoreCatalog(session);
                Assert.Null(await catalog.CachedRowsAsync("Vault"));
                await catalog.RowsAsync(await catalog.StoreAsync("Vault", CancellationToken.None), refresh: true, CancellationToken.None);
            }

            // A client whose token the installation refuses: whatever it answers, completion must not need it.
            using var lapsed = _factory.CreateAuthedClient("not-a-token");
            using var expired = Session.Over(lapsed);
            var cached = await new StoreCatalog(expired).CachedRowsAsync("Vault");

            Assert.Equal(mail.Id, Assert.Single(cached!).Id);
            Assert.Null(await new StoreCatalog(expired).CachedRowsAsync("Another"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALCIFRA_CACHE_DIR", before);
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    // `apass whoami` against a real installation: the person and tenant the session acts as, from the root's whoami rel,
    // and their enrolled keys (none here), through the same two reads the command makes.
    [Fact]
    public async Task Whoami_names_the_person_and_tenant_the_session_acts_as()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"apass-who-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "sp-1234", "Pass Whoami");
        using var http = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "sp-1234"));
        using var session = Session.Over(http);

        var identity = await Alcifra.Pass.Commands.WhoAmICommand.ReadAsync(session, CancellationToken.None);

        Assert.Equal("Pass Whoami", identity.Me.UserName);
        Assert.False(string.IsNullOrEmpty(identity.Me.TenantName));
        Assert.Empty(identity.Keys);
        Assert.Contains("none enrolled: a store's entries cannot be opened", Alcifra.Pass.Commands.WhoAmICommand.Describe(identity));
        Assert.Contains("My Passwords", identity.Stores);
    }
}
