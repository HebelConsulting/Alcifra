using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.ApiClient;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Sapass;
using SimplArchive.Sapass.Infrastructure;

namespace SimplArchive.EndToEndTests;

// ADR 0982: a new password entry is offered only where what is saved can be sealed so its author can read it back. The
// server withholds the `password-entries` rel and says why (`newEntryUnavailableReason`); folders stay offered; the
// create refuses by the same rule before anything is written; and every client in this repository follows it — the
// shared client refuses before sealing, sapass before the secret is read.
public sealed partial class PasswordStoreTests
{
    [Fact]
    public Task A_reader_without_an_enrolled_key_is_offered_folders_but_not_entries_and_told_why() => WithEnrolmentAsync(async rig =>
    {
        var team = await SharedStoreAsync(rig);
        var (carolEmail, carol, carolToken) = await KeylessReaderAsync(rig);

        // Anna holds a key: the rel rides, and no reason.
        var annas = await TestJson.Get(rig.Anna.Http, team.Links.Href("self")!);
        Assert.Contains("password-entries", Links(annas).Keys);
        Assert.Equal(JsonValueKind.Null, annas.GetProperty("newEntryUnavailableReason").ValueKind);

        // Carol reads the store and may create in it, but holds no key: folders yes, entries no, and why.
        var carols = await TestJson.Get(carol, team.Links.Href("self")!);
        Assert.DoesNotContain("password-entries", Links(carols).Keys);
        Assert.Contains("password-folders", Links(carols).Keys);
        Assert.Equal("enrolment-required", carols.GetProperty("newEntryUnavailableReason").GetString());

        // The create refuses by the same rule, before anything is written.
        var entriesHref = Links(annas)["password-entries"];
        using (var refused = await carol.PostAsJsonAsync(entriesHref, new { name = "Carol's secret" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal("PASSWORD_ENTRY_ENROLMENT_REQUIRED",
                (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        }

        Assert.False(await AnyDocumentNamedAsync("Carol's secret"));

        // A folder is not sealed, so it is still hers to create.
        using (var folder = await carol.PostAsJsonAsync(Links(carols)["password-folders"], new { name = $"Carol's folder {Guid.NewGuid():N}" }))
        {
            Assert.Equal(HttpStatusCode.Created, folder.StatusCode);
        }

        // The shared client refuses before sealing or sending, with the reason; sapass before the secret is read.
        var client = new PasswordStoreClient(ApiCore.Over(carol));
        var seen = await client.GetFolderAsync(team.Links.Href("self")!);
        Assert.Equal(NewEntryUnavailability.EnrolmentRequired, seen.NewEntryUnavailable);
        var withheld = await Assert.ThrowsAsync<PasswordEntryCreationWithheldException>(
            () => client.CreateEntryAsync(seen, "Carol's secret", PasswordEntryContent.Of("x")));
        Assert.Equal(NewEntryUnavailability.EnrolmentRequired, withheld.Reason);
        using (var session = Session.Over(_factory.CreateAuthedClient(carolToken)))   // the session owns its client
        {
            var refusal = await Assert.ThrowsAsync<SapassException>(() => EntryWriter.StoreForNewEntryAsync(session, "Team", CancellationToken.None));
            Assert.StartsWith("Enrol a hardware key first", refusal.Message);
        }

        // Once she enrols a key, she is offered entries — and what she saves is sealed to her.
        var key = NewKey("Carol", carolEmail);
        await TestJson.Post(carol, "/api/certificates", new { certificatePem = key.ExportCertificatePem(), label = "Carol's key" });
        var enrolled = await TestJson.Get(carol, team.Links.Href("self")!);
        Assert.Contains("password-entries", Links(enrolled).Keys);
        Assert.Equal(JsonValueKind.Null, enrolled.GetProperty("newEntryUnavailableReason").ValueKind);
        return 0;
    });

    // An administrator reads a store through the bypass, which makes nobody a reader (ADR 0964): New entry is withheld
    // as "not-a-reader" — even with a key — because nothing they saved would be sealed to them.
    [Fact]
    public Task An_administrator_who_is_no_reader_is_not_offered_entries_even_with_a_key() => WithEnrolmentAsync(async rig =>
    {
        var team = await SharedStoreAsync(rig);
        await TestJson.Post(rig.Admin, "/api/certificates", new { certificatePem = NewKey("Admin", rig.AdminEmail).ExportCertificatePem(), label = "Admin's key" });

        var seen = await TestJson.Get(rig.Admin, team.Links.Href("self")!);
        Assert.DoesNotContain("password-entries", Links(seen).Keys);
        Assert.Contains("password-folders", Links(seen).Keys);
        Assert.Equal("not-a-reader", seen.GetProperty("newEntryUnavailableReason").GetString());

        var entriesHref = Links(await TestJson.Get(rig.Anna.Http, team.Links.Href("self")!))["password-entries"];
        using var refused = await rig.Admin.PostAsJsonAsync(entriesHref, new { name = "Admin's secret" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("PASSWORD_ENTRY_NOT_A_READER", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        Assert.False(await AnyDocumentNamedAsync("Admin's secret"));
        return 0;
    });

    // A service account seals to the readers (saconsole's seeding, ADR 0966): offered entries while a reader holds a key,
    // refused as "no-reader-certificates" when none does.
    [Fact]
    public Task A_service_account_is_offered_entries_only_while_some_reader_holds_a_key() => WithEnrolmentAsync(async rig =>
    {
        var api = new SimplArchive.Cli.Infrastructure.SimplArchiveApi(rig.Owner);
        var (storeId, _) = await SimplArchive.Cli.Commands.PasswordStoreSurface.FindOrCreateStoreAsync(api, rig.RepoId, $"Seeded {Guid.NewGuid():N}", CancellationToken.None);
        var seeder = new PasswordStoreClient(ApiCore.Over(rig.Owner));
        var store = await SimplArchive.Cli.Commands.PasswordStoreSurface.StoreAsync(seeder, storeId, CancellationToken.None);
        Assert.True(store.CanCreateEntries);   // Anna, a reader by the repository's grant, holds a key

        await SimplArchive.Cli.Commands.AclBreakInheritanceCommand.BreakAsync(api, storeId, CancellationToken.None);
        (await rig.Owner.DeleteAsync($"/api/documents/{storeId}/acl-entries/users/{rig.Anna.Id}")).EnsureSuccessStatusCode();

        var keyless = await SimplArchive.Cli.Commands.PasswordStoreSurface.StoreAsync(seeder, storeId, CancellationToken.None);
        Assert.False(keyless.CanCreateEntries);
        Assert.Equal(NewEntryUnavailability.NoReaderCertificates, keyless.NewEntryUnavailable);
        using var refused = await rig.Owner.PostAsJsonAsync($"{keyless.Links.Href("self")}/entries", new { name = "Unreadable" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("PASSWORD_STORE_NO_READER_CERTIFICATES", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        Assert.Empty((await SimplArchive.Cli.Commands.PasswordStoreSurface.StoreAsync(seeder, storeId, CancellationToken.None)).Items);
        return 0;
    });

    // A user who reads the repository (and so every store in it) and may create there, with no key enrolled.
    private async Task<(string Email, HttpClient Http, string Token)> KeylessReaderAsync(Rig rig)
    {
        var email = $"pw-carol-{Guid.NewGuid():N}@e2e.local";
        var id = await _factory.SeedUserAsync(rig.TenantId, email, "pwstore-1234", "Carol");
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{id}", new
        {
            canSee = true,
            canReadContent = true,
            canCreateSubItems = true,
        });
        var token = await _factory.GetUserTokenAsync(email, "pwstore-1234");
        return (email, _factory.CreateAuthedClient(token), token);
    }

    private async Task<bool> AnyDocumentNamedAsync(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        return await db.Documents.IgnoreQueryFilters(["TenantFilter", "SoftDeleteFilter"]).AnyAsync(d => d.Name == name);
    }
}
