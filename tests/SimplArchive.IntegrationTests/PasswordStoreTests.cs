using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Audience;
using SimplArchive.Api.Certificates;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Concurrency;
using SimplArchive.Api.Errors.Exceptions.PasswordStores;
using SimplArchive.Api.PasswordStores;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Certificates;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// The password store's rules at the one door every write uses, and its history and compromise sweep (ADR 0964):
// containment, one My Passwords per personal space under its fixed name, the sealed-shape check at filing, the 24-hour
// undo window, the claim two instances share, and the revoked-certificate workflow.
public sealed partial class PasswordStoreTests : IDisposable
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Filename=:memory:");
    private readonly InMemoryObjectStorage _storage = new();

    public void Dispose() => _connection.Dispose();

    private SimplArchiveDbContext Ctx() =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(_connection).Options, new CurrentTenantAccessor { TenantId = _tenantId });

    private async Task SeedAsync()
    {
        await _connection.OpenAsync();
        using (var setup = Ctx())
        {
            await setup.Database.EnsureCreatedAsync();
        }

        using var db = Ctx();
        db.Tenants.Add(new Tenant { Id = _tenantId, Name = $"T{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow });
        db.Users.Add(new User { Id = _userId, TenantId = _tenantId, Email = "anna@t.test", DisplayName = "Anna", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await new WellKnownMaskSeeder(db, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(_tenantId);
    }

    private async Task<Guid> VersionOfAsync(SimplArchiveDbContext db, Guid maskId) =>
        await db.MaskVersions.Where(v => v.MaskId == maskId && v.IsCurrent).Select(v => v.Id).SingleAsync();

    private async Task<Guid> AddAsync(string name, Guid? parentId, Guid maskId, Guid? personalOf = null)
    {
        using var db = Ctx();
        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            ParentId = parentId,
            Name = name,
            MaskVersionId = await VersionOfAsync(db, maskId),
            PersonalOfUserId = personalOf,
            CreatedByUserId = _userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        return document.Id;
    }

    private async Task<(Guid Repository, Guid Store)> StoreAsync()
    {
        await SeedAsync();
        var repository = await AddAsync($"Team {Guid.NewGuid():N}", null, WellKnownMaskIds.Repository);
        return (repository, await AddAsync("Team passwords", repository, WellKnownMaskIds.PasswordStore));
    }

    // A confirmed version holding the given bytes, filed at the given time.
    private async Task<Guid> VersionAsync(Guid documentId, byte[] content, DateTimeOffset createdAt, int number,
        VersionCommentKind kind = VersionCommentKind.UserComment, DocumentVersionStatus status = DocumentVersionStatus.Confirmed)
    {
        using var db = Ctx();
        var id = Guid.NewGuid();
        var key = $"tenants/{_tenantId}/passwords/{documentId}/{id}.p7m";
        _storage.Objects[key] = content;
        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = id,
            TenantId = _tenantId,
            DocumentId = documentId,
            ObjectKey = key,
            Status = status,
            VersionNumber = status == DocumentVersionStatus.Confirmed ? number : null,
            Sha256Hash = status == DocumentVersionStatus.Confirmed ? Convert.ToHexStringLower(SHA256.HashData(content)) : null,
            CreatedAt = createdAt,
            DocumentDate = DateOnly.FromDateTime(createdAt.UtcDateTime),
            CreatedByUserId = _userId,
            SizeBytes = content.Length,
            CommentKind = kind,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static X509Certificate2 Certificate(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var withKey = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadCertificate(withKey.RawData);
    }

    private static byte[] Sealed(params X509Certificate2[] to) => PasswordStoreClient.Seal(PasswordEntryContent.Of("pw", "anna"), to);

    private async Task RevokeAsync(X509Certificate2 certificate, DateTimeOffset at, Guid anchorParent)
    {
        var anchor = await AddAsync($"cert {Guid.NewGuid():N}", anchorParent, WellKnownMaskIds.Folder);
        using var db = Ctx();
        db.EnrolledCertificates.Add(new EnrolledCertificate
        {
            DocumentId = anchor,
            TenantId = _tenantId,
            UserId = _userId,
            CertificatePem = certificate.ExportCertificatePem(),
            Label = "Lost key",
            Thumbprint = certificate.GetCertHashString(HashAlgorithmName.SHA256),
            Serial = certificate.SerialNumber,
            NotBefore = certificate.NotBefore,
            NotAfter = certificate.NotAfter,
            RevokedAt = at,
        });
        await db.SaveChangesAsync();
    }

    // The audience a store's entries are sealed to (ADR 0983), as the API composes it, for the caller given.
    private static ContentAudience Audience(SimplArchiveDbContext db, Guid? caller = null) =>
        new(db, new EffectiveRightsCalculator(db),
            new ReaderCertificateSet(db, new ModuleReaderCertificates(db, new LicensingModules(), NullLogger<ModuleReaderCertificates>.Instance), NullLogger<ReaderCertificateSet>.Instance),
            new PasswordStoreTree(db), new CurrentUserAccessor { UserId = caller }, NullLogger<ContentAudience>.Instance);

    private static PasswordStoreReaders Readers(SimplArchiveDbContext db) => new(Audience(db));

    private PasswordEntryHistory History(SimplArchiveDbContext db)
    {
        var audience = Audience(db);
        var sealing = new PasswordStoreSealing(db, _storage, new PasswordStoreTree(db), new PasswordStoreReaders(audience), new SealedRecipientCache(), NullLogger<PasswordStoreSealing>.Instance);
        return new PasswordEntryHistory(db, _storage, new SimplArchive.Infrastructure.Storage.StorageQuotaService(db, NullLogger<SimplArchive.Infrastructure.Storage.StorageQuotaService>.Instance),
            sealing, audience, NoOpAuditRecorder.Instance, NoOpDocumentIndexQueue.Instance, NullLogger<PasswordEntryHistory>.Instance);
    }

    private async Task<string?> ExposedSinceAsync(Guid entryId)
    {
        using var db = Ctx();
        return (await PasswordEntryHistory.ExposedSinceAsync(db, entryId, CancellationToken.None))?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<List<Guid>> VersionIdsAsync(Guid entryId)
    {
        using var db = Ctx();
        return [.. (await db.DocumentVersions.Where(v => v.DocumentId == entryId).Select(v => new { v.Id, v.CreatedAt }).ToListAsync())
            .OrderBy(v => v.CreatedAt).Select(v => v.Id)];
    }

    // ---- Containment and My Passwords ----

    [Fact]
    public async Task A_store_holds_folders_and_entries_and_nothing_else_and_they_live_nowhere_else()
    {
        var (repository, store) = await StoreAsync();
        var folder = await AddAsync("Banking", store, WellKnownMaskIds.PasswordFolder);
        await AddAsync("Online banking", folder, WellKnownMaskIds.PasswordEntry);
        await AddAsync("Nested", folder, WellKnownMaskIds.PasswordFolder);

        await Assert.ThrowsAsync<TypedFolderContainmentException>(() => AddAsync("A plain folder", store, WellKnownMaskIds.Folder));
        await Assert.ThrowsAsync<TypedFolderContainmentException>(() => AddAsync("Loose entry", repository, WellKnownMaskIds.PasswordEntry));
        await Assert.ThrowsAsync<TypedFolderContainmentException>(() => AddAsync("Loose folder", repository, WellKnownMaskIds.PasswordFolder));
    }

    [Fact]
    public async Task My_Passwords_is_created_once_beside_My_Documents_and_keeps_its_name()
    {
        await SeedAsync();
        Guid storeId;
        using (var db = Ctx())
        {
            var provisioner = new MyPasswordsProvisioner(db, new PersonalRepositoryProvisioner(db, NoOpAuditRecorder.Instance), NoOpAuditRecorder.Instance,
                NullLogger<MyPasswordsProvisioner>.Instance);
            var (first, created) = await provisioner.EnsureAsync(_userId, _tenantId, CancellationToken.None);
            var (again, createdAgain) = await provisioner.EnsureAsync(_userId, _tenantId, CancellationToken.None);
            Assert.True(created);
            Assert.False(createdAgain);
            Assert.Equal(first, again);
            storeId = first;
        }

        using (var db = Ctx())
        {
            var store = await db.Documents.SingleAsync(d => d.Id == storeId);
            Assert.Equal(PersonalFolders.MyPasswords, store.Name);
            Assert.True(await db.Documents.AnyAsync(d => d.ParentId == store.ParentId && d.Name == PersonalFolders.MyDocuments));

            store.Name = "Renamed";
            await Assert.ThrowsAsync<PersonalSpaceStructureException>(() => db.SaveChangesAsync());
        }

        using (var db = Ctx())
        {
            var store = await db.Documents.SingleAsync(d => d.Id == storeId);
            store.DeletedAt = DateTimeOffset.UtcNow;
            await Assert.ThrowsAsync<PersonalSpaceStructureException>(() => db.SaveChangesAsync());
        }

        var personalRoot = await Ctx().Documents.Where(d => d.Id == storeId).Select(d => d.ParentId!.Value).SingleAsync();
        // A second store beside it: refused by the one-per-space cap before its name is even asked about.
        await Assert.ThrowsAsync<TypedFolderContainmentException>(() => AddAsync(PersonalFolders.MyPasswords + " 2", personalRoot, WellKnownMaskIds.PasswordStore));
    }

    // ---- The sealed shape, checked at filing ----

    private async Task<(Guid Entry, DocumentVersion Pending)> PendingAsync(byte[] content, bool withEarlierVersion)
    {
        var (_, store) = await StoreAsync();
        var entry = await AddAsync("Mail", store, WellKnownMaskIds.PasswordEntry);
        if (withEarlierVersion)
        {
            await VersionAsync(entry, content, DateTimeOffset.UtcNow.AddMinutes(-5), 1);
        }

        await VersionAsync(entry, content, DateTimeOffset.UtcNow, 2, status: DocumentVersionStatus.Pending);
        using var db = Ctx();
        return (entry, await db.DocumentVersions.AsNoTracking().SingleAsync(v => v.DocumentId == entry && v.Status == DocumentVersionStatus.Pending));
    }

    private PasswordEntryVersions Versions(SimplArchiveDbContext db) => new(db, _storage, new NoModuleScopeFactory(db), new SimplArchive.Infrastructure.Persistence.CurrentUserAccessor(), NullLogger<PasswordEntryVersions>.Instance);

    [Fact]
    public async Task Plaintext_filed_as_a_password_entry_is_refused_and_leaves_nothing_behind()
    {
        var (entry, pending) = await PendingAsync("my password is hunter2"u8.ToArray(), withEarlierVersion: false);
        using var db = Ctx();
        var version = await db.DocumentVersions.SingleAsync(v => v.Id == pending.Id);

        await Assert.ThrowsAsync<ContentNotSealedException>(() => Versions(db).GuardAsync(version, null, new DefaultHttpContext().Request, CancellationToken.None));

        Assert.False(_storage.Objects.ContainsKey(pending.ObjectKey));
        Assert.Empty(await VersionIdsAsync(entry));
    }

    [Fact]
    public async Task An_envelope_is_filed_and_a_change_of_an_entry_with_content_requires_what_its_author_saw()
    {
        using var reader = Certificate("reader");

        // Without the precondition: refused, and — like every other refusal of a sealed version — nothing is left
        // behind. The pending row and its object go, so a client that forgot If-Match cannot fill the document's
        // history or the bucket one attempt at a time.
        var (entry, ungated) = await PendingAsync(Sealed(reader), withEarlierVersion: true);
        using (var refused = Ctx())
        {
            var version = await refused.DocumentVersions.SingleAsync(v => v.Id == ungated.Id);
            await Assert.ThrowsAsync<IfMatchRequiredException>(
                () => Versions(refused).GuardAsync(version, null, new DefaultHttpContext().Request, CancellationToken.None));
            Assert.False(await refused.DocumentVersions.AnyAsync(v => v.Id == ungated.Id));
        }

        Assert.False(_storage.Objects.ContainsKey(ungated.ObjectKey));

        // With it, a re-seal is accepted — on a fresh pending version, because the refused one no longer exists.
        var pending = await VersionAsync(entry, Sealed(reader), DateTimeOffset.UtcNow, 2, status: DocumentVersionStatus.Pending);
        using var db = Ctx();
        var accepted = await db.DocumentVersions.SingleAsync(v => v.Id == pending);

        var request = new DefaultHttpContext().Request;
        request.Headers.IfMatch = $"\"{Guid.NewGuid()}\"";
        var guarded = await Versions(db).GuardAsync(accepted, (int)VersionCommentKind.ResealedAfterAccessChange, request, CancellationToken.None);
        Assert.NotNull(guarded);
        Assert.True(guarded.Gated);
        Assert.True(guarded.Reseal);
        Assert.Equal(VersionCommentKind.ResealedAfterAccessChange, accepted.CommentKind);
    }

    [Fact]
    public async Task A_first_version_cannot_claim_to_be_a_re_seal()
    {
        using var reader = Certificate("reader");
        var (_, pending) = await PendingAsync(Sealed(reader), withEarlierVersion: false);
        using var db = Ctx();
        var version = await db.DocumentVersions.SingleAsync(v => v.Id == pending.Id);

        await Assert.ThrowsAsync<InvalidVersionCommentKindException>(
            () => Versions(db).GuardAsync(version, (int)VersionCommentKind.ResealedAfterAccessChange, new DefaultHttpContext().Request, CancellationToken.None));
    }

    // ---- History: 24 hours, claimed ----

    [Fact]
    public async Task A_superseded_version_is_deleted_for_good_only_after_its_undo_window()
    {
        using var reader = Certificate("reader");
        var (_, store) = await StoreAsync();
        var entry = await AddAsync("Mail", store, WellKnownMaskIds.PasswordEntry);
        var now = DateTimeOffset.UtcNow;
        var old = await VersionAsync(entry, Sealed(reader), now.AddHours(-30), 1);
        var current = await VersionAsync(entry, Sealed(reader), now.AddHours(-23), 2);

        using (var db = Ctx())
        {
            Assert.Equal(0, (await History(db).SweepAsync(now, CancellationToken.None)).VersionsDeleted);
        }

        Assert.Equal([old, current], await VersionIdsAsync(entry));

        using (var db = Ctx())
        {
            Assert.Equal(1, (await History(db).SweepAsync(now.AddHours(2), CancellationToken.None)).VersionsDeleted);
        }

        Assert.Equal([current], await VersionIdsAsync(entry));
        Assert.Single(_storage.Objects.Keys, k => k.Contains($"/{entry}/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_instances_acting_on_one_read_delete_a_version_once()
    {
        using var reader = Certificate("reader");
        var (_, store) = await StoreAsync();
        var entry = await AddAsync("Mail", store, WellKnownMaskIds.PasswordEntry);
        var old = await VersionAsync(entry, Sealed(reader), DateTimeOffset.UtcNow.AddDays(-3), 1);
        await VersionAsync(entry, Sealed(reader), DateTimeOffset.UtcNow.AddDays(-2), 2);
        string key;
        using (var db = Ctx())
        {
            key = await db.DocumentVersions.Where(v => v.Id == old).Select(v => v.ObjectKey).SingleAsync();
        }

        // Both instances read the same candidate; the first claims and deletes it, the second's claim finds nothing.
        using var first = Ctx();
        using var second = Ctx();
        Assert.True(await History(first).DeleteVersionAsync(old, entry, key, 10, _tenantId, compromise: false, CancellationToken.None));
        _storage.Objects[key] = [1, 2, 3];   // were the second to act, this object would vanish
        Assert.False(await History(second).DeleteVersionAsync(old, entry, key, 10, _tenantId, compromise: false, CancellationToken.None));
        Assert.True(_storage.Objects.ContainsKey(key));

        using var third = Ctx();
        Assert.Equal(0, (await History(third).SweepAsync(DateTimeOffset.UtcNow, CancellationToken.None)).VersionsDeleted);
    }

    // ---- Compromise: a revoked certificate ----

    [Fact]
    public async Task A_revoked_key_marks_the_entry_exposed_and_a_re_seal_without_it_deletes_the_past_at_once_but_keeps_the_mark()
    {
        using var kept = Certificate("kept");
        using var lost = Certificate("lost");
        var (repository, store) = await StoreAsync();
        var entry = await AddAsync("Mail", store, WellKnownMaskIds.PasswordEntry);
        var now = DateTimeOffset.UtcNow;
        var first = await VersionAsync(entry, Sealed(kept, lost), now.AddHours(-2), 1);
        await RevokeAsync(lost, new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero), repository);
        DateTimeOffset storeStamp;
        using (var db = Ctx())
        {
            storeStamp = await db.Documents.Where(d => d.Id == store).Select(d => d.ChangedAt).SingleAsync();
        }

        using (var db = Ctx())
        {
            var outcome = await History(db).SweepAsync(now, CancellationToken.None);
            Assert.Equal(1, outcome.MarkedExposed);
        }

        Assert.Equal("2026-10-20", await ExposedSinceAsync(entry));
        using (var db = Ctx())
        {
            Assert.True(await db.Documents.Where(d => d.Id == store).Select(d => d.ChangedAt).SingleAsync() > storeStamp);   // phones learn of it
            Assert.Equal(0, (await History(db).SweepAsync(now, CancellationToken.None)).MarkedExposed);                        // marked once
        }

        // A member re-seals without the revoked key: the version sealed to it goes at once, not after 24 hours.
        var resealed = await VersionAsync(entry, Sealed(kept), now.AddMinutes(-1), 2, VersionCommentKind.ResealedAfterAccessChange);
        using (var db = Ctx())
        {
            Assert.Equal(1, (await History(db).ReconcileAsync(entry, now, null, CancellationToken.None)).VersionsDeleted);
        }

        Assert.Equal([resealed], await VersionIdsAsync(entry));
        Assert.DoesNotContain(first, await VersionIdsAsync(entry));
        Assert.Equal("2026-10-20", await ExposedSinceAsync(entry));   // a re-seal keeps the old secret: still exposed

        // Only a new password clears it, and the next sweep does not mark it again.
        using (var db = Ctx())
        {
            await Versions(db).ClearExposureAsync(entry, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        Assert.Null(await ExposedSinceAsync(entry));
        using (var db = Ctx())
        {
            Assert.Equal(0, (await History(db).SweepAsync(now, CancellationToken.None)).MarkedExposed);
        }
    }

    [Fact]
    public async Task Password_store_masks_are_kept_out_of_retention_and_legal_hold()
    {
        Assert.All(WellKnownMaskIds.PasswordStoreMasks, mask =>
        {
            Assert.Contains(mask, ModuleMaskDeclarations.None.NotRetained);
            Assert.Contains(mask, ModuleMaskDeclarations.None.NotHeld);
        });
        Assert.Contains(WellKnownMaskIds.PasswordEntry, ModuleMaskDeclarations.None.SealedContent);
        Assert.True(ModuleMaskDeclarations.None.IsSealedContent($"tenants/{Guid.NewGuid()}/passwords/a/b.p7m"));
        Assert.Equal("passwords", ModuleMaskDeclarations.None.ContentSegmentFor(WellKnownMaskIds.PasswordEntry));
        await Task.CompletedTask;
    }

    // ---- Abandoned creates ----

    [Fact]
    public async Task An_entry_that_never_received_content_is_removed_after_a_day_once_by_one_of_two_instances()
    {
        using var reader = Certificate("reader");
        var (_, store) = await StoreAsync();
        var abandoned = await AddAsync("Never uploaded", store, WellKnownMaskIds.PasswordEntry);
        await VersionAsync(abandoned, "half an upload"u8.ToArray(), DateTimeOffset.UtcNow, 1, status: DocumentVersionStatus.Pending);
        var filed = await AddAsync("Filed", store, WellKnownMaskIds.PasswordEntry);
        await VersionAsync(filed, Sealed(reader), DateTimeOffset.UtcNow, 1);

        using (var db = Ctx())
        {
            Assert.Equal(0, (await History(db).SweepAsync(DateTimeOffset.UtcNow.AddHours(23), CancellationToken.None)).AbandonedRemoved);
        }

        // Both instances decided from the same read that it is due; the one statement that removes it is the claim.
        using var first = Ctx();
        using var second = Ctx();
        Assert.True(await History(first).RemoveAbandonedAsync(abandoned, "Never uploaded", _tenantId, CancellationToken.None));
        Assert.False(await History(second).RemoveAbandonedAsync(abandoned, "Never uploaded", _tenantId, CancellationToken.None));

        using var check = Ctx();
        Assert.False(await check.Documents.IgnoreQueryFilters().AnyAsync(d => d.Id == abandoned));
        Assert.DoesNotContain(_storage.Objects.Keys, k => k.Contains($"/{abandoned}/", StringComparison.Ordinal));
        Assert.True(await check.Documents.AnyAsync(d => d.Id == filed));   // an entry with content is never touched
        Assert.False(await History(check).RemoveAbandonedAsync(filed, "Filed", _tenantId, CancellationToken.None));
    }

    [Fact]
    public async Task The_sweep_removes_an_entry_still_without_content_after_a_day()
    {
        var (_, store) = await StoreAsync();
        var abandoned = await AddAsync("Never uploaded", store, WellKnownMaskIds.PasswordEntry);
        using var db = Ctx();
        Assert.Equal(1, (await History(db).SweepAsync(DateTimeOffset.UtcNow.AddHours(25), CancellationToken.None)).AbandonedRemoved);
        Assert.False(await db.Documents.AnyAsync(d => d.Id == abandoned));
    }

}
