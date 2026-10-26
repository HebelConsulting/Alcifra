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

    // The shared client's "change the audience" (ADR 0983): one save, then the re-seal in the same act, so the new
    // member opens what was there before they joined, and the editor's stale read is refused by name.
    [Fact]
    public Task The_client_saves_an_audience_and_re_seals_in_the_same_act() => WithEnrolmentAsync(async rig =>
    {
        var store = await SharedStoreAsync(rig);
        await rig.Anna.Client.CreateEntryAsync(store, "Wiki", PasswordEntryContent.Of("w1", "team"));
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.Bob.Id}", new { canSee = true, canReadContent = true });

        var audiences = new SimplArchive.ApiClient.ContentAudienceClient(SimplArchive.ApiClient.ApiCore.Over(rig.Anna.Http));
        var seen = await audiences.GetAsync(store.Links);
        var bob = Assert.Single(seen.Candidates, c => c.UserId == rig.Bob.Id);
        var (saved, resealed) = await rig.Anna.Client.ChangeAudienceAsync(store, seen,
            [.. seen.Members.Select(SimplArchive.ApiClient.AudienceChange.Of), SimplArchive.ApiClient.AudienceChange.Of(bob)]);

        Assert.Contains(saved.Recipients, r => r.UserId == rig.Bob.Id);
        Assert.Equal(["Wiki"], resealed!.Resealed);
        var row = Assert.Single((await rig.Bob.Client.GetFolderAsync(store.Links.Href("self")!)).Items);
        Assert.Equal("w1", (await rig.Bob.Client.OpenAsync(await rig.Bob.Client.GetEntryAsync(row.Href))).Content.Password);

        // The read that filled the editor is stale now: a second save from it is refused, never applied blind.
        await Assert.ThrowsAsync<SimplArchive.ApiClient.AudienceChangedException>(() => audiences.SaveAsync(seen, [.. seen.Members.Select(SimplArchive.ApiClient.AudienceChange.Of)]));
        return 0;
    });

    // A Sealed document is filed to its DECLARED audience (ADR 0983): the filer and the members they chose, sealed to
    // the certificates the server answers for them — the envelope's recipients are exactly theirs.
    [Fact]
    public Task A_sealed_document_is_filed_to_the_audience_it_declares() => WithEnrolmentAsync(async rig =>
    {
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.Bob.Id}", new { canSee = true, canReadContent = true });
        var documents = new SimplArchive.ApiClient.DocumentsClient(SimplArchive.ApiClient.ApiCore.Over(rig.Anna.Http), () => throw new InvalidOperationException("no reminders here"));
        var repository = await TestJson.Get(rig.Anna.Http, $"/api/documents/{rig.RepoId}");
        var childrenHref = Links(repository)["children"];

        var bob = new SimplArchive.ApiClient.AudienceCandidateInfo(rig.Bob.Id, null, "Bob");
        var id = await documents.CreateSealedDocumentAsync(childrenHref, $"Board pack {Guid.NewGuid():N}", "minutes"u8.ToArray(), "text/plain", "minutes.txt", [bob]);

        var audience = await TestJson.Get(rig.Anna.Http, $"/api/documents/{id}/audience");
        Assert.Equal(new[] { rig.Anna.Id, rig.Bob.Id }.Order(), audience.GetProperty("recipients").EnumerateArray().Select(r => r.GetProperty("userId").GetGuid()).Order());
        var download = (await TestJson.Get(rig.Anna.Http, $"/api/documents/{id}/versions")).GetProperty("versions").EnumerateArray().Single()
            .GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "download").GetProperty("href").GetString()!;
        using var plain = new HttpClient();
        var recipients = SimplArchive.SealedArchive.CmsRecipients.Of(await plain.GetByteArrayAsync(download));
        Assert.Equal(2, recipients.Count);
        Assert.Contains(recipients, r => r.Is(rig.Anna.Key));
        Assert.Contains(recipients, r => r.Is(rig.Bob.Key));
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

    // A move into another store from the Passwords tab (owner, 2026-10-26): the shared client moves the entry — or a
    // whole folder — and seals what moved to the TARGET's audience in the same act, so the target's member opens it.
    [Fact]
    public Task An_entry_and_a_folder_move_into_another_store_and_are_sealed_to_its_audience() => WithEnrolmentAsync(async rig =>
    {
        var anna = rig.Anna.Client;
        var from = await SharedStoreAsync(rig);
        var otherId = (await TestJson.Post(rig.Anna.Http, $"/api/documents/{rig.RepoId}/children", new { name = "Ops", maskId = WellKnownMaskIds.PasswordStore }))
            .GetProperty("id").GetGuid();
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.Bob.Id}", new { canSee = true, canReadContent = true });
        await DeclareAudienceAsync(rig.Anna.Http, otherId, (rig.Anna.Id, true), (rig.Bob.Id, false));
        var to = await anna.GetFolderAsync((await anna.ListStoresAsync()).Stores.Single(s => s.Id == otherId).Href);

        await anna.CreateEntryAsync(from, "Router", PasswordEntryContent.Of("r1", "admin"));
        var vault = await anna.CreateFolderAsync(from, "Vault");
        await anna.CreateEntryAsync(vault, "Safe", PasswordEntryContent.Of("s1", "ops"));
        var rows = (await anna.GetFolderAsync(from.Links.Href("self")!)).Items;

        var entryOutcome = await anna.MoveIntoAsync(rows.Single(r => r.Name == "Router"), from.StoreId, to);
        var folderOutcome = await anna.MoveIntoAsync(rows.Single(r => r.Name == "Vault"), from.StoreId, to);
        Assert.Equal(["Router"], entryOutcome!.Resealed);
        Assert.Equal(["Safe"], folderOutcome!.Resealed);

        // Bob reads only the target store, and opens both — the moved entry and the one inside the moved folder.
        var bobs = await rig.Bob.Client.GetFolderAsync(to.Links.Href("self")!);
        Assert.False(bobs.NeedsReseal);
        Assert.Equal("r1", (await rig.Bob.Client.OpenAsync(await rig.Bob.Client.GetEntryAsync(bobs.Items.Single(i => i.Name == "Router").Href))).Content.Password);
        var bobsVault = await rig.Bob.Client.GetFolderAsync(bobs.Items.Single(i => i.Name == "Vault").Href);
        Assert.Equal("s1", (await rig.Bob.Client.OpenAsync(await rig.Bob.Client.GetEntryAsync(Assert.Single(bobsVault.Items).Href))).Content.Password);

        // Within one store a move needs no re-seal.
        var stay = await anna.CreateFolderAsync(to, "Stay");
        Assert.Null(await anna.MoveIntoAsync((await anna.GetFolderAsync(to.Links.Href("self")!)).Items.Single(i => i.Name == "Router"), to.StoreId, stay));
        return 0;
    });

    private static async Task<string?> SignAsync(HttpClient caller, Guid folderId, Guid rowId)
    {
        var row = (await TestJson.Get(caller, $"/api/documents/{folderId}/children")).GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == rowId);
        return row.GetProperty("encryptedForCaller").ValueKind == JsonValueKind.Null ? null : row.GetProperty("encryptedForCaller").GetString();
    }
}
