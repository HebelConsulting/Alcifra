using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Controllers;
using Alcifra.Api.Documents;
using Alcifra.Api.Errors.Exceptions.Certificates;
using Alcifra.Api.Principals;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Masks;
using Alcifra.Domain.Notifications;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Certificates;

/// <summary>
/// Revoking an enrolled key from the clients (#2278, ADR 1083): by its holder, or by an administrator who manages users.
/// One transition writes the Certificate document's Revoked on and Revocation reason in one commit; the registry
/// projection follows in that save, and what the key could read is marked "Exposed since" by the sweep that already
/// watches revocations (ADR 0964).
/// </summary>
/// <remarks>
/// The holder needs no rights on the Reader certificates folder, which is why this is its own transition and not the
/// generic index-data PUT the CLI used to write: a person who lost a key must be able to stop it at once.
/// </remarks>
public sealed class CertificateRevocation(
    AlcifraDbContext dbContext,
    IAuditRecorder audit,
    DocumentAccessService documents,
    UserAccessService users,
    ICurrentTenantAccessor tenant,
    ILogger<CertificateRevocation> logger)
{
    /// <summary>Whether the caller may revoke a key held by <paramref name="holderUserId"/>: its holder, or a user administrator.</summary>
    public async Task<bool> MayRevokeAsync(Guid holderUserId, CancellationToken cancellationToken) =>
        (await WhoseKeysMayBeRevokedAsync(cancellationToken))(holderUserId);

    /// <summary>The same question for a whole listing, asked of the caller once.</summary>
    public async Task<Func<Guid, bool>> WhoseKeysMayBeRevokedAsync(CancellationToken cancellationToken)
    {
        var caller = documents.GetCallerIdentity().UserId;
        var administrator = await users.CanManageUsersAsync(cancellationToken);
        return holder => administrator || (caller is { } self && self == holder);
    }

    /// <summary>
    /// Revokes the key filed as <paramref name="documentId"/>. <paramref name="reason"/> is one of <see cref="Reasons"/>,
    /// or <see cref="Other"/> with <paramref name="otherText"/>. Refuses, before writing anything, an unknown key, a caller
    /// who may not, a key revoked already, and a reason that is none of these.
    /// </summary>
    public async Task RevokeAsync(Guid documentId, string? reason, string? otherText, CancellationToken cancellationToken)
    {
        var stored = Alcifra.Presentation.CertificateRevocationReasons.Stored(reason, otherText) ?? throw new CertificateRevocationReasonInvalidException();
        var row = await dbContext.EnrolledCertificates.AsNoTracking().SingleOrDefaultAsync(c => c.DocumentId == documentId, cancellationToken)
            ?? throw new CertificateNotFoundException();
        if (!await MayRevokeAsync(row.UserId, cancellationToken))
        {
            throw new CertificateRevocationNotAllowedException();
        }

        if (row.RevokedAt is not null)
        {
            throw new CertificateAlreadyRevokedException();
        }

        var document = await dbContext.Documents.SingleAsync(d => d.Id == documentId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var owned = dbContext.Database.CurrentTransaction is null ? await dbContext.Database.BeginTransactionAsync(cancellationToken) : null;
        await using (owned)
        {
            await OntoCurrentMaskVersionAsync(document, cancellationToken);
            await SetAsync(document, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Revoked on"] = now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture),
                ["Revocation reason"] = stored,
            }, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (owned is not null)
            {
                await owned.CommitAsync(cancellationToken);
            }
        }

        // After the commit: an audit line or a notification about a revocation that rolled back would be a lie.
        var caller = documents.GetCallerIdentity();
        await audit.RecordAsync(AuditActions.CertificateRevoked, "Document", documentId, row.Label,
            $"Thumbprint {row.Thumbprint}, reason: {stored}", cancellationToken: cancellationToken);
        if (caller.UserId != row.UserId)
        {
            await NotifyHolderAsync(row, stored, caller, now, cancellationToken);
        }

        logger.LogInformation("Revoked enrolled certificate {Thumbprint} ({DocumentId}), reason {Reason}", row.Thumbprint, documentId, stored);
    }

    // The holder is told when somebody else revoked their key (owner, 2026-11-09): which key, by whom, why, and what now.
    private async Task NotifyHolderAsync(
        Domain.Certificates.EnrolledCertificate row, string reason, (Guid? UserId, Guid? ServiceAccountId) caller, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var by = caller.UserId is { } userId
            ? await dbContext.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleOrDefaultAsync(cancellationToken)
            : caller.ServiceAccountId is { } accountId
                ? await dbContext.ServiceAccounts.Where(s => s.Id == accountId).Select(s => s.Name).SingleOrDefaultAsync(cancellationToken)
                : null;
        dbContext.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            TenantId = row.TenantId,
            RecipientUserId = row.UserId,
            Type = NotificationType.CertificateRevoked,
            Title = $"Your key \"{row.Label}\" was revoked",
            Body = $"Revoked by {by ?? "an administrator"}. Reason: {Readable(reason)}. Enrol a new key to read sealed items again.",
            DocumentId = row.DocumentId,
            CreatedAt = now,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Notifications are written in English, as every server-written one is; a code reads as its English choice, a
    // person's own words as written.
    private static string Readable(string stored) =>
        Alcifra.Presentation.CertificateRevocationReasons.StoredKey(stored) is { } key
            ? Alcifra.Localization.Strings.Get(key, CultureInfo.InvariantCulture)
            : stored;

    // A certificate filed before its mask gained Revocation reason wears an older mask version without that field, and a
    // value written against a field the document's version lacks would be stored and never shown. So the document moves
    // to the mask's current version first, its values carried over by name: mask versions are immutable and the newer one
    // only adds a field, so every value finds its definition.
    private async Task OntoCurrentMaskVersionAsync(Domain.Documents.Document document, CancellationToken cancellationToken)
    {
        var maskId = await dbContext.MaskVersions.Where(v => v.Id == document.MaskVersionId).Select(v => v.MaskId).SingleAsync(cancellationToken);
        var (current, _) = await MaskAssignment.ResolveCurrentVersionAsync(dbContext, maskId, cancellationToken);
        if (current == document.MaskVersionId)
        {
            return;
        }

        var definitions = await dbContext.FieldDefinitions.Where(f => f.MaskVersionId == current)
            .ToDictionaryAsync(f => f.Name, f => f.Id, StringComparer.Ordinal, cancellationToken);
        var values = await dbContext.FieldValues.Where(v => v.DocumentId == document.Id)
            .Join(dbContext.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { Value = v, f.Name })
            .ToListAsync(cancellationToken);
        foreach (var value in values)
        {
            if (definitions.TryGetValue(value.Name, out var definition))
            {
                value.Value.FieldDefinitionId = definition;
            }
        }

        document.MaskVersionId = current;
    }

    // Sets each named field on the document's (now current) mask version, replacing a value it already has.
    private async Task SetAsync(Domain.Documents.Document document, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var definitions = await dbContext.FieldDefinitions
            .Where(f => f.MaskVersionId == document.MaskVersionId && values.Keys.Contains(f.Name))
            .Select(f => new { f.Id, f.Name })
            .ToListAsync(cancellationToken);
        foreach (var definition in definitions)
        {
            var ids = new[] { definition.Id };
            var existing = await dbContext.FieldValues.Where(v => v.DocumentId == document.Id && ids.Contains(v.FieldDefinitionId)).ToListAsync(cancellationToken);
            dbContext.FieldValues.RemoveRange(existing);
            dbContext.FieldValues.Add(new Domain.Masks.FieldValue
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId ?? document.TenantId,
                DocumentId = document.Id,
                FieldDefinitionId = definition.Id,
                Value = values[definition.Name],
            });
        }
    }
}
