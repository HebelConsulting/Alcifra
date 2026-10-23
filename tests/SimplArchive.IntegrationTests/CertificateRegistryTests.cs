using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Booking;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Certificates;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.IntegrationTests;

// The core's certificate registry (#1790, ADR 0935): a projection of Certificate documents, and the answer a module
// that licenses enrolment gets in place of its own. The cases are weighted towards the direction that must never be
// missed — a certificate that STOPS being addressable (revoked, deleted, re-masked) — because a stale row there is a
// document sealed to a key its holder has given up.
//
// These cases are about the projection, so their tenant admits hardware without attestation (ADR 0963): the attestation
// rules are CertificateRegistryTests.Attestation.cs's subject, against a tenant that does not.
public partial class CertificateRegistryTests
{
    private sealed record Seed(SqliteConnection Connection, Guid TenantId, Guid UserId, InMemoryObjectStorage Storage);

    private static SimplArchiveDbContext Context(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private static async Task<Seed> SeedAsync(bool unattestedAllowed = true)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        using (var setup = Context(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using (var seed = Context(connection))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow, CertificateUnattestedHardwareAllowed = unattestedAllowed });
            seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "alice@example.com", DisplayName = "Alice", CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        using var masks = Context(connection, tenantId);
        await new WellKnownMaskSeeder(masks, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);
        return new Seed(connection, tenantId, userId, new InMemoryObjectStorage());
    }

    private static string SelfSigned(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(notBefore, notAfter);
        return certificate.ExportCertificatePem();
    }

    private static string Valid(string subject) =>
        SelfSigned(subject, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

    // The tenant's "Reader certificates" folder (ADR 0936), created on first use under a plain root.
    private static async Task<Guid> ReaderFolderAsync(Seed seed, SimplArchiveDbContext db)
    {
        var existing = await db.Documents
            .Where(d => db.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.ReaderCertificates))
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync();
        if (existing is { } id)
        {
            return id;
        }

        var root = new Document { Id = Guid.NewGuid(), TenantId = seed.TenantId, Name = "Root", CreatedByUserId = seed.UserId, CreatedAt = DateTimeOffset.UtcNow };
        var folder = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = seed.TenantId,
            ParentId = root.Id,
            Name = "Reader certificates",
            MaskVersionId = await MaskVersionAsync(db, WellKnownMaskIds.ReaderCertificates),
            CreatedByUserId = seed.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Documents.AddRange(root, folder);
        await db.SaveChangesAsync();
        return folder.Id;
    }

