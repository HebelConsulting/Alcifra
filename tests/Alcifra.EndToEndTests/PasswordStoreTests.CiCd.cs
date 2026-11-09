using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.ApiClient;
using Alcifra.ApiClient.CiCd;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.EndToEndTests;

// The CI/CD store (#2269, ADR 1084), through the shared client the desktop and apass use: a sealed store like the Password
// Store, flat, whose secrets keep their key names, targets and variables as plain index data beside their sealed values.
public sealed partial class PasswordStoreTests
{
    private static readonly byte[] P12 = [0x30, 0x82, 0x01, 0x02, 0x00, 0xFF];

    private static CiCdEntryContent SigningEntry() => new(
        ["github:HebelConsulting/Alcifra", "github:HebelConsulting/Alcifra4Easyname@release"],
        [new CiCdSecretValue("MACOS_CERT_P12_BASE64", P12, IsFile: true), new CiCdSecretValue("MACOS_CERT_PASSWORD", "hunter2"u8.ToArray(), IsFile: false)],
        [new CiCdVariable("MACOS_NOTARIZATION_POSTPONED", "false")]);

    private async Task<(CiCdStoreClient Client, ApiClient.PasswordStore.PasswordFolderInfo Store)> CiCdStoreAsync(Rig rig)
    {
        var created = await TestJson.Post(rig.Anna.Http, $"/api/documents/{rig.RepoId}/children",
            new { name = $"Pipelines {Guid.NewGuid():N}"[..18], maskId = WellKnownMaskIds.CiCdStore });
        var client = new CiCdStoreClient(ApiCore.Over(rig.Anna.Http), new KeyOpener(rig.Anna.Key));
        var summary = Assert.Single(await client.ListStoresAsync(), s => s.Id == created.GetProperty("id").GetGuid());
        return (client, await client.GetStoreAsync(summary.Href));
    }

