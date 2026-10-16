using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Certificates;

/// <summary>
/// The tenant's one "Reader certificates" folder (ADR 0936): where enrolment files certificates and the policy, and the
/// discriminator the registry keys on.
/// </summary>
/// <remarks>
/// Found by its mask, never by name, and created only under the parent the tenant chose
/// (<c>Tenant.CertificateParentFolderId</c>): its rights decide who sees whose certificate, so the core does not invent
/// a location nobody chose. Folders the Encryption Module created before the core owned enrolment wear the same mask
/// and are adopted as they are.
/// </remarks>
public static class ReaderCertificateFolder
{
    public const string Name = "Reader certificates";

    /// <summary>The tenant's folder, the oldest if a second ever exists, so the answer does not drift between calls.</summary>
    public static Task<Document?> FindAsync(SimplArchiveDbContext dbContext, CancellationToken cancellationToken) =>
        dbContext.Documents
            .Where(d => dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == WellKnownMaskIds.ReaderCertificates))
            .OrderBy(d => d.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The folder, created under the configured parent when it does not exist yet; null when no parent is set.</summary>
    public static async Task<Document?> FindOrCreateAsync(
        SimplArchiveDbContext dbContext, Guid tenantId, (Guid? UserId, Guid? ServiceAccountId) creator, CancellationToken cancellationToken)
    {
        if (await FindAsync(dbContext, cancellationToken) is { } existing)
        {
            return existing;
        }

        var parentId = await dbContext.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.CertificateParentFolderId)
            .SingleOrDefaultAsync(cancellationToken);
        if (parentId is not { } id || !await dbContext.Documents.AnyAsync(d => d.Id == id, cancellationToken))
        {
            return null;
        }

        var folder = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = id,
            Name = Name,
            MaskVersionId = await dbContext.MaskVersions
                .Where(v => v.MaskId == WellKnownMaskIds.ReaderCertificates && v.IsCurrent)
                .Select(v => v.Id)
                .SingleAsync(cancellationToken),
            // Exactly one creator (CK_Documents_ExactlyOneCreator): the user, else the service account — an unattended
            // setup account enrols too, and the first enrolment creates this folder.
            CreatedByUserId = creator.UserId,
            CreatedByServiceAccountId = creator.UserId is null ? creator.ServiceAccountId : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.Documents.Add(folder);
        await dbContext.SaveChangesAsync(cancellationToken);
        return folder;
    }
}
