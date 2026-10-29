using Alcifra.ApiClient;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.Domain.Masks;
using Alcifra.Sapass;
using Alcifra.Sapass.Infrastructure;

namespace Alcifra.EndToEndTests;

// Store names parent-qualified as the DAV collections are (ADR 0619): two shared stores may share a name in different
// folders, and each must read differently in the list, in the store's own resource, and to apass's --store. And a
// tenant administrator who sees another user's My Passwords through the personal-space x-ray (ADR 0670) is no longer
// LISTED it: the list holds only what the caller can decrypt (ADR 0983), which is what ended crypt@'s two "My Passwords".
public sealed partial class PasswordStoreTests
{
    [Fact]
    public Task Stores_of_one_name_under_different_parents_read_as_parent_slash_name() => WithEnrolmentAsync(async rig =>
    {
        // A fully provisioned administrator holds the x-ray into personal spaces (ADR 0670), as crypt@ on the demo does.
        await _factory.GrantTenantAdminAsync(rig.AdminEmail);
        await rig.Anna.Client.EnsureMyPasswordsAsync();
        var admin = new PasswordStoreClient(ApiCore.Over(rig.Admin));
        var adminsOwn = await admin.EnsureMyPasswordsAsync();

        // "Team" at the repository's root, and another "Team" in a folder of it; the administrator reads both and Anna,
        // their creator and manager, adds them to both audiences.
        var repoName = (await TestJson.Get(rig.Owner, $"/api/documents/{rig.RepoId}")).GetProperty("name").GetString()!;
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.AdminId}", new { canSee = true, canReadContent = true });
        var atRoot = await SharedStoreAsync(rig);
        var ops = (await TestJson.Post(rig.Anna.Http, $"/api/documents/{rig.RepoId}/children", new { name = "Ops" })).GetProperty("id").GetGuid();
        var inFolder = (await TestJson.Post(rig.Anna.Http, $"/api/documents/{ops}/children", new { name = "Team", maskId = WellKnownMaskIds.PasswordStore }))
            .GetProperty("id").GetGuid();
        await DeclareAudienceAsync(rig.Anna.Http, atRoot.Id, (rig.Anna.Id, true), (rig.AdminId, false));
        await DeclareAudienceAsync(rig.Anna.Http, inFolder, (rig.Anna.Id, true), (rig.AdminId, false));

        var (stores, _) = await admin.ListStoresAsync();
        var mine = Assert.Single(stores, s => s.IsPersonal);
        Assert.Equal(adminsOwn.Id, mine.Id);
        Assert.Equal("Store Admin / My Passwords", mine.Label);
        Assert.Equal("My Passwords", mine.Name);   // the document's own name is unchanged
        Assert.DoesNotContain(stores, s => !s.IsPersonal && s.Name == "My Passwords");   // Anna's: seen, not decryptable
        Assert.Equal($"{repoName} / Team", stores.Single(s => s.Id == atRoot.Id).Label);
        Assert.Equal("Ops / Team", stores.Single(s => s.Id == inFolder).Label);
        Assert.Equal(stores.Count, stores.Select(s => s.Label).Distinct().Count());
        Assert.Equal(mine.Id, stores[0].Id);   // the caller's own first, the rest by their full names
        Assert.Equal([.. stores.Skip(1).Select(s => s.Label).Order(StringComparer.CurrentCultureIgnoreCase)], stores.Skip(1).Select(s => s.Label));

        // The store's own resource reads as the list does; a folder in it keeps its own name.
        Assert.Equal("Ops / Team", (await admin.GetFolderAsync(stores.Single(s => s.Id == inFolder).Href)).Label);
        Assert.Equal("Store Admin / My Passwords", adminsOwn.Label);
        var folder = await rig.Anna.Client.CreateFolderAsync(atRoot, "Mail");
        Assert.Equal("Mail", folder.Label);

        // apass: the full name always, the plain name only where it is unique (or the caller's own).
        using var session = Session.Over(rig.Admin);
        var catalog = new StoreCatalog(session);
        Assert.Equal(inFolder, (await catalog.StoreAsync("Ops / Team", CancellationToken.None)).Id);
        Assert.Equal(adminsOwn.Id, (await catalog.StoreAsync("My Passwords", CancellationToken.None)).Id);
        await Assert.ThrowsAsync<SapassException>(() => catalog.StoreAsync("Anna / My Passwords", CancellationToken.None));
        var refused = await Assert.ThrowsAsync<SapassException>(() => catalog.StoreAsync("Team", CancellationToken.None));
        Assert.Contains("'Ops / Team'", refused.Message);
        return 0;
    });
}
