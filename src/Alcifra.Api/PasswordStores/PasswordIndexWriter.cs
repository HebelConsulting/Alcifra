using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Errors.Exceptions.PasswordStores;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.PasswordStores;

/// <summary>
/// Writes the three autofill index values a password entry carries (ADR 0973), from the finalize body of the version
/// being filed.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only these three fields are touched</b>, by name and within the document's own mask version: a full index-data
/// replacement would erase the server's "Exposed since" (ADR 0964), which is exactly the field a client must never own.
/// </para>
/// <para>
/// The values come from the client, which derived them from the content it just sealed — the server never decrypts.
/// Only a Password Entry is served here; a version of any other document that sends an index is refused rather than
/// silently ignored, because the value would otherwise look stored and never be.
/// </para>
/// </remarks>
public static class PasswordIndexWriter
{
    /// <summary>Applies the values (an empty one clears that field), or does nothing when <paramref name="index"/> is null.</summary>
    public static async Task ApplyAsync(
        AlcifraDbContext dbContext,
        Guid documentId,
        PasswordIndex? index,
        CancellationToken cancellationToken)
    {
        if (index is null)
        {
            return;
        }

        var mask = await dbContext.Documents
            .Where(d => d.Id == documentId)
            .Join(dbContext.MaskVersions, d => d.MaskVersionId, v => (Guid?)v.Id, (_, v) => new { v.MaskId, MaskVersionId = v.Id })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotAPasswordStoreItemException();

        if (mask.MaskId != WellKnownMaskIds.PasswordEntry)
        {
            throw new NotAPasswordStoreItemException();
        }

        var names = new[]
        {
            WellKnownMaskIds.PasswordEntryFields.Site,
            WellKnownMaskIds.PasswordEntryFields.UserName,
            WellKnownMaskIds.PasswordEntryFields.App,
        };
        var definitions = await dbContext.FieldDefinitions
            .Where(f => f.MaskVersionId == mask.MaskVersionId && names.Contains(f.Name))
            .Select(f => new { f.Id, f.Name })
            .ToListAsync(cancellationToken);

        var wanted = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [WellKnownMaskIds.PasswordEntryFields.Site] = index.Site,
            [WellKnownMaskIds.PasswordEntryFields.UserName] = index.UserName,
            [WellKnownMaskIds.PasswordEntryFields.App] = index.App,
        };

        var stored = await dbContext.FieldValues
            .Where(v => v.DocumentId == documentId && definitions.Select(d => d.Id).Contains(v.FieldDefinitionId))
            .ToListAsync(cancellationToken);

        foreach (var definition in definitions)
        {
            var value = wanted[definition.Name];
            var row = stored.FirstOrDefault(v => v.FieldDefinitionId == definition.Id);
            if (string.IsNullOrEmpty(value))
            {
                if (row is not null)
                {
                    dbContext.FieldValues.Remove(row);
                }

                continue;
            }

            if (row is null)
            {
                dbContext.FieldValues.Add(new FieldValue
                {
                    Id = Guid.NewGuid(),
                    TenantId = await TenantOfAsync(dbContext, documentId, cancellationToken),
                    DocumentId = documentId,
                    FieldDefinitionId = definition.Id,
                    Value = value,
                });
            }
            else
            {
                row.Value = value;
            }
        }
    }

    // The tenant is the document's own; there is no ambient tenant on this path when a sweep or a service files.
    private static async Task<Guid> TenantOfAsync(AlcifraDbContext dbContext, Guid documentId, CancellationToken cancellationToken) =>
        await dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.TenantId).FirstAsync(cancellationToken);
}

/// <summary>What a client derived for a password entry's autofill index (ADR 0973); an empty value clears its field.</summary>
public sealed class PasswordIndex
{
    public string? Site { get; set; }

    public string? UserName { get; set; }

    public string? App { get; set; }
}
