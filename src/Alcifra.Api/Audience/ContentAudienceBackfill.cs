using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.PasswordStores;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Domain.Tenants;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Audience;

/// <summary>
/// Gives every existing shared store and Sealed document its first declared audience (ADR 0983), once, at start-up —
/// because neither source is SQL: a store's readers come from the rights calculator, a Sealed document's from the
/// recipients of its current envelope.
/// </summary>
/// <remarks>
/// <para>
/// <b>A shared store</b> gets today's readers (what its entries were sealed to until now), with its creator as manager
/// when the creator is one of them; otherwise no manager, until a tenant administrator appoints one.
/// <b>A Sealed document</b> gets the users its current envelope's recipients map to through the certificate registry,
/// with the author of its current version as manager. <b>Personal-space items</b> get nothing: the owner is computed.
/// </para>
/// <para>
/// Idempotent by its own selection: only an item WITHOUT rows is filled, and every item created since ADR 0983 has
/// one at birth. An item with nobody to add (a store with no reader) stays empty and is simply looked at again on the
/// next start. One item's failure (an envelope that cannot be read) is logged and leaves it for the next start.
/// </para>
/// </remarks>
public sealed class ContentAudienceBackfill(
    AlcifraDbContext dbContext,
    ContentAudience audience,
    PasswordStoreSealing sealing,
    ILogger<ContentAudienceBackfill> logger)
{
    /// <summary>Fills the ambient tenant's items; answers how many got rows.</summary>
    public async Task<int> FillTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var items = await dbContext.Documents.AsNoTracking()
            .Where(d => d.PersonalRootOwnerId == null
                && dbContext.MaskVersions.Any(v => v.Id == d.MaskVersionId
                    && (v.MaskId == WellKnownMaskIds.PasswordStore || v.MaskId == WellKnownMaskIds.SealedDocument))
                && !dbContext.ContentAudienceMembers.Any(m => m.DocumentId == d.Id))
            .Select(d => new
            {
                d.Id,
                d.CreatedByUserId,
                d.CurrentVersionId,
                MaskId = dbContext.MaskVersions.Where(v => v.Id == d.MaskVersionId).Select(v => v.MaskId).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var filled = 0;
        foreach (var item in items)
        {
            try
            {
                var rows = item.MaskId == WellKnownMaskIds.PasswordStore
                    ? await StoreRowsAsync(item.Id, item.CreatedByUserId, cancellationToken)
                    : await SealedDocumentRowsAsync(item.Id, item.CurrentVersionId, cancellationToken);
                if (rows.Count == 0)
                {
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                dbContext.ContentAudienceMembers.AddRange(rows.Select(r => new ContentAudienceMember
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    DocumentId = item.Id,
                    UserId = r.UserId,
                    IsManager = r.IsManager,
                    CreatedAt = now,
                }));
                await dbContext.SaveChangesAsync(cancellationToken);
                filled++;
                logger.LogInformation("Encrypted content {DocumentId} got its first audience: {Members} member(s), {Managers} manager(s)",
                    item.Id, rows.Count, rows.Count(r => r.IsManager));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                dbContext.ChangeTracker.Clear();
                logger.LogWarning(e, "Could not give encrypted content {DocumentId} its first audience; the next start tries again", item.Id);
            }
        }

        return filled;
    }

    private async Task<IReadOnlyList<(Guid UserId, bool IsManager)>> StoreRowsAsync(Guid storeId, Guid? creatorId, CancellationToken cancellationToken) =>
        [.. (await audience.ReadersOfAsync(storeId, cancellationToken)).Select(r => (r.Id, r.Id == creatorId))];

    private async Task<IReadOnlyList<(Guid UserId, bool IsManager)>> SealedDocumentRowsAsync(
        Guid documentId, Guid? currentVersionId, CancellationToken cancellationToken)
    {
        if (await CurrentVersion.ResolveAsync(dbContext.DocumentVersions.AsNoTracking(), documentId, currentVersionId, cancellationToken)
            is not { Status: DocumentVersionStatus.Confirmed } version)
        {
            return [];
        }

        var recipients = await sealing.RecipientsAsync(version, cancellationToken);
        var registry = await dbContext.EnrolledCertificates.AsNoTracking()
            .Select(c => new { c.UserId, c.CertificatePem })
            .ToListAsync(cancellationToken);
        var users = registry
            .Where(c => recipients.Any(r => r.Is(X509Certificate2.CreateFromPem(c.CertificatePem))))
            .Select(c => c.UserId)
            .ToHashSet();

        // The author of the current version is the first manager — and a manager is always a member.
        if (version.CreatedByUserId is { } author)
        {
            users.Add(author);
        }

        return [.. users.Select(u => (u, u == version.CreatedByUserId))];
    }

    /// <summary>Every active tenant, each in its own scope: run at start-up, after the masks are healed.</summary>
    public static async Task FillEverywhereAsync(IServiceProvider root, CancellationToken cancellationToken = default)
    {
        List<Guid> tenants;
        using (var scope = root.CreateScope())
        {
            tenants = await scope.ServiceProvider.GetRequiredService<AlcifraDbContext>().Tenants
                .Where(t => t.Status == TenantStatus.Active).Select(t => t.Id).ToListAsync(cancellationToken);
        }

        var logger = root.GetRequiredService<ILoggerFactory>().CreateLogger<ContentAudienceBackfill>();
        foreach (var tenantId in tenants)
        {
            try
            {
                using var scope = root.CreateScope();
                ((CurrentTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).TenantId = tenantId;
                await scope.ServiceProvider.GetRequiredService<ContentAudienceBackfill>().FillTenantAsync(tenantId, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Giving the encrypted content of tenant {TenantId} its first audience failed; the next start tries again", tenantId);
            }
        }
    }
}
