using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// The one rule about a personal space's password store the containment tables cannot state (ADR 0964): it is named
/// <c>My Passwords</c>, always. Enforced at the one door every write path uses, so a rename from the workbench, WebDAV
/// or a create under another name is refused alike.
/// </summary>
/// <remarks>
/// <b>Protection by name, admission by mask</b> (ADR 0633): the first level admits the Password Store mask and caps it
/// at one (<see cref="WellKnownMaskIds.ChildCardinalityRules"/>); this adds only that the one is called by its fixed
/// name. A store elsewhere in the archive is named freely.
/// </remarks>
public static class PasswordStoreInvariants
{
    public static async Task EnforceAsync(SimplArchiveDbContext dbContext, Document document, CancellationToken cancellationToken)
    {
        var entry = dbContext.Entry(document);
        var relevant = entry.State == EntityState.Added
            || entry.Property(d => d.Name).IsModified
            || entry.Property(d => d.ParentId).IsModified
            || entry.Property(d => d.MaskVersionId).IsModified;
        if (!relevant || document.ParentId is not { } parentId
            || string.Equals(document.Name, PersonalFolders.MyPasswords, StringComparison.Ordinal))
        {
            return;
        }

        var maskVersionId = document.MaskVersionId;
        var isStore = maskVersionId != Guid.Empty && await dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .AnyAsync(v => v.Id == maskVersionId && v.MaskId == WellKnownMaskIds.PasswordStore, cancellationToken);
        if (!isStore)
        {
            return;
        }

        var inPersonalRoot = await dbContext.Documents.IgnoreQueryFilters(["TenantFilter", "SoftDeleteFilter"])
            .AnyAsync(d => d.Id == parentId && d.PersonalOfUserId != null, cancellationToken);
        if (inPersonalRoot)
        {
            throw PersonalSpaceStructureException.PasswordStoreNameIsFixed(document.Name);
        }
    }
}
