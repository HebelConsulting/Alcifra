using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.Domain.Masks;

namespace SimplArchive.EndToEndTests;

// The audience resource of encrypted content (ADR 0983), over HTTP: the creator is the first manager; only a manager
// changes the audience, and never to leave it without one; members are readers only; a manager is a person; the whole
// list is written under the store's tag; the Passwords list shows a person only what they can decrypt, and a listing
// row says which items they cannot; a folder or entry crosses into another store only for somebody in both audiences.
public sealed partial class PasswordStoreTests
{
    private static async Task<(HttpStatusCode Status, string Body)> PutAudienceAsync(
        HttpClient caller, Guid documentId, object[] members, string? ifMatch = "read")
    {
        if (ifMatch == "read")
        {
            using var read = await caller.GetAsync($"/api/documents/{documentId}/audience");
            ifMatch = read.Headers.ETag?.Tag;
        }

        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/audience")
        {
            Content = JsonContent.Create(new { members }),
        };
        if (ifMatch is not null)
        {
            put.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var answer = await caller.SendAsync(put);
        return (answer.StatusCode, await answer.Content.ReadAsStringAsync());
    }

    [Fact]
    public Task Only_a_manager_changes_the_audience_and_never_to_leave_it_without_one() => WithEnrolmentAsync(async rig =>
    {
        var store = await SharedStoreAsync(rig);
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.Bob.Id}", new { canSee = true, canReadContent = true });

        // The creator is the first manager; Bob, a reader, is a candidate and may not change anything.
        var audience = await TestJson.Get(rig.Anna.Http, $"/api/documents/{store.Id}/audience");
        Assert.True(audience.GetProperty("canManage").GetBoolean());
        var only = Assert.Single(audience.GetProperty("members").EnumerateArray());
        Assert.Equal(rig.Anna.Id, only.GetProperty("userId").GetGuid());
        Assert.True(only.GetProperty("isManager").GetBoolean());
        Assert.Contains(audience.GetProperty("candidates").EnumerateArray(), c => c.GetProperty("userId").GetString() == rig.Bob.Id.ToString());
        var bobs = await TestJson.Get(rig.Bob.Http, $"/api/documents/{store.Id}/audience");
        Assert.False(bobs.GetProperty("canManage").GetBoolean());
        Assert.Empty(bobs.GetProperty("candidates").EnumerateArray());
        var annaAndBob = new object[] { new { userId = rig.Anna.Id, isManager = true }, new { userId = rig.Bob.Id, isManager = false } };
        Assert.Contains("AUDIENCE_CHANGE_REQUIRES_MANAGER", (await PutAudienceAsync(rig.Bob.Http, store.Id, annaAndBob)).Body);

        // The precondition is the store's: missing is 428, stale is 412.
        Assert.Equal((HttpStatusCode)428, (await PutAudienceAsync(rig.Anna.Http, store.Id, annaAndBob, ifMatch: null)).Status);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutAudienceAsync(rig.Anna.Http, store.Id, annaAndBob, ifMatch: "\"" + Guid.NewGuid() + "\"")).Status);

        // Members are readers; a manager is a person; and somebody must stay in charge.
        var carolId = await _factory.SeedUserAsync(rig.TenantId, $"pw-carol-{Guid.NewGuid():N}@e2e.local", "pwstore-1234", "Carol");
        Assert.Contains("AUDIENCE_MEMBER_NOT_A_READER", (await PutAudienceAsync(rig.Anna.Http, store.Id,
            [new { userId = rig.Anna.Id, isManager = true }, new { userId = carolId, isManager = false }])).Body);
        var team = await _factory.SeedGroupWithMemberAsync(rig.TenantId, $"Team {Guid.NewGuid():N}", rig.Bob.Id);
        Assert.Contains("AUDIENCE_MANAGER_MUST_BE_A_USER", (await PutAudienceAsync(rig.Anna.Http, store.Id,
            [new { userId = rig.Anna.Id, isManager = true }, new { groupId = team, isManager = true }])).Body);
        Assert.Contains("AUDIENCE_NEEDS_A_MANAGER", (await PutAudienceAsync(rig.Anna.Http, store.Id,
            [new { userId = rig.Anna.Id, isManager = false }])).Body);

