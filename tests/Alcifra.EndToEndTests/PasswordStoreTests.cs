using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.ApiClient;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;
using Alcifra.SealedArchive;

namespace Alcifra.EndToEndTests;

// The password store end to end (ADR 0964), driven through the SHARED client the phone uses: My Passwords on first use,
// folders and entries sealed on the device to the readers' enrolled certificates, opened with the reader's key, the
// re-seal after an access change with a 412 skipping an entry somebody changed meanwhile, plaintext refused at filing,
// the offline export carrying entries exactly as sealed, and a revoked key marking what it could read.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public sealed partial class PasswordStoreTests
{
    private const string LicensesVariable = "SIMPLARCHIVE_TESTMODULE_LICENSES_ENROLMENT";
    private const string VerifyKeyVariable = "SIMPLARCHIVE_TESTMODULE_VERIFY_KEY";

    private readonly E2EApiFactory _factory;

    public PasswordStoreTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Member(Guid Id, HttpClient Http, X509Certificate2 Key, PasswordStoreClient Client);

    private sealed record Rig(Guid TenantId, Guid RepoId, HttpClient Owner, HttpClient Admin, Member Anna, Member Bob)
    {
        public Guid AdminId { get; init; }

        public string AdminEmail { get; init; } = string.Empty;
    }

    // Opens an envelope the way a host's opener does: the S/MIME step, the reader's key, the shared payload step.
    private sealed class KeyOpener(X509Certificate2 key, Func<Task>? whileOpening = null) : IEnvelopeOpener
    {
        public Func<Task>? WhileOpening { get; set; } = whileOpening;

        public async Task<(byte[] Bytes, string ContentType)> OpenAsync(byte[] envelope, string contentType)
        {
            if (WhileOpening is { } during)
            {
                WhileOpening = null;
                await during();
            }

            var cms = new EnvelopedCms();
            cms.Decode(SmimeEnvelope.Cms(envelope));
            try
            {
                cms.Decrypt(new X509Certificate2Collection(key));
            }
            catch (CryptographicException e)
            {
                throw new EnvelopeNotOpenedException("it is not sealed to this key", e);
            }

            return SmimeEnvelope.Payload(cms.ContentInfo.Content);
        }
    }

    // The holder's address rides as the e-mail SAN: enrolment asks for it by default since ADR 0971.
    private static X509Certificate2 NewKey(string subject, string email)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddEmailAddress(email);
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    private static IReadOnlyDictionary<string, string> Links(JsonElement resource) =>
        resource.GetProperty("links").EnumerateArray()
            .GroupBy(l => l.GetProperty("rel").GetString()!)
            .ToDictionary(g => g.Key, g => g.First().GetProperty("href").GetString()!);

    /// <param name="tenantName">A tenant the configuration names (an encryption tier); null for a fresh one of its own.</param>
    private async Task<T> WithEnrolmentAsync<T>(Func<Rig, Task<T>> test, string? tenantName = null)
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            return await test(await RigAsync(vendorKey, tenantName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    private async Task<Rig> RigAsync(ECDsa vendorKey, string? tenantName)
    {
        var (clientId, secret, tenantId) = tenantName is null
            ? await _factory.SeedServiceAccountAsync(canManageRepositories: true)
            : await InNamedTenantAsync(tenantName);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Passwords {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();

        const string password = "pwstore-1234";
        var adminEmail = $"pw-admin-{Guid.NewGuid():N}@e2e.local";
        var adminId = await _factory.SeedUserAsync(tenantId, adminEmail, password, "Store Admin", isTenantAdmin: true, canManageServiceAccounts: true);
        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, password));

        // The licence that switches the core's certificate enrolment on (ADR 0936), as the enrolment tests file it.
        var license = new TenantLicense(["test-module"], tenantId, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), ModuleAbiVersion.Major, string.Empty).Sign(vendorKey);
        var licenseId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = $"License {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var version = await TestJson.Post(owner, $"/api/documents/{licenseId}/versions", new { fileExtension = ".json" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(license, new JsonSerializerOptions(JsonSerializerDefaults.Web))))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{licenseId}/versions/{version.GetProperty("id").GetGuid()}", new { });
        await TestJson.Put(admin, "/api/modules/test-module/license", new { licenseDocumentId = licenseId });

        // Self-service on, hardware without attestation allowed: the attestation rules are the enrolment tests' subject.
        var settingsHref = Links(await TestJson.Get(admin, "/api"))["certificateSettings"];
        using (var read = await admin.GetAsync(settingsHref))
        {
            read.EnsureSuccessStatusCode();
            using var write = new HttpRequestMessage(HttpMethod.Put, settingsHref)
            {
                Content = JsonContent.Create(new { selfServiceEnabled = true, parentFolderId = repoId, unattestedHardwareAllowed = true }),
            };
            write.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag!.Tag);
            (await admin.SendAsync(write)).EnsureSuccessStatusCode();
        }

        async Task<Member> MemberAsync(string name)
        {
            var email = $"pw-{name}-{Guid.NewGuid():N}@e2e.local";
            var id = await _factory.SeedUserAsync(tenantId, email, password, name);
            var http = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
            var key = NewKey(name, email);
            await TestJson.Post(http, "/api/certificates", new { certificatePem = key.ExportCertificatePem(), label = $"{name}'s key" });
            return new Member(id, http, key, new PasswordStoreClient(ApiCore.Over(http), new KeyOpener(key)));
        }

        var anna = await MemberAsync("Anna");
        var bob = await MemberAsync("Bob");
        await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{anna.Id}", new
        {
            canSee = true,
            canReadContent = true,
            canEditContent = true,
            canEditIndexData = true,
            canCreateSubItems = true,
            canDelete = true,
            canMove = true,
        });
        return new Rig(tenantId, repoId, owner, admin, anna, bob) { AdminId = adminId, AdminEmail = adminEmail };
    }

    private async Task<(string ClientId, string Secret, Guid TenantId)> InNamedTenantAsync(string tenantName)
    {
        var tenantId = await _factory.SeedTenantNamedAsync(tenantName);
        var (clientId, secret) = await _factory.SeedServiceAccountInTenantAsync(tenantId, canManageRepositories: true);
        return (clientId, secret, tenantId);
    }

    // Who a store is encrypted for (ADR 0983): its manager — Anna, who created it, or an administrator appointing one to
    // a store left without any — declares the whole audience in one PUT under the store's tag.
    private static async Task DeclareAudienceAsync(HttpClient manager, Guid storeId, params (Guid UserId, bool Manager)[] members)
    {
        using var read = await manager.GetAsync($"/api/documents/{storeId}/audience");
        Assert.True(read.IsSuccessStatusCode, $"audience read: {(int)read.StatusCode} {await read.Content.ReadAsStringAsync()}");
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{storeId}/audience")
        {
            Content = JsonContent.Create(new { members = members.Select(m => new { userId = m.UserId, isManager = m.Manager }) }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag!.Tag);
        using var answer = await manager.SendAsync(put);
        Assert.True(answer.IsSuccessStatusCode, $"audience PUT: {(int)answer.StatusCode} {await answer.Content.ReadAsStringAsync()}");
    }

    private static async Task<PasswordFolderInfo> SharedStoreAsync(Rig rig)
    {
        var created = await TestJson.Post(rig.Anna.Http, $"/api/documents/{rig.RepoId}/children", new { name = "Team", maskId = WellKnownMaskIds.PasswordStore });
        var (stores, _) = await rig.Anna.Client.ListStoresAsync();
        return await rig.Anna.Client.GetFolderAsync(stores.Single(s => s.Id == created.GetProperty("id").GetGuid()).Href);
    }

    // The flat listing (ADR 0207): one cursor-paginated stream of every folder and entry beneath a store, for the
    // clients that complete and search over the whole tree rather than browsing it (apass, 2026-10-25).
    [Fact]
    public Task The_flat_listing_returns_every_folder_and_entry_beneath_a_store_a_page_at_a_time() => WithEnrolmentAsync(async rig =>
    {
        var client = rig.Anna.Client;
        var store = await SharedStoreAsync(rig);

        var mail = await client.CreateFolderAsync(store, "Mail");
        var github = await client.CreateFolderAsync(store, "Github");
        await client.CreateFolderAsync(mail, "Banking");
        await client.CreateEntryAsync(github, "login", PasswordEntryContent.Of("pw", "anna"));

        var seen = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var url = store.Links.Href("descendants")
            ?? throw new InvalidOperationException("the store resource offers no 'descendants' rel");
        var pages = 0;
        while (url is not null)
        {
            var page = await TestJson.Get(rig.Anna.Http, $"{url}{(url.Contains('?', StringComparison.Ordinal) ? '&' : '?')}limit=2");
            pages++;
            foreach (var item in page.GetProperty("items").EnumerateArray())
            {
                seen[item.GetProperty("name").GetString()!] = item.GetProperty("parentId").GetGuid();
            }

            url = page.GetProperty("links").EnumerateArray()
                .Where(l => l.GetProperty("rel").GetString() == "next")
                .Select(l => l.GetProperty("href").GetString())
                .FirstOrDefault();
        }

        Assert.Equal(["Banking", "Github", "Mail", "login"], seen.Keys.Order(StringComparer.Ordinal));
        Assert.True(pages > 1, "four rows two at a time must page, or the cursor is not doing anything");
        Assert.Equal(mail.Id, seen["Banking"]);   // a nested folder names its real parent, which is how a client builds paths
        return 0;
    });

    [Fact]
    public Task My_Passwords_holds_folders_and_entries_sealed_on_the_device_and_opened_with_the_key() => WithEnrolmentAsync(async rig =>
    {
        var client = rig.Anna.Client;
        var (_, offered) = await client.ListStoresAsync();
        Assert.True(offered);

        var mine = await client.EnsureMyPasswordsAsync();
        Assert.Equal(mine.Id, (await client.EnsureMyPasswordsAsync()).Id);   // idempotent
        Assert.True(mine.IsStore && mine.IsPersonal);
        Assert.Equal("My Passwords", mine.Name);
        Assert.True(mine.RecommendsSpareKey);   // one key opens it: a spare is recommended

        var banking = await client.CreateFolderAsync(mine, "Banking");
        var content = new PasswordEntryContent("hunter2",
            [new(PasswordEntryFieldNames.UserName, "anna"), new(PasswordEntryFieldNames.Url, "https://bank.example"), new("PIN", "1234", IsSecret: true)]);
        var entry = await client.CreateEntryAsync(banking, "Online banking", content);
        Assert.False(entry.NeedsReseal);
        Assert.Null(entry.ExposedSince);

        var listed = await client.GetFolderAsync((await client.GetFolderAsync(mine.Links.Href("self")!)).Items.Single(i => i.IsFolder).Href);
        var row = Assert.Single(listed.Items);
        Assert.Equal("Online banking", row.Name);
        var opened = await client.OpenAsync(await client.GetEntryAsync(row.Href));
        Assert.Equal(content.Password, opened.Content.Password);
        Assert.Equal(content.Fields, opened.Content.Fields);

        var saved = await client.SaveEntryAsync(opened, opened.Content with { Password = "correct horse" });
        Assert.Equal(2, saved.VersionNumber);
        Assert.Equal("correct horse", (await client.OpenAsync(saved)).Content.Password);

        // A stale edit is refused, never a silent overwrite.
        await Assert.ThrowsAsync<PasswordEntryChangedException>(() => client.SaveEntryAsync(opened, opened.Content with { Password = "stale" }));

        // My Passwords keeps its name.
        using (var read = await rig.Anna.Http.GetAsync($"/api/documents/{mine.Id}"))
        using (var rename = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{mine.Id}") { Content = JsonContent.Create(new { name = "Renamed" }) })
        {
            read.EnsureSuccessStatusCode();
            rename.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag!.Tag);
            using var refused = await rig.Anna.Http.SendAsync(rename);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("PERSONAL_SPACE_STRUCTURE", await refused.Content.ReadAsStringAsync());
        }

        // Plaintext filed as an entry is refused at filing.
        var versionsHref = saved.Links.Href("versions")!;
        var pending = await TestJson.Post(rig.Anna.Http, versionsHref, new { fileExtension = ".p7m" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(pending.GetProperty("uploadUrl").GetString()!, new ByteArrayContent("plaintext password"u8.ToArray()))).EnsureSuccessStatusCode();
        }

        using var finalize = new HttpRequestMessage(HttpMethod.Put, Links(pending)["self"]) { Content = JsonContent.Create(new { }) };
        finalize.Headers.TryAddWithoutValidation("If-Match", saved.ETag);
        using var notSealed = await rig.Anna.Http.SendAsync(finalize);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, notSealed.StatusCode);
        Assert.Contains("CONTENT_NOT_SEALED", await notSealed.Content.ReadAsStringAsync());
        return 0;
    });

    [Fact]
    public Task A_new_reader_makes_the_store_need_a_re_seal_and_a_412_skips_an_entry_changed_meanwhile() => WithEnrolmentAsync(async rig =>
    {
        var anna = rig.Anna.Client;
        var store = await SharedStoreAsync(rig);
        Assert.Equal(1, store.ReaderCertificateCount);
        await anna.CreateEntryAsync(store, "Wiki", PasswordEntryContent.Of("w1", "team"));
        var changed = await anna.CreateEntryAsync(store, "Router", PasswordEntryContent.Of("r1", "admin"));
        Assert.False((await anna.GetFolderAsync(store.Links.Href("self")!)).NeedsReseal);

        // Bob may read, but the store is encrypted for its audience (ADR 0983): nothing changes until Anna adds him.
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.Bob.Id}", new { canSee = true, canReadContent = true });
        Assert.False((await anna.GetFolderAsync(store.Links.Href("self")!)).NeedsReseal);

        // Bob joins the audience: the entries are not sealed to him yet.
        await DeclareAudienceAsync(rig.Anna.Http, store.Id, (rig.Anna.Id, true), (rig.Bob.Id, false));
        var stale = await anna.GetFolderAsync(store.Links.Href("self")!);
        Assert.True(stale.NeedsReseal);
        Assert.True(stale.CanReseal);
        Assert.Equal(2, stale.ReaderCertificateCount);
        await Assert.ThrowsAsync<EnvelopeNotOpenedException>(async () => await rig.Bob.Client.OpenAsync(
            await rig.Bob.Client.GetEntryAsync((await rig.Bob.Client.GetFolderAsync(store.Links.Href("self")!)).Items.Single(i => i.Name == "Wiki").Href)));
        Assert.False((await rig.Bob.Client.GetFolderAsync(store.Links.Href("self")!)).CanReseal);   // read rights only: not his to re-seal

        // While Anna's client re-seals, a second session of hers changes the Router (the first entry it opens, by name): the
        // re-seal's own write of it then carries a stale tag, and its 412 skips that entry.
        var second = new PasswordStoreClient(ApiCore.Over(rig.Anna.Http), new KeyOpener(rig.Anna.Key));
        var routerOpened = await second.OpenAsync(await second.GetEntryAsync(changed.Links.Href("self")!));
        var resealing = new PasswordStoreClient(ApiCore.Over(rig.Anna.Http), new KeyOpener(rig.Anna.Key,
            whileOpening: () => second.SaveEntryAsync(routerOpened, routerOpened.Content with { Password = "r2" })));
        var outcome = await resealing.ResealAsync(stale);

        Assert.Equal(["Wiki"], outcome.Resealed);
        Assert.Equal(["Router"], outcome.SkippedChanged);
        Assert.Empty(outcome.Failed);

        // Bob opens both now: the Wiki re-sealed, the Router saved by the concurrent edit to the current readers.
        var bobStore = await rig.Bob.Client.GetFolderAsync(store.Links.Href("self")!);
        Assert.False(bobStore.NeedsReseal);
        foreach (var row in bobStore.Items)
        {
            await rig.Bob.Client.OpenAsync(await rig.Bob.Client.GetEntryAsync(row.Href));
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            var wiki = bobStore.Items.Single(i => i.Name == "Wiki").Id;
            var kinds = await db.DocumentVersions.IgnoreQueryFilters().Where(v => v.DocumentId == wiki && v.Status == DocumentVersionStatus.Confirmed)
                .Select(v => new { v.VersionNumber, v.CommentKind }).ToListAsync();
            Assert.Equal(VersionCommentKind.ResealedAfterAccessChange, kinds.OrderBy(k => k.VersionNumber).Last().CommentKind);
            Assert.True(await db.AuditEvents.IgnoreQueryFilters().AnyAsync(e => e.TenantId == rig.TenantId && e.Action == "PasswordEntry.Resealed" && e.TargetId == wiki));
        }

        // Bob leaves again: re-seal needed once more, and the server says so without a key.
        (await rig.Owner.DeleteAsync($"/api/documents/{rig.RepoId}/acl-entries/users/{rig.Bob.Id}")).EnsureSuccessStatusCode();
        Assert.True((await anna.GetFolderAsync(store.Links.Href("self")!)).NeedsReseal);
        return 0;
    });

    [Fact]
    public Task The_offline_export_carries_entries_exactly_as_sealed() => WithEnrolmentAsync(async rig =>
    {
        var client = rig.Anna.Client;
        var mine = await client.EnsureMyPasswordsAsync();
        var entry = await client.CreateEntryAsync(mine, "Mail", PasswordEntryContent.Of("m41l", "anna"));

        var document = await TestJson.Get(rig.Anna.Http, $"/api/documents/{mine.Id}");
        using var response = await rig.Anna.Http.GetAsync(Links(document)["offlineExport"]);
        response.EnsureSuccessStatusCode();
        using var archive = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);

        using var reader = new StreamReader(archive.GetEntry("tree/versions.jsonl")!.Open());
        var version = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement)
            .Single(v => v.GetProperty("documentId").GetGuid() == entry.Id);
        var blob = archive.Entries.Single(e => e.FullName == $"blobs/{version.GetProperty("sha256").GetString()}");
        using var bytes = new MemoryStream();
        await using (var stream = blob.Open())
        {
            await stream.CopyToAsync(bytes);
        }

        // Sealed as delivered (ADR 0931): the bytes the device sealed, addressed to Anna, and nothing readable beside them.
        var recipients = CmsRecipients.Of(bytes.ToArray());
        Assert.Contains(recipients, r => r.Is(rig.Anna.Key));
        Assert.DoesNotContain("m41l", Encoding.UTF8.GetString(bytes.ToArray()), StringComparison.Ordinal);
        return 0;
    });

    [Fact]
    public Task A_revoked_key_marks_what_it_could_read_and_a_re_seal_without_it_deletes_the_past_at_once() => WithEnrolmentAsync(async rig =>
    {
        var client = rig.Anna.Client;
        var store = await SharedStoreAsync(rig);
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.Bob.Id}", new { canSee = true, canReadContent = true, canEditContent = true });
        await DeclareAudienceAsync(rig.Anna.Http, store.Id, (rig.Anna.Id, true), (rig.Bob.Id, false));
        var entry = await client.CreateEntryAsync(store, "Vault", PasswordEntryContent.Of("v1", "ops"));

        // Anna's key is revoked in the registry (a state on its row, never a deletion), and the sweep runs.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            var row = await db.EnrolledCertificates.IgnoreQueryFilters().SingleAsync(c => c.UserId == rig.Anna.Id);
            row.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await _factory.Services.GetRequiredService<Alcifra.Api.PasswordStores.PasswordHistorySweepWorker>()
            .SweepAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        var marked = await client.GetEntryAsync(entry.Links.Href("self")!);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), marked.ExposedSince);
        var urgent = await client.GetFolderAsync(store.Links.Href("self")!);
        Assert.True(urgent.UrgentReseal);
        Assert.True(urgent.NeedsReseal);
        Assert.Equal(1, urgent.ReaderCertificateCount);   // Bob's; the revoked one addresses nobody now

        // Anna still holds the token the envelope was sealed to, and re-seals to the current readers: the version the
        // revoked key could read is deleted at once, and the entry stays marked until somebody files a new password.
        var outcome = await client.ResealAsync(urgent);
        Assert.Equal(["Vault"], outcome.Resealed);
        var after = await client.GetFolderAsync(store.Links.Href("self")!);
        Assert.False(after.UrgentReseal);
        Assert.False(after.NeedsReseal);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            Assert.Equal(1, await db.DocumentVersions.IgnoreQueryFilters().CountAsync(v => v.DocumentId == entry.Id));
        }

        var resealed = await rig.Bob.Client.OpenAsync(await rig.Bob.Client.GetEntryAsync(entry.Links.Href("self")!));
        Assert.Equal("v1", resealed.Content.Password);
        Assert.NotNull(resealed.Entry.ExposedSince);

        // Anna's revoked key no longer opens it. Bob edits: keeping the password keeps the mark; a new password clears it.
        await Assert.ThrowsAsync<EnvelopeNotOpenedException>(async () => await client.OpenAsync(await client.GetEntryAsync(entry.Links.Href("self")!)));
        var bob = rig.Bob.Client;
        var opened = await bob.OpenAsync(await bob.GetEntryAsync(entry.Links.Href("self")!));
        var edited = await bob.SaveEntryAsync(opened, opened.Content with { Fields = [.. opened.Content.Fields, new("Note", "rotated soon")] });
        Assert.NotNull(edited.ExposedSince);
        var rotated = await bob.SaveEntryAsync(await bob.OpenAsync(edited), opened.Content with { Password = "v2-new" });
        Assert.Null(rotated.ExposedSince);
        return 0;
    });

    [Fact]
    public Task A_tenant_administrator_is_sealed_to_only_once_granted_read_and_added_to_the_audience() => WithEnrolmentAsync(async rig =>
    {
        // The administrator enrols a key too, and reads the shared store through the bypass.
        var adminKey = NewKey("Admin", rig.AdminEmail);
        await TestJson.Post(rig.Admin, "/api/certificates", new { certificatePem = adminKey.ExportCertificatePem(), label = "Admin's key" });
        var adminClient = new PasswordStoreClient(ApiCore.Over(rig.Admin), new KeyOpener(adminKey));
        var store = await SharedStoreAsync(rig);
        var entry = await rig.Anna.Client.CreateEntryAsync(store, "Firewall", PasswordEntryContent.Of("fw1", "root"));

        // Not a recipient, so nothing needs re-sealing, and the key the administrator holds opens nothing.
        var recipients = await rig.Anna.Client.GetRecipientsAsync(store.Links);
        Assert.DoesNotContain(recipients.Certificates, c => c.UserId == rig.AdminId);
        Assert.DoesNotContain(recipients.ReadersWithoutCertificate, r => r.UserId == rig.AdminId);
        Assert.False((await rig.Anna.Client.GetFolderAsync(store.Links.Href("self")!)).NeedsReseal);
        // Outside the audience, the administrator is not even handed the envelope (ADR 0983).
        Assert.Null((await adminClient.GetEntryAsync(entry.Links.Href("self")!)).Links.Href("content"));

        // The administrator grants themselves read: audited, and a reader — but not yet somebody the store is sealed to.
        await TestJson.Put(rig.Admin, $"/api/documents/{rig.RepoId}/acl-entries/users/{rig.AdminId}", new { canSee = true, canReadContent = true });
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            Assert.True(await db.AuditEvents.IgnoreQueryFilters().AnyAsync(e => e.TenantId == rig.TenantId && e.Action == "Acl.Granted" && e.TargetId == rig.RepoId));
        }

        // Reading is not decrypting (ADR 0983): only once the store's manager adds them is the administrator sealed to.
        Assert.DoesNotContain((await rig.Anna.Client.GetRecipientsAsync(store.Links)).Certificates, c => c.UserId == rig.AdminId);
        await DeclareAudienceAsync(rig.Anna.Http, store.Id, (rig.Anna.Id, true), (rig.AdminId, false));
        Assert.Contains((await rig.Anna.Client.GetRecipientsAsync(store.Links)).Certificates, c => c.UserId == rig.AdminId);
        var stale = await rig.Anna.Client.GetFolderAsync(store.Links.Href("self")!);
        Assert.True(stale.NeedsReseal);
        Assert.Equal(["Firewall"], (await rig.Anna.Client.ResealAsync(stale)).Resealed);
        Assert.Equal("fw1", (await adminClient.OpenAsync(await adminClient.GetEntryAsync(entry.Links.Href("self")!))).Content.Password);
        return 0;
    });

    // alcifra's password commands (ADR 0966), as the kiosk's crypto seed runs them: a service account creates a shared
    // store, breaks its inheritance, grants a reader, and files an entry sealed on ITS machine; every step re-runs unchanged.
    [Fact]
    public Task Saconsole_seeds_a_shared_store_whose_entries_a_reader_opens_and_a_re_run_changes_nothing() => WithEnrolmentAsync(async rig =>
    {
        var api = new Alcifra.Cli.Infrastructure.AlcifraApi(rig.Owner);
        var name = $"Demo logins {Guid.NewGuid():N}";

        var (storeId, created) = await Alcifra.Cli.Commands.PasswordStoreSurface.FindOrCreateStoreAsync(api, rig.RepoId, name, CancellationToken.None);
        Assert.True(created);
        Assert.Equal((storeId, false), await Alcifra.Cli.Commands.PasswordStoreSurface.FindOrCreateStoreAsync(api, rig.RepoId, name, CancellationToken.None));

        // A child of that name that is not a store is refused, never taken for one.
        var plain = $"Plain {Guid.NewGuid():N}";
        await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children", new { name = plain });
        await Assert.ThrowsAsync<Alcifra.Cli.Infrastructure.CliException>(
            () => Alcifra.Cli.Commands.PasswordStoreSurface.FindOrCreateStoreAsync(api, rig.RepoId, plain, CancellationToken.None));

        // Its own grants from now on (Anna's write is copied down), then Bob is granted read on it.
        Assert.True(await Alcifra.Cli.Commands.AclBreakInheritanceCommand.BreakAsync(api, storeId, CancellationToken.None));
        Assert.False(await Alcifra.Cli.Commands.AclBreakInheritanceCommand.BreakAsync(api, storeId, CancellationToken.None));
        await TestJson.Put(rig.Owner, $"/api/documents/{storeId}/acl-entries/users/{rig.Bob.Id}", new { canSee = true, canReadContent = true });

        // A store a service account creates starts with the readers it inherited (Anna) and NO manager (ADR 0983): a
        // tenant administrator appoints one from the readers, and that manager adds Bob.
        await DeclareAudienceAsync(rig.Admin, storeId, (rig.Anna.Id, true));
        await DeclareAudienceAsync(rig.Anna.Http, storeId, (rig.Anna.Id, true), (rig.Bob.Id, false));

        // The service account seals the entry to the store's audience (Anna and Bob), not to itself: it holds no key.
        var seeder = new PasswordStoreClient(ApiCore.Over(rig.Owner));
        var content = PasswordEntryContent.Of("SimplDemo2026!", "admin@demo.example", "https://demo.example");
        var (entryId, outcome) = await Alcifra.Cli.Commands.PasswordStoreSurface.FindOrCreateEntryAsync(seeder, storeId, "Demo login: admin@", content, CancellationToken.None);
        Assert.Equal(Alcifra.Cli.Commands.PasswordStoreSurface.EntryOutcome.Created, outcome);
        var again = await Alcifra.Cli.Commands.PasswordStoreSurface.FindOrCreateEntryAsync(
            seeder, storeId, "Demo login: admin@", PasswordEntryContent.Of("different"), CancellationToken.None);
        Assert.Equal((entryId, Alcifra.Cli.Commands.PasswordStoreSurface.EntryOutcome.Unchanged), again);

        foreach (var reader in new[] { rig.Anna, rig.Bob })
        {
            var store = await Alcifra.Cli.Commands.PasswordStoreSurface.StoreAsync(reader.Client, storeId, CancellationToken.None);
            Assert.Equal(2, store.ReaderCertificateCount);
            Assert.False(store.NeedsReseal);
            var opened = await reader.Client.OpenAsync(await reader.Client.GetEntryAsync(Assert.Single(store.Items).Href));
            Assert.Equal(1, opened.Entry.VersionNumber);   // the re-run filed no version
            Assert.Equal("SimplDemo2026!", opened.Content.Password);
            Assert.Equal("admin@demo.example", opened.Content.UserName);
            Assert.Equal("https://demo.example", opened.Content.Url);
        }

        // Bob holds read only on the store: the copied-down write does not reach him, the explicit grant does.
        Assert.False((await Alcifra.Cli.Commands.PasswordStoreSurface.StoreAsync(rig.Bob.Client, storeId, CancellationToken.None)).CanCreateEntries);
        return 0;
    });

    // With no reader holding a certificate, nothing is created: the refusal names who lacks one (ADR 0966).
    [Fact]
    public Task Saconsole_refuses_an_entry_nobody_could_read_and_leaves_no_empty_entry() => WithEnrolmentAsync(async rig =>
    {
        var api = new Alcifra.Cli.Infrastructure.AlcifraApi(rig.Owner);
        var (storeId, _) = await Alcifra.Cli.Commands.PasswordStoreSurface.FindOrCreateStoreAsync(api, rig.RepoId, $"Nobody {Guid.NewGuid():N}", CancellationToken.None);
        await Alcifra.Cli.Commands.AclBreakInheritanceCommand.BreakAsync(api, storeId, CancellationToken.None);

        // Anna's copied-down grant is withdrawn, so the store has no reader with a key; Carol reads it without one.
        (await rig.Owner.DeleteAsync($"/api/documents/{storeId}/acl-entries/users/{rig.Anna.Id}")).EnsureSuccessStatusCode();

        var carolId = await _factory.SeedUserAsync(rig.TenantId, $"pw-carol-{Guid.NewGuid():N}@e2e.local", "pwstore-1234", "Carol");
        await TestJson.Put(rig.Owner, $"/api/documents/{storeId}/acl-entries/users/{carolId}", new { canSee = true, canReadContent = true });

        // No manager (a service account created it): the administrator appoints Carol, a reader, and so a member.
        await DeclareAudienceAsync(rig.Admin, storeId, (rig.Anna.Id, false), (carolId, true));

        var seeder = new PasswordStoreClient(ApiCore.Over(rig.Owner));
        var refusal = await Assert.ThrowsAsync<Alcifra.Cli.Infrastructure.CliException>(() => Alcifra.Cli.Commands.PasswordStoreSurface.FindOrCreateEntryAsync(
            seeder, storeId, "Unreadable", PasswordEntryContent.Of("x"), CancellationToken.None));
        Assert.Contains("Carol", refusal.Message);
        Assert.Empty((await Alcifra.Cli.Commands.PasswordStoreSurface.StoreAsync(seeder, storeId, CancellationToken.None)).Items);
        return 0;
    });

    // ADR 0973: the autofill index is derived by the client from the content it seals, rides with the version, comes
    // back on the entry AND its listing row, follows an edit (an emptied URL clears the site), and is written by any
    // later version for an entry that predates it.
    [Fact]
    public Task An_entry_carries_its_autofill_index_without_being_opened() => WithEnrolmentAsync(async rig =>
    {
        var client = rig.Anna.Client;
        var mine = await client.EnsureMyPasswordsAsync();
        var content = new PasswordEntryContent("hunter2",
        [
            new(PasswordEntryFieldNames.UserName, "anna@example.com"),
            new(PasswordEntryFieldNames.Url, "https://www.bank.example/login?next=/"),
            new(PasswordEntryFieldNames.App, "com.bank.app"),
        ]);

        var entry = await client.CreateEntryAsync(mine, "Online banking", content);
        Assert.Equal("bank.example", entry.Site);            // host only: no www, no scheme, no path
        Assert.Equal("anna@example.com", entry.UserName);
        Assert.Equal("com.bank.app", entry.App);

        // The LISTING carries it too: an autofill sheet is built from the folder, not one request per entry.
        var row = Assert.Single((await client.GetFolderAsync(mine.Links.Href("self")!)).Items);
        Assert.Equal(("bank.example", "anna@example.com", "com.bank.app"), (row.Site, row.UserName, row.App));

        // An edit follows: the user name travels with the version, and an emptied URL clears the site.
        var opened = await client.OpenAsync(entry);
        var edited = new PasswordEntryContent(content.Password,
            [new(PasswordEntryFieldNames.UserName, "anna2@example.com")]);
        var saved = await client.SaveEntryAsync(opened, edited);
        Assert.Equal("anna2@example.com", saved.UserName);
        Assert.Null(saved.Site);
        Assert.Null(saved.App);

        // An entry that predates the index has none; the next version it receives (a save, or a re-seal) writes it,
        // so old entries become matchable as their owner touches them — never by opening them behind their back.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            var legacy = await db.FieldValues.IgnoreQueryFilters(["TenantFilter"]).Where(v => v.DocumentId == saved.Id).ToListAsync();
            db.FieldValues.RemoveRange(legacy);
            await db.SaveChangesAsync();
        }

        Assert.Null((await client.GetEntryAsync(saved.Links.Href("self")!)).Site);
        var again = await client.OpenAsync(await client.GetEntryAsync(saved.Links.Href("self")!));
        var restored = new PasswordEntryContent("newer",
            [new(PasswordEntryFieldNames.UserName, "anna3@example.com"), new(PasswordEntryFieldNames.Url, "portal.example")]);
        var refiled = await client.SaveEntryAsync(again, restored);
        Assert.Equal("portal.example", refiled.Site);   // a scheme-less URL is read as https, host only
        Assert.Equal("anna3@example.com", refiled.UserName);
        return 0;
    });

    // ADR 1013: "Has code" says THAT an entry carries a verification code — derived by the client from the content it
    // seals, filed with the version, on the entry, its listing row and the flat descendants — and nothing more: the
    // stored value is a Boolean, never the setup. Removing the code clears it with the next version.
    [Fact]
    public Task An_entry_says_it_has_a_verification_code_without_being_opened() => WithEnrolmentAsync(async rig =>
    {
        const string Setup = "otpauth://totp/Bank:anna?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Bank";
        var client = rig.Anna.Client;
        var mine = await client.EnsureMyPasswordsAsync();
        var content = new PasswordEntryContent("hunter2",
        [
            new(PasswordEntryFieldNames.UserName, "anna"),
            new(PasswordEntryFieldNames.Url, "https://bank.example"),
            new(PasswordEntryFieldNames.VerificationCode, Setup, IsSecret: true),
        ]);

        var entry = await client.CreateEntryAsync(mine, "Bank with code", content);
        Assert.True(entry.HasCode);
        Assert.True(Assert.Single((await client.GetFolderAsync(mine.Links.Href("self")!)).Items, i => i.Id == entry.Id).HasCode);
        var (rows, _) = await client.DescendantsPageAsync(mine.Links.Href("descendants")!);
        Assert.True(Assert.Single(rows, r => r.Id == entry.Id).HasCode);

        // Met as a document in the Repositories tree, its row says so too: the web marks it, never opening it.
        var children = await TestJson.Get(rig.Anna.Http, $"/api/documents/{mine.Id}/children");
        var child = children.GetProperty("children").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == entry.Id);
        Assert.True(child.GetProperty("hasCode").GetBoolean());

        // Stored as a Boolean, and nothing of the secret reaches an index value.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            var values = await db.FieldValues.IgnoreQueryFilters().Where(v => v.DocumentId == entry.Id).Select(v => v.Value).ToListAsync();
            Assert.Contains("true", values);
            Assert.DoesNotContain(values, v => v.Contains("GEZDGNBV", StringComparison.Ordinal));
        }

        // The code removed: the next version clears the flag.
        var opened = await client.OpenAsync(entry);
        var saved = await client.SaveEntryAsync(opened, new PasswordEntryContent("hunter2", [new(PasswordEntryFieldNames.UserName, "anna")]));
        Assert.False(saved.HasCode);
        Assert.Equal("anna", saved.UserName);
        return 0;
    });
}
