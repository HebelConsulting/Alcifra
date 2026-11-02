using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.PasswordStores;

/// <summary>
/// The autofill index values an entry can carry (ADR 0973, ADR 1013's "Has code" and ADR 1033's passkey), read for a set of entries in ONE
/// query.
/// </summary>
/// <remarks>
/// They are plain index fields on the Password Entry mask, derived by the client from the sealed content and written
/// with the version it files — the server never decrypts. A row that predates the fields (or has not been re-filed
/// since) simply has none, which is why every value is nullable rather than defaulted.
/// </remarks>
public sealed class PasswordIndexValues
{
    public string? Site { get; private init; }

    public string? UserName { get; private init; }

    public string? App { get; private init; }

    /// <summary>Whether the sealed content carries a verification code (ADR 1013); false where the field is absent.</summary>
    public bool HasCode { get; private init; }

    /// <summary>The site the entry's passkey signs in to (ADR 1033), or null where it has none.</summary>
    public string? PasskeySite { get; private init; }

    /// <summary>The passkey's credential id, base64url (ADR 1033).</summary>
    public string? PasskeyId { get; private init; }

    /// <summary>The site's user handle for the passkey's account, base64url (ADR 1033).</summary>
    public string? PasskeyUserHandle { get; private init; }

    /// <summary>Whether the entry holds a passkey: it has a credential id.</summary>
    public bool HasPasskey => !string.IsNullOrEmpty(PasskeyId);

    public static readonly PasswordIndexValues None = new();

    /// <summary>One query for every entry named: a folder's rows and a single entry share this path.</summary>
    public static async Task<Dictionary<Guid, PasswordIndexValues>> LoadAsync(
        AlcifraDbContext dbContext, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        if (documentIds.Count == 0)
        {
            return [];
        }

        var rows = await (
                from value in dbContext.FieldValues
                join definition in dbContext.FieldDefinitions on value.FieldDefinitionId equals definition.Id
                where documentIds.Contains(value.DocumentId)
                    && (definition.Name == WellKnownMaskIds.PasswordEntryFields.Site
                        || definition.Name == WellKnownMaskIds.PasswordEntryFields.UserName
                        || definition.Name == WellKnownMaskIds.PasswordEntryFields.App
                        || definition.Name == WellKnownMaskIds.PasswordEntryFields.HasCode
                        || definition.Name == WellKnownMaskIds.PasswordEntryFields.PasskeySite
                        || definition.Name == WellKnownMaskIds.PasswordEntryFields.PasskeyId
                        || definition.Name == WellKnownMaskIds.PasswordEntryFields.PasskeyUserHandle)
                select new { value.DocumentId, definition.Name, value.Value })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.DocumentId)
            .ToDictionary(
                g => g.Key,
                g => new PasswordIndexValues
                {
                    Site = g.FirstOrDefault(r => r.Name == WellKnownMaskIds.PasswordEntryFields.Site)?.Value,
                    UserName = g.FirstOrDefault(r => r.Name == WellKnownMaskIds.PasswordEntryFields.UserName)?.Value,
                    App = g.FirstOrDefault(r => r.Name == WellKnownMaskIds.PasswordEntryFields.App)?.Value,
                    // A Boolean index field is stored as its invariant text, as every Boolean field is (SearchFieldMapper).
                    HasCode = bool.TryParse(g.FirstOrDefault(r => r.Name == WellKnownMaskIds.PasswordEntryFields.HasCode)?.Value, out var hasCode)
                        && hasCode,
                    PasskeySite = g.FirstOrDefault(r => r.Name == WellKnownMaskIds.PasswordEntryFields.PasskeySite)?.Value,
                    PasskeyId = g.FirstOrDefault(r => r.Name == WellKnownMaskIds.PasswordEntryFields.PasskeyId)?.Value,
                    PasskeyUserHandle = g.FirstOrDefault(r => r.Name == WellKnownMaskIds.PasswordEntryFields.PasskeyUserHandle)?.Value,
                });
    }
}
