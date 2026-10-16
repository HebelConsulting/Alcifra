using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Controllers;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Certificates;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Certificates;
using SimplArchive.Infrastructure.Certificates.Policy;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Certificates;

/// <summary>
/// Certificate enrolment, owned by the core (ADR 0936; moved from the Encryption Module): validate a certificate
/// against enrolment's own checks and the tenant's policy, then file it as a Certificate document in the tenant's
/// "Reader certificates" folder, where the registry projection (ADR 0935) picks it up in the same transaction.
/// </summary>
/// <remarks>
/// <b>Enrolment validates BEFORE it files.</b> A document filed first and validated later leaves the archive holding a
/// certificate a reader can see and which silently addresses nothing, so every refusal happens before anything is
/// written, and names what is wrong in terms the person who chose the file can act on.
/// </remarks>
public sealed class CertificateEnrolment(
    SimplArchiveDbContext dbContext,
    IObjectStorageClient storage,
    DocumentFinalizer finalizer,
    IAuditRecorder audit,
    DocumentAccessService access,
    ICurrentTenantAccessor tenant,
    IReadOnlyList<ModuleLoader.LoadedModule> modules,
    ILogger<CertificateEnrolment> logger)
{
    /// <summary>An enrolled certificate as the listing shows it.</summary>
    public sealed record Listed(
        Guid DocumentId, string Label, string Thumbprint, string Serial, DateTimeOffset NotAfter, DateTimeOffset? RevokedAt,
        bool Usable, string? FailsPolicy);

    private Guid TenantId => tenant.TenantId ?? throw new InvalidOperationException("Certificate enrolment needs a current tenant.");

    /// <summary>Refuses with 404 unless an active module licenses enrolment for this tenant.</summary>
    public async Task EnsureOfferedAsync(CancellationToken cancellationToken)
    {
        if (!await CertificateEnrolmentLicence.IsOfferedAsync(dbContext, modules, DateTimeOffset.UtcNow, cancellationToken))
        {
            throw new CertificateEnrolmentNotOfferedException();
        }
    }

    /// <summary>Whether the caller administers enrolment: enrols for others and sets the policy.</summary>
    public Task<bool> CallerAdministersAsync(CancellationToken cancellationToken) => access.CanAdministerModulesAsync(cancellationToken);

    /// <summary>The caller's certificates, or every one in the tenant for an administrator who asks for them all.</summary>
    public async Task<IReadOnlyList<Listed>> ListAsync(bool everyone, CancellationToken cancellationToken)
    {
        var userId = access.GetCallerIdentity().UserId;
        var query = dbContext.EnrolledCertificates.AsNoTracking();
        if (!everyone)
        {
            query = query.Where(c => c.UserId == userId);
        }

        var rows = await query.ToListAsync(cancellationToken);
        var policy = await CurrentPolicyAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return [.. rows
            .OrderByDescending(r => r.NotAfter)
            .ThenBy(r => r.Thumbprint, StringComparer.Ordinal)
            .Select(r => new Listed(
                r.DocumentId, r.Label, r.Thumbprint, r.Serial, r.NotAfter, r.RevokedAt,
                r.RevokedAt is null && r.NotBefore <= now && r.NotAfter > now,
                PolicyEnforcement.RefusalForEnrolled(policy, r.CertificatePem)))];
    }

    /// <summary>Validates and files one certificate; returns what the listing would show for it.</summary>
    public async Task<Listed> EnrolAsync(string certificatePem, string? label, string? holder, CancellationToken cancellationToken)
    {
        var admin = await CallerAdministersAsync(cancellationToken);
        var forSomebodyElse = holder is { Length: > 0 };
        if (forSomebodyElse && !admin)
        {
            throw new CertificateNotYoursToEnrolException();
        }

        if (!forSomebodyElse && !admin && !await dbContext.Tenants.Where(t => t.Id == TenantId).Select(t => t.CertificateSelfServiceEnabled).SingleAsync(cancellationToken))
        {
            throw new CertificateSelfServiceDisabledException();
        }

        var checkedPem = EnrolmentPolicy.Check(certificatePem);
        if (!checkedPem.Ok)
        {
            throw new CertificateRefusedException(checkedPem.Refusal!);
        }

        using var leaf = checkedPem.Leaf!;
        var userId = access.GetCallerIdentity().UserId;
        var holderAddress = forSomebodyElse
            ? holder!
            : await dbContext.Users.Where(u => u.Id == userId).Select(u => u.Email).SingleOrDefaultAsync(cancellationToken);
        if (holderAddress is not { Length: > 0 })
        {
            throw new CertificateHolderMissingException();
        }

        if (PolicyEnforcement.Refusal(await CurrentPolicyAsync(cancellationToken), leaf, holderAddress, certificatePem) is { } policyRefusal)
        {
            throw new CertificatePolicyRefusedException(policyRefusal);
        }

        var thumbprint = Convert.ToHexString(SHA256.HashData(leaf.RawData));
        if (await dbContext.EnrolledCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.Thumbprint == thumbprint, cancellationToken) is { } existing)
        {
            throw new CertificateAlreadyEnrolledException(existing.UserId == userId
                ? $"This certificate is already enrolled as '{existing.Label}'"
                    + $"{(existing.RevokedAt is { } when ? $", revoked on {when:yyyy-MM-dd}" : string.Empty)}"
                    + ". Enrolling it again would change nothing; revoke the existing entry first if it should no longer "
                    + "address documents."
                : "This certificate is already enrolled for somebody else in this tenant. One key cannot be held by two "
                    + "readers; if that is genuinely the intent, the existing enrolment has to be revoked first.");
        }

        var notBefore = new DateTimeOffset(leaf.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        var notAfter = new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Holder"] = holderAddress,
            ["Label"] = label is { Length: > 0 } ? label : leaf.GetNameInfo(X509NameType.SimpleName, false),
            ["Subject"] = leaf.Subject,
            ["Issuer"] = leaf.Issuer,
            ["Thumbprint"] = thumbprint,
            ["Serial"] = leaf.SerialNumber.ToUpperInvariant(),
            ["Valid from"] = Instant(notBefore),
            ["Valid until"] = Instant(notAfter),
        };

        Document document;
        var owned = dbContext.Database.CurrentTransaction is null ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        await using (owned)
        {
            var folder = await ReaderCertificateFolder.FindOrCreateAsync(dbContext, TenantId, access.GetCallerIdentity(), cancellationToken)
                ?? throw new CertificateFolderNotConfiguredException();
            document = await FileAsync(folder.Id, WellKnownMaskIds.Certificate, $"{fields["Label"]} ({thumbprint[..16]})",
                Encoding.UTF8.GetBytes(certificatePem), ".pem", "application/x-pem-file", cancellationToken);
            await SetFieldsAsync(document, fields, cancellationToken);
            if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken);
            }
        }

        // After the commit: an event announcing an enrolment that then rolled back would be a lie.
        await audit.RecordAsync(AuditActions.CertificateEnrolled, "Document", document.Id, document.Name,
            $"Holder {holderAddress}, thumbprint {thumbprint}", cancellationToken: cancellationToken);
        logger.LogInformation("Enrolled certificate {Thumbprint} as document {DocumentId}", thumbprint, document.Id);
        return new Listed(document.Id, fields["Label"], thumbprint, fields["Serial"], notAfter, null, true, null);
    }

    /// <summary>The tenant's certificate policy; the unset policy when none was saved.</summary>
    public async Task<CertificatePolicy> CurrentPolicyAsync(CancellationToken cancellationToken) =>
        (await CurrentPolicyWithTagAsync(cancellationToken)).Policy;

    /// <summary>
    /// The policy and its tag: the id of the policy document's current version, or <see cref="Guid.Empty"/> before one
    /// was saved. Each save is a new version, so the tag moves with every change, which is what <c>If-Match</c> compares.
    /// </summary>
    public async Task<(CertificatePolicy Policy, Guid Tag)> CurrentPolicyWithTagAsync(CancellationToken cancellationToken)
    {
        if (await PolicyDocumentAsync(cancellationToken) is not { } document
            || await CurrentVersion.ResolveAsync(dbContext.DocumentVersions, document.Id, document.CurrentVersionId, cancellationToken) is not { } version)
        {
            return (CertificatePolicy.Unset, Guid.Empty);
        }

        // Read here and parsed; the bytes never leave the process (PlaintextDoorRatchetTests' internal readers).
        await using var stream = await storage.GetObjectAsync(version.ObjectKey, cancellationToken);
        using var reader = new StreamReader(stream);
        return (CertificatePolicy.FromJson(await reader.ReadToEndAsync(cancellationToken)), version.Id);
    }

    /// <summary>
    /// Saves the policy as a new version of the tenant's policy document, creating it on first use, provided it is still
    /// the one the caller read (<paramref name="ifMatch"/>, the tag of <see cref="CurrentPolicyWithTagAsync"/>). Returns
    /// the new tag.
    /// </summary>
    public async Task<Guid> SavePolicyAsync(CertificatePolicy policy, Guid ifMatch, CancellationToken cancellationToken)
    {
        if (!await CallerAdministersAsync(cancellationToken))
        {
            throw new CertificatePolicyNotYoursToSetException();
        }

        if (policy.Problems() is { Count: > 0 } problems)
        {
            throw new CertificatePolicyRefusedException(
                $"This policy would not work as written, so it was not saved: {string.Join(" ", problems)}");
        }

        var bytes = Encoding.UTF8.GetBytes(policy.ToJson());
        Document document;
        var owned = dbContext.Database.CurrentTransaction is null ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        await using (owned)
        {
            // THE LOCK, before the comparison: a second save running beside this one waits here, then reads the tag this
            // one committed and is refused. TOKEN DELIBERATELY NOT MOVED: nothing changes, the statement exists only for
            // the row lock it takes (ManualFiler's idiom).
            await dbContext.Tenants
                .Where(t => t.Id == TenantId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.StandardRepositoryId, t => t.StandardRepositoryId), cancellationToken);
            if ((await CurrentPolicyWithTagAsync(cancellationToken)).Tag != ifMatch)
            {
                throw Errors.Exceptions.Concurrency.EtagMismatchException.ForCertificatePolicy();
            }

            if (await PolicyDocumentAsync(cancellationToken) is { } existing)
            {
                document = existing;
                await AddVersionAsync(document, bytes, ".json", "application/json", cancellationToken);
            }
            else
            {
                var folder = await ReaderCertificateFolder.FindOrCreateAsync(dbContext, TenantId, access.GetCallerIdentity(), cancellationToken)
                    ?? throw new CertificateFolderNotConfiguredException();
                document = await FileAsync(folder.Id, WellKnownMaskIds.CertificatePolicy, "Certificate policy", bytes, ".json", "application/json", cancellationToken);
            }

            if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken);
            }
        }

        await audit.RecordAsync(AuditActions.CertificatePolicyChanged, "Document", document.Id, document.Name, cancellationToken: cancellationToken);
        return (await CurrentPolicyWithTagAsync(cancellationToken)).Tag;
    }

    private Task<Document?> PolicyDocumentAsync(CancellationToken cancellationToken) =>
        dbContext.Documents
            .Where(d => dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.CertificatePolicy))
            .OrderBy(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<Document> FileAsync(
        Guid folderId, Guid maskId, string name, byte[] bytes, string extension, string contentType, CancellationToken cancellationToken)
    {
        var (userId, serviceAccountId) = access.GetCallerIdentity();
        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            ParentId = folderId,
            Name = name,
            MaskVersionId = await dbContext.MaskVersions.Where(v => v.MaskId == maskId && v.IsCurrent).Select(v => v.Id).SingleAsync(cancellationToken),
            CreatedByUserId = userId,
            CreatedByServiceAccountId = userId is null ? serviceAccountId : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.Documents.Add(document);
        await dbContext.SaveChangesAsync(cancellationToken);
        await AddVersionAsync(document, bytes, extension, contentType, cancellationToken);
        return document;
    }

    private async Task AddVersionAsync(Document document, byte[] bytes, string extension, string contentType, CancellationToken cancellationToken)
    {
        var (userId, serviceAccountId) = access.GetCallerIdentity();
        var versionId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(document.TenantId, document.CreatedAt, document.StorageFolderId, versionId, extension);

        // Through the seam, so at-rest encryption applies exactly as it does to an upload.
        using (var content = new MemoryStream(bytes, writable: false))
        {
            await storage.PutObjectAsync(objectKey, content, contentType, cancellationToken);
        }

        // PENDING and confirmed by the finalizer, never a hand-written Confirmed row: it hashes the stored bytes, numbers
        // the version and joins our transaction.
        await finalizer.FileAsync(new DocumentVersion
        {
            Id = versionId,
            TenantId = document.TenantId,
            DocumentId = document.Id,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = userId,
            CreatedByServiceAccountId = userId is null ? serviceAccountId : null,
            DocumentDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAt = DateTimeOffset.UtcNow,
        }, cancellationToken);

        if (document.CurrentVersionId is not null)
        {
            document.CurrentVersionId = null;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task SetFieldsAsync(Document document, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var definitions = await dbContext.FieldDefinitions
            .Where(f => f.MaskVersionId == document.MaskVersionId && values.Keys.Contains(f.Name))
            .Select(f => new { f.Id, f.Name })
            .ToListAsync(cancellationToken);
        foreach (var definition in definitions)
        {
            dbContext.FieldValues.Add(new FieldValue
            {
                Id = Guid.NewGuid(),
                TenantId = document.TenantId,
                DocumentId = document.Id,
                FieldDefinitionId = definition.Id,
                Value = values[definition.Name],
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // A certificate's validity is a pair of UTC instants, stored with their offset (the Valid from/until DateTime fields).
    private static string Instant(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
}