    [Fact]
    public Task A_cicd_secret_keeps_its_names_plain_and_its_values_sealed_and_reads_back() => WithEnrolmentAsync(async rig =>
    {
        var (client, store) = await CiCdStoreAsync(rig);

        // A flat store: entries, no folders.
        Assert.True(store.CanCreateEntries);
        Assert.False(store.CanCreateFolders);

        var entry = await client.CreateAsync(store, "Developer ID certificate", SigningEntry());

        // The listing says what each secret sets, with nothing opened: what a dry run compares.
        var row = Assert.Single((await client.GetStoreAsync(store.Links.Href("self")!)).Items);
        Assert.Equal(["MACOS_CERT_P12_BASE64", "MACOS_CERT_PASSWORD"], row.CiCd!.SecretKeys);
        Assert.Equal(["MACOS_CERT_P12_BASE64"], row.CiCd.FileKeys);
        Assert.Equal(["MACOS_NOTARIZATION_POSTPONED=false"], row.CiCd.Variables);
        Assert.Equal(SigningEntry().Targets, row.CiCd.Targets);

        // The values come back only through the key, the file as its raw bytes.
        var opened = await client.OpenAsync(entry);
        Assert.Equal(P12, opened.Content.Secrets.Single(s => s.Name == "MACOS_CERT_P12_BASE64").Value);
        Assert.Equal("hunter2", Encoding.UTF8.GetString(opened.Content.Secrets.Single(s => s.Name == "MACOS_CERT_PASSWORD").Value));

        // Neither value is anywhere in the index data the server holds.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            var values = await db.FieldValues.IgnoreQueryFilters().Where(v => v.DocumentId == entry.Id).Select(v => v.Value).ToListAsync();
            Assert.DoesNotContain(values, v => v.Contains("hunter2", StringComparison.Ordinal));

            // Handing out the sealed content is the delivery the server can see, audited by entry.
            Assert.True(await db.AuditEvents.IgnoreQueryFilters().AnyAsync(a => a.Action == "CiCdSecret.Read" && a.TargetId == entry.Id));
            Assert.True(await db.AuditEvents.IgnoreQueryFilters().AnyAsync(a => a.Action == "CiCdSecret.Created" && a.TargetId == entry.Id));
        }

        // An edit is one version: a new password and one key fewer, under the tag of what was opened.
        var edited = opened.Content with
        {
            Secrets = [new CiCdSecretValue("MACOS_CERT_PASSWORD", "correct horse"u8.ToArray(), IsFile: false)],
        };
        await client.SaveAsync(opened, edited);
        var again = await client.OpenAsync(await client.GetEntryAsync(entry.Links.Href("self")!));
        Assert.Equal(["MACOS_CERT_PASSWORD"], again.Entry.CiCd!.SecretKeys);
        Assert.Empty(again.Entry.CiCd.FileKeys);
        Assert.Equal("correct horse", Encoding.UTF8.GetString(again.Content.Secrets.Single().Value));

        // Saving over a read somebody else has moved past is refused, not merged.
        await Assert.ThrowsAsync<ApiClient.PasswordStore.PasswordEntryChangedException>(() => client.SaveAsync(opened, edited));
        return 0;
    });

    [Fact]
    public Task The_generic_document_door_makes_no_cicd_secret() => WithEnrolmentAsync(async rig =>
    {
        var (_, store) = await CiCdStoreAsync(rig);
        var children = $"/api/documents/{store.Id}/children";

        using (var named = await rig.Anna.Http.PostAsJsonAsync(children, new { name = "Named secret", maskId = WellKnownMaskIds.CiCdSecret }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, named.StatusCode);
            Assert.Equal("CICD_SECRET_USES_ITS_STORE", (await named.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        }

        using (var implied = await rig.Anna.Http.PostAsJsonAsync(children, new { name = "Implied secret" }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, implied.StatusCode);
            Assert.Equal("CICD_SECRET_USES_ITS_STORE", (await implied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        }

        Assert.False(await AnyDocumentNamedAsync("Named secret"));
        Assert.False(await AnyDocumentNamedAsync("Implied secret"));
        return 0;
    });

    // The server holds the shared rules too: index data a client did not check is refused before it is stored.
    [Fact]
    public Task Index_data_the_rules_refuse_is_refused_by_the_server() => WithEnrolmentAsync(async rig =>
    {
        var (_, store) = await CiCdStoreAsync(rig);
        var entry = await TestJson.Post(rig.Anna.Http, Links(await TestJson.Get(rig.Anna.Http, store.Links.Href("self")!))["password-entries"],
            new { name = "Hand-made" });
        var version = await TestJson.Post(rig.Anna.Http, Links(entry)["versions"], new { fileExtension = ".p7m" });
        var envelope = ApiClient.PasswordStore.PasswordStoreClient.Seal(
            ApiClient.PasswordStore.PasswordEntryContent.Of("x", "y"), [rig.Anna.Key]);
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(envelope))).EnsureSuccessStatusCode();
        }

        using var finalize = await rig.Anna.Http.PutAsJsonAsync(Links(version)["self"],
            new { ciCd = new { targets = new[] { "github:o/r" }, secretKeys = new[] { "GITHUB_TOKEN" }, fileKeys = Array.Empty<string>(), variables = Array.Empty<string>() } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, finalize.StatusCode);
        Assert.Equal("CICD_ENTRY_INVALID", (await finalize.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        return 0;
    });

    // A CI/CD secret is sealed again exactly as it was (ADR 1084): its payload is not a password entry's, and its index stays.
    [Fact]
    public Task A_cicd_secret_is_resealed_unchanged_to_a_new_member() => WithEnrolmentAsync(async rig =>
    {
        var (client, store) = await CiCdStoreAsync(rig);
        var entry = await client.CreateAsync(store, "Signing", SigningEntry());

        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.Bob.Id}", new { canSee = true, canReadContent = true });
        await DeclareAudienceAsync(rig.Anna.Http, store.Id, (rig.Anna.Id, true), (rig.Bob.Id, false));
        var outcome = await client.Store.ResealAsync(await client.GetStoreAsync(store.Links.Href("self")!));
        Assert.Equal(["Signing"], outcome.Resealed);

        var bob = new CiCdStoreClient(ApiCore.Over(rig.Bob.Http), new KeyOpener(rig.Bob.Key));
        var opened = await bob.OpenAsync(await bob.GetEntryAsync(entry.Links.Href("self")!));
        Assert.Equal(P12, opened.Content.Secrets.Single(s => s.Name == "MACOS_CERT_P12_BASE64").Value);
        Assert.Equal(SigningEntry().Targets, opened.Entry.CiCd!.Targets);
        return 0;
    });
}