    // A live Certificate document held by the seeded user, its content the given PEM, in the reader folder by default.
    private static async Task<Guid> FileAsync(Seed seed, SimplArchiveDbContext db, string pem, bool withHolder = true, bool inReaderFolder = true)
    {
        var parentId = inReaderFolder ? await ReaderFolderAsync(seed, db) : (Guid?)null;
        var documentId = Guid.NewGuid();
        var objectKey = $"k/{documentId}.pem";
        seed.Storage.Objects[objectKey] = Encoding.UTF8.GetBytes(pem);
        db.Documents.Add(new Document
        {
            Id = documentId,
            TenantId = seed.TenantId,
            ParentId = parentId,
            Name = $"{documentId}.pem",
            MaskVersionId = await MaskVersionAsync(db, WellKnownMaskIds.Certificate),
            CreatedByUserId = seed.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = seed.TenantId,
            DocumentId = documentId,
            Status = DocumentVersionStatus.Confirmed,
            VersionNumber = 1,
            Sha256Hash = new string('a', 64),
            ObjectKey = objectKey,
            CreatedByUserId = seed.UserId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        if (withHolder)
        {
            db.ResourcePrincipals.Add(new ResourcePrincipal
            {
                Id = Guid.NewGuid(),
                TenantId = seed.TenantId,
                ResourceDocumentId = documentId,
                UserId = seed.UserId,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return documentId;
    }

    private static async Task<Guid> MaskVersionAsync(SimplArchiveDbContext db, Guid maskId) =>
        await db.MaskVersions.Where(v => v.MaskId == maskId && v.IsCurrent).Select(v => v.Id).FirstAsync();

    private static async Task SetFieldAsync(SimplArchiveDbContext db, Seed seed, Guid documentId, string name, string value)
    {
        var maskVersionId = await MaskVersionAsync(db, WellKnownMaskIds.Certificate);
        var fieldId = await db.FieldDefinitions
            .Where(f => f.MaskVersionId == maskVersionId && f.Name == name)
            .Select(f => f.Id)
            .FirstAsync();
        db.FieldValues.Add(new FieldValue
        {
            Id = Guid.NewGuid(),
            TenantId = seed.TenantId,
            DocumentId = documentId,
            FieldDefinitionId = fieldId,
            Value = value,
        });
        await db.SaveChangesAsync();
    }

    private static Task ProjectAsync(SimplArchiveDbContext db, Seed seed, Guid documentId) =>
        CertificateRegistryProjection.ProjectAsync(db, seed.Storage, documentId, seed.TenantId, NullLogger.Instance);

    [Fact]
    public async Task A_filed_certificate_is_registered_and_answered_for_its_holder()
    {
        var seed = await SeedAsync();
        await using var db = Context(seed.Connection, seed.TenantId);
        var pem = Valid("alice");
        var documentId = await FileAsync(seed, db, pem);
        await SetFieldAsync(db, seed, documentId, "Label", "Card");

        await ProjectAsync(db, seed, documentId);

        var row = await db.EnrolledCertificates.SingleAsync();
        Assert.Equal((documentId, seed.UserId, "Card"), (row.DocumentId, row.UserId, row.Label));
        var answered = Assert.Single(await CertificateRegistry.ForReaderAsync(db, seed.UserId, DateTimeOffset.UtcNow));
        Assert.Equal(pem.Trim(), answered.CertificatePem.Trim());
    }

    [Fact]
    public async Task A_certificate_OUTSIDE_the_reader_folder_addresses_nobody()
    {
        // ADR 0936: the Certificate mask is shared. A signing module's certificate, or a CA's kept for reference, has
        // a holder too, and registering it would wrap content keys to a key nobody decrypts with, silently.
        var seed = await SeedAsync();
        await using var db = Context(seed.Connection, seed.TenantId);
        var documentId = await FileAsync(seed, db, Valid("signing"), inReaderFolder: false);

        await ProjectAsync(db, seed, documentId);

        Assert.Empty(await db.EnrolledCertificates.ToListAsync());
    }

    [Fact]
    public async Task Moving_a_certificate_out_of_the_reader_folder_removes_its_row()
    {
        var seed = await SeedAsync();
        await using var db = Context(seed.Connection, seed.TenantId);
        var documentId = await FileAsync(seed, db, Valid("alice"));
        await ProjectAsync(db, seed, documentId);
        Assert.Single(await db.EnrolledCertificates.ToListAsync());

        var document = await db.Documents.SingleAsync(d => d.Id == documentId);
        document.ParentId = null;
        await db.SaveChangesAsync();
        await ProjectAsync(db, seed, documentId);

        Assert.Empty(await db.EnrolledCertificates.ToListAsync());
    }

    [Fact]
    public async Task A_certificate_with_no_holder_is_addressed_to_nobody()
    {
        var seed = await SeedAsync();
        await using var db = Context(seed.Connection, seed.TenantId);
        var documentId = await FileAsync(seed, db, Valid("nobody"), withHolder: false);

        await ProjectAsync(db, seed, documentId);

        Assert.Empty(await db.EnrolledCertificates.ToListAsync());
    }

    [Fact]
    public async Task A_REVOCATION_on_the_document_takes_the_certificate_out_of_the_answer()
    {
        // The case the projection exists for: revocation is an index field, and a revoked key that is still
        // answered keeps receiving documents sealed to it.
        var seed = await SeedAsync();
        await using var db = Context(seed.Connection, seed.TenantId);
        var documentId = await FileAsync(seed, db, Valid("alice"));
        await ProjectAsync(db, seed, documentId);

        await SetFieldAsync(db, seed, documentId, "Revoked on", "2026-09-29T10:00:00Z");
        await ProjectAsync(db, seed, documentId);

        Assert.NotNull((await db.EnrolledCertificates.SingleAsync()).RevokedAt);
        Assert.Empty(await CertificateRegistry.ForReaderAsync(db, seed.UserId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Deleting_the_document_removes_its_row()
    {
        var seed = await SeedAsync();
        await using var db = Context(seed.Connection, seed.TenantId);
        var documentId = await FileAsync(seed, db, Valid("alice"));
        await ProjectAsync(db, seed, documentId);

        var document = await db.Documents.SingleAsync(d => d.Id == documentId);
        document.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await ProjectAsync(db, seed, documentId);

        Assert.Empty(await db.EnrolledCertificates.ToListAsync());
    }

    [Fact]
    public async Task The_same_key_filed_twice_is_registered_once_and_the_second_save_does_not_fail()
    {
        var seed = await SeedAsync();
        await using var db = Context(seed.Connection, seed.TenantId);
        var pem = Valid("alice");
        var first = await FileAsync(seed, db, pem);
        var second = await FileAsync(seed, db, pem);

        await ProjectAsync(db, seed, first);
        await ProjectAsync(db, seed, second);

        Assert.Equal(first, (await db.EnrolledCertificates.SingleAsync()).DocumentId);
    }

    [Fact]
    public async Task Content_that_is_not_a_certificate_leaves_no_row_and_does_not_throw()
    {
        var seed = await SeedAsync();
        await using var db = Context(seed.Connection, seed.TenantId);
        var documentId = await FileAsync(seed, db, "not a certificate");

        await ProjectAsync(db, seed, documentId);

        Assert.Empty(await db.EnrolledCertificates.ToListAsync());
    }

    [Fact]
    public async Task Only_certificates_inside_their_validity_are_answered_longest_lived_first()
    {
        var seed = await SeedAsync();
        await using var db = Context(seed.Connection, seed.TenantId);
        var now = DateTimeOffset.UtcNow;
        var expired = await FileAsync(seed, db, SelfSigned("old", now.AddYears(-2), now.AddDays(-1)));
        var future = await FileAsync(seed, db, SelfSigned("next", now.AddDays(1), now.AddYears(3)));
        var shortLived = await FileAsync(seed, db, SelfSigned("short", now.AddDays(-1), now.AddMonths(1)));
        var longLived = await FileAsync(seed, db, SelfSigned("long", now.AddDays(-1), now.AddYears(2)));
        foreach (var id in new[] { expired, future, shortLived, longLived })
        {
            await ProjectAsync(db, seed, id);
        }

        var answered = await CertificateRegistry.ForReaderAsync(db, seed.UserId, now);

        Assert.Equal(["long", "short"], answered.Select(c => c.Label));
    }

    /// <summary>A 1.17 module that licenses enrolment and answers nothing itself.</summary>
    private sealed class LicensingModule : IIndustryModule
    {
        public bool LicensesCertificateEnrolment => true;
        public string ModuleId => "msg";
        public string DisplayName => "Messenger";
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public int AbiMinorVersion => ModuleAbiVersion.Minor;
        public string LicenseVerifyKeyPem => string.Empty;
        public IReadOnlyList<ModuleMaskSeed> Masks => [];
        public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services) { }
    }

    private sealed class Modules(IReadOnlyList<ModuleLoader.LoadedModule> modules) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IReadOnlyList<ModuleLoader.LoadedModule>) ? modules : null;
    }

    [Fact]
    public async Task An_active_module_that_LICENSES_enrolment_is_answered_from_the_core_registry()
    {
        // It declares no ReaderCertificates of its own, so a filter on that delegate alone would skip it and the
        // core would answer NoModule — and a strict tenant would seal to the reader's own column instead.
        var seed = await SeedAsync();
        await using var db = Context(seed.Connection, seed.TenantId);
        db.ModuleActivations.Add(new SimplArchive.Domain.Modules.ModuleActivation
        {
            Id = Guid.NewGuid(),
            TenantId = seed.TenantId,
            ModuleId = "msg",
            ActivatedAt = DateTimeOffset.UtcNow,
            SupportContractEndDate = DateTimeOffset.UtcNow.AddYears(1),
        });
        await db.SaveChangesAsync();
        var documentId = await FileAsync(seed, db, Valid("alice"));
        await ProjectAsync(db, seed, documentId);

        var answer = await new ModuleReaderCertificates(
                db, new Modules([new ModuleLoader.LoadedModule(new LicensingModule(), "test://msg")]),
                NullLogger<ModuleReaderCertificates>.Instance)
            .ForAsync(seed.UserId);

        Assert.Equal(ReaderCertificateOutcome.Answered, answer.Outcome);
        Assert.Single(answer.Certificates);
    }
}