        // A group of readers joins as a member; the store's tag moved, so the read Bob had is stale now.
        var accepted = await PutAudienceAsync(rig.Anna.Http, store.Id, [new { userId = rig.Anna.Id, isManager = true }, new { groupId = team, isManager = false }]);
        Assert.True(accepted.Status == HttpStatusCode.OK, accepted.Body);
        Assert.Contains((await TestJson.Get(rig.Bob.Http, $"/api/documents/{store.Id}/audience")).GetProperty("recipients").EnumerateArray(),
            r => r.GetProperty("userId").GetGuid() == rig.Bob.Id);
        return 0;
    });

    [Fact]
    public Task A_personal_store_has_its_owner_as_its_fixed_audience() => WithEnrolmentAsync(async rig =>
    {
        var mine = await rig.Anna.Client.EnsureMyPasswordsAsync();
        var audience = await TestJson.Get(rig.Anna.Http, $"/api/documents/{mine.Id}/audience");
        Assert.True(audience.GetProperty("isPersonal").GetBoolean());
        Assert.False(audience.GetProperty("canManage").GetBoolean());
        Assert.Equal(rig.Anna.Id, Assert.Single(audience.GetProperty("recipients").EnumerateArray()).GetProperty("userId").GetGuid());
        Assert.Contains("AUDIENCE_FIXED_IN_PERSONAL_SPACE", (await PutAudienceAsync(rig.Anna.Http, mine.Id, [new { userId = rig.Anna.Id, isManager = true }])).Body);
        return 0;
    });

    [Fact]
    public Task A_person_is_listed_only_the_stores_they_can_decrypt_and_rows_say_which_they_cannot() => WithEnrolmentAsync(async rig =>
    {
        var store = await SharedStoreAsync(rig);
        await rig.Anna.Client.CreateEntryAsync(store, "Wiki", PasswordEntryContent.Of("w1", "team"));
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.Bob.Id}", new { canSee = true, canReadContent = true });

        // Bob sees the store in the Repositories tree, marked, and not in his Passwords list.
        Assert.DoesNotContain((await rig.Bob.Client.ListStoresAsync()).Stores, s => s.Id == store.Id);
        Assert.Equal("not-you", await SignAsync(rig.Bob.Http, rig.RepoId, store.Id));
        Assert.Equal("you", await SignAsync(rig.Anna.Http, rig.RepoId, store.Id));

        // Added, he is listed it; the entry is "not yet" his until a member re-seals it.
        await DeclareAudienceAsync(rig.Anna.Http, store.Id, (rig.Anna.Id, true), (rig.Bob.Id, false));
        Assert.Contains((await rig.Bob.Client.ListStoresAsync()).Stores, s => s.Id == store.Id);
        var wiki = (await rig.Anna.Client.GetFolderAsync(store.Links.Href("self")!)).Items.Single().Id;
        Assert.Equal("not-yet-you", await SignAsync(rig.Bob.Http, store.Id, wiki));
        await rig.Anna.Client.ResealAsync(await rig.Anna.Client.GetFolderAsync(store.Links.Href("self")!));
        Assert.Equal("you", await SignAsync(rig.Bob.Http, store.Id, wiki));
        return 0;
    });

    [Fact]
    public Task An_administrator_may_only_appoint_a_manager_to_a_store_without_one() => WithEnrolmentAsync(async rig =>
    {
        // A service account creates the store: its readers (Anna) are its audience, with no manager.
        var api = new SimplArchive.Cli.Infrastructure.SimplArchiveApi(rig.Owner);
        var (storeId, _) = await SimplArchive.Cli.Commands.PasswordStoreSurface.FindOrCreateStoreAsync(api, rig.RepoId, $"Orphan {Guid.NewGuid():N}", CancellationToken.None);
        var orphan = await TestJson.Get(rig.Admin, $"/api/documents/{storeId}/audience");
        Assert.True(orphan.GetProperty("canAppointManager").GetBoolean());
        Assert.False(orphan.GetProperty("canManage").GetBoolean());

        // Removing somebody, or adding a member who manages nothing, is not an appointment.
        Assert.Contains("AUDIENCE_ADMINISTRATOR_MAY_ONLY_APPOINT", (await PutAudienceAsync(rig.Admin, storeId, [])).Body);
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.Bob.Id}", new { canSee = true, canReadContent = true });
        Assert.Contains("AUDIENCE_ADMINISTRATOR_MAY_ONLY_APPOINT", (await PutAudienceAsync(rig.Admin, storeId,
            [new { userId = rig.Anna.Id, isManager = false }, new { userId = rig.Bob.Id, isManager = false }])).Body);

        // Appointing Anna is, and audited; after it the administrator has no say.
        Assert.Equal(HttpStatusCode.OK, (await PutAudienceAsync(rig.Admin, storeId, [new { userId = rig.Anna.Id, isManager = true }])).Status);
        Assert.False((await TestJson.Get(rig.Admin, $"/api/documents/{storeId}/audience")).GetProperty("canAppointManager").GetBoolean());
        Assert.Contains("AUDIENCE_CHANGE_REQUIRES_MANAGER", (await PutAudienceAsync(rig.Admin, storeId, [new { userId = rig.Bob.Id, isManager = true }])).Body);
        return 0;
    });

    [Fact]
    public Task An_entry_moves_into_another_store_only_for_somebody_in_both_audiences() => WithEnrolmentAsync(async rig =>
    {
        var from = await SharedStoreAsync(rig);
        var otherId = (await TestJson.Post(rig.Anna.Http, $"/api/documents/{rig.RepoId}/children", new { name = "Other", maskId = WellKnownMaskIds.PasswordStore }))
            .GetProperty("id").GetGuid();
        var to = await rig.Anna.Client.GetFolderAsync((await rig.Anna.Client.ListStoresAsync()).Stores.Single(s => s.Id == otherId).Href);
        var entry = await rig.Anna.Client.CreateEntryAsync(from, "Wiki", PasswordEntryContent.Of("w1", "team"));

        // Bob may move things in the repository and is in the source's audience only.
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.Bob.Id}",
            new { canSee = true, canReadContent = true, canMove = true, canCreateSubItems = true });
        await DeclareAudienceAsync(rig.Anna.Http, from.Id, (rig.Anna.Id, true), (rig.Bob.Id, false));
        var bobRead = await rig.Bob.Http.GetAsync($"/api/documents/{entry.Id}");
        using (var move = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{entry.Id}/parent") { Content = JsonContent.Create(new { parentId = to.Id }) })
        {
            move.Headers.TryAddWithoutValidation("If-Match", bobRead.Headers.ETag!.Tag);
            using var refused = await rig.Bob.Http.SendAsync(move);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains("MOVE_REQUIRES_BOTH_AUDIENCES", await refused.Content.ReadAsStringAsync());
        }

        // Anna is in both, and may.
        var annaRead = await rig.Anna.Http.GetAsync($"/api/documents/{entry.Id}");
        using var allowed = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{entry.Id}/parent") { Content = JsonContent.Create(new { parentId = to.Id }) };
        allowed.Headers.TryAddWithoutValidation("If-Match", annaRead.Headers.ETag!.Tag);
        using var moved = await rig.Anna.Http.SendAsync(allowed);
        Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());
        return 0;
    });

    // Unattended seeding (owner, 2026-10-26): the service account that created a store writes its whole audience while no
    // person manages it — the nightly demo seed's case — and loses that say as soon as the audience names a manager.
    [Fact]
    public Task A_service_account_seeds_the_audience_of_a_store_nobody_manages_yet() => WithEnrolmentAsync(async rig =>
    {
        var api = new SimplArchive.Cli.Infrastructure.SimplArchiveApi(rig.Owner);
        var (storeId, _) = await SimplArchive.Cli.Commands.PasswordStoreSurface.FindOrCreateStoreAsync(api, rig.RepoId, $"Seeded {Guid.NewGuid():N}", CancellationToken.None);
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.Bob.Id}", new { canSee = true, canReadContent = true });

        Assert.True((await TestJson.Get(rig.Owner, $"/api/documents/{storeId}/audience")).GetProperty("canManage").GetBoolean());
        var seeded = await PutAudienceAsync(rig.Owner, storeId, [new { userId = rig.Anna.Id, isManager = true }, new { userId = rig.Bob.Id, isManager = false }]);
        Assert.True(seeded.Status == HttpStatusCode.OK, seeded.Body);

        // Anna manages it now: the seed's say is over.
        Assert.False((await TestJson.Get(rig.Owner, $"/api/documents/{storeId}/audience")).GetProperty("canManage").GetBoolean());
        Assert.Contains("AUDIENCE_CHANGE_REQUIRES_MANAGER", (await PutAudienceAsync(rig.Owner, storeId, [new { userId = rig.Bob.Id, isManager = true }])).Body);
        return 0;
    });

    private static async Task<string?> SignAsync(HttpClient caller, Guid folderId, Guid rowId)
    {
        var row = (await TestJson.Get(caller, $"/api/documents/{folderId}/children")).GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == rowId);
        return row.GetProperty("encryptedForCaller").ValueKind == JsonValueKind.Null ? null : row.GetProperty("encryptedForCaller").GetString();
    }
}
