using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Alcifra.Domain.Certificates;
using Alcifra.Infrastructure.Certificates;
using Alcifra.Infrastructure.Certificates.Attestation;
using Alcifra.Infrastructure.Persistence;
using Alcifra.TestSupport;

namespace Alcifra.IntegrationTests;

// Attested enrolment in the registry (#1790, ADR 0963), on a tenant that requires attestation: the projection derives the
// root from the stored evidence, and the reader answer leaves out what is not attested to a root trusted here NOW, so
// switching a root or the tenant's setting takes effect without touching a certificate. Plus the schema's own rules.
public partial class CertificateRegistryTests
{
    private static async Task<TrustedAttestationRoot> TrustAsync(AlcifraDbContext db, TestAttestationHierarchy hierarchy, bool enabled = true)
    {
        var root = new TrustedAttestationRoot
        {
            Id = Guid.NewGuid(),
            Name = "Test root",
            CertificatePem = hierarchy.RootPem,
            Thumbprint = AttestationCatalogue.Thumbprint(hierarchy.Root),
            Enabled = enabled,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.TrustedAttestationRoots.Add(root);
        await db.SaveChangesAsync();
        return root;
    }

    private static async Task<Guid> FileAttestedAsync(Seed seed, AlcifraDbContext db, TestKey key, string? attestationPem = null)
    {
        var documentId = await FileAsync(seed, db, key.CertificatePem);
        db.CertificateAttestations.Add(new CertificateAttestation
        {
            DocumentId = documentId,
            TenantId = seed.TenantId,
            Format = "piv",
            AttestationCertificatePem = attestationPem ?? key.AttestationPem,
            IntermediateCertificatePem = key.IntermediatePem,
            Slot = "9d",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        await ProjectAsync(db, seed, documentId);
        return documentId;
    }

    [Fact]
    public async Task An_attested_certificate_records_its_root_and_addresses_its_holder_while_the_root_is_trusted()
    {
        var seed = await SeedAsync(unattestedAllowed: false);
        await using var db = Context(seed.Connection, seed.TenantId);
        using var hierarchy = TestAttestationHierarchy.WithIntermediates();
        var root = await TrustAsync(db, hierarchy);

        await FileAttestedAsync(seed, db, hierarchy.NewKey("alice"));

        Assert.Equal(root.Thumbprint, (await db.EnrolledCertificates.SingleAsync()).AttestationRootThumbprint);
        Assert.Single(await CertificateRegistry.ForReaderAsync(db, seed.UserId, DateTimeOffset.UtcNow));

        // Switched off for the installation: out at once, the certificate row untouched.
        root.Enabled = false;
        await db.SaveChangesAsync();
        Assert.Empty(await CertificateRegistry.ForReaderAsync(db, seed.UserId, DateTimeOffset.UtcNow));
        Assert.Equal(root.Thumbprint, (await db.EnrolledCertificates.AsNoTracking().SingleAsync()).AttestationRootThumbprint);
    }

    [Fact]
    public async Task A_root_the_TENANT_switched_off_leaves_the_certificate_out_for_that_tenant()
    {
        var seed = await SeedAsync(unattestedAllowed: false);
        await using var db = Context(seed.Connection, seed.TenantId);
        using var hierarchy = TestAttestationHierarchy.Direct();
        var root = await TrustAsync(db, hierarchy);
        await FileAttestedAsync(seed, db, hierarchy.NewKey("alice"));

        db.TenantDisabledAttestationRoots.Add(new TenantDisabledAttestationRoot { TenantId = seed.TenantId, RootId = root.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        Assert.Empty(await CertificateRegistry.ForReaderAsync(db, seed.UserId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task An_unattested_certificate_drops_out_unless_the_tenant_allows_hardware_without_attestation()
    {
        var seed = await SeedAsync(unattestedAllowed: false);
        await using var db = Context(seed.Connection, seed.TenantId);
        var documentId = await FileAsync(seed, db, Valid("alice"));
        await ProjectAsync(db, seed, documentId);

        Assert.Null((await db.EnrolledCertificates.SingleAsync()).AttestationRootThumbprint);
        Assert.Empty(await CertificateRegistry.ForReaderAsync(db, seed.UserId, DateTimeOffset.UtcNow));

        var tenant = await db.Tenants.SingleAsync();
        tenant.CertificateUnattestedHardwareAllowed = true;
        await db.SaveChangesAsync();
        Assert.Single(await CertificateRegistry.ForReaderAsync(db, seed.UserId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Evidence_that_does_not_verify_is_read_as_not_attested_and_the_save_does_not_fail()
    {
        var seed = await SeedAsync(unattestedAllowed: false);
        await using var db = Context(seed.Connection, seed.TenantId);
        using var hierarchy = TestAttestationHierarchy.Direct();
        await TrustAsync(db, hierarchy);
        var key = hierarchy.NewKey("alice");

        await FileAttestedAsync(seed, db, key, attestationPem: hierarchy.NewKey("other").AttestationPem);   // another key's

        Assert.Null((await db.EnrolledCertificates.SingleAsync()).AttestationRootThumbprint);
        Assert.Empty(await CertificateRegistry.ForReaderAsync(db, seed.UserId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task A_root_uploaded_LATER_makes_stored_evidence_attested_on_reverification()
    {
        var seed = await SeedAsync(unattestedAllowed: false);
        await using var db = Context(seed.Connection, seed.TenantId);
        using var hierarchy = TestAttestationHierarchy.Direct();
        await FileAttestedAsync(seed, db, hierarchy.NewKey("alice"));
        Assert.Null((await db.EnrolledCertificates.SingleAsync()).AttestationRootThumbprint);

        var root = await TrustAsync(db, hierarchy);
        Assert.Equal(1, await AttestationReverification.RunAsync(db, NullLogger.Instance));

        Assert.Equal(root.Thumbprint, (await db.EnrolledCertificates.AsNoTracking().SingleAsync()).AttestationRootThumbprint);
        Assert.Single(await CertificateRegistry.ForReaderAsync(db, seed.UserId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Schema_one_root_per_thumbprint_one_per_catalogue_key_and_any_number_uploaded()
    {
        var seed = await SeedAsync();
        await using var db = Context(seed.Connection, seed.TenantId);
        TrustedAttestationRoot Row(string thumbprint, string? key) => new()
        {
            Id = Guid.NewGuid(),
            Name = "R",
            CertificatePem = "pem",
            Thumbprint = thumbprint,
            CatalogueKey = key,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.TrustedAttestationRoots.AddRange(Row(new string('A', 64), null), Row(new string('B', 64), null), Row(new string('C', 64), "one"));
        await db.SaveChangesAsync();

        db.TrustedAttestationRoots.Add(Row(new string('A', 64), null));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.TrustedAttestationRoots.Add(Row(new string('D', 64), "one"));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Schema_evidence_goes_with_its_document_and_a_tenant_switch_goes_with_its_root()
    {
        var seed = await SeedAsync();
        await using var db = Context(seed.Connection, seed.TenantId);
        using var hierarchy = TestAttestationHierarchy.Direct();
        var root = await TrustAsync(db, hierarchy);
        var documentId = await FileAttestedAsync(seed, db, hierarchy.NewKey("alice"));
        db.TenantDisabledAttestationRoots.Add(new TenantDisabledAttestationRoot { TenantId = seed.TenantId, RootId = root.Id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        await db.EnrolledCertificates.Where(c => c.DocumentId == documentId).ExecuteDeleteAsync();
        await db.DocumentVersions.Where(v => v.DocumentId == documentId).ExecuteDeleteAsync();
        await db.FieldValues.Where(v => v.DocumentId == documentId).ExecuteDeleteAsync();
        await db.ResourcePrincipals.Where(p => p.ResourceDocumentId == documentId).ExecuteDeleteAsync();
        await db.Documents.IgnoreQueryFilters().Where(d => d.Id == documentId).ExecuteDeleteAsync();
        Assert.Empty(await db.CertificateAttestations.ToListAsync());

        await db.TrustedAttestationRoots.Where(r => r.Id == root.Id).ExecuteDeleteAsync();
        Assert.Empty(await db.TenantDisabledAttestationRoots.ToListAsync());
    }
}
