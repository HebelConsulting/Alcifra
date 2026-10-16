using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Where a module's document keeps its bytes (ABI 1.18, ADR 0937): under its mask's declared content segment rather than
/// in the archive's year folders. Only an ARCHIVE key moves; staged content keeps its own area.
/// </summary>
internal static class ModuleContentKeys
{
    public static async Task<string> InSegmentAsync(
        SimplArchiveDbContext dbContext, IServiceProvider? services, string archiveKey, Guid maskVersionId, Guid tenantId,
        Guid storageFolderId, Guid versionId, CancellationToken cancellationToken)
    {
        if (services?.GetService(typeof(IModuleMaskDeclarations)) is not IModuleMaskDeclarations declarations
            || ObjectKeyBuilder.IsModuleStagedContentKey(archiveKey))
        {
            return archiveKey;
        }

        var maskId = await dbContext.MaskVersions.Where(v => v.Id == maskVersionId).Select(v => (Guid?)v.MaskId).FirstOrDefaultAsync(cancellationToken);
        return declarations.ContentSegmentFor(maskId) is { } segment
            ? ObjectKeyBuilder.ModuleContentKey(tenantId, segment, storageFolderId, versionId, Path.GetExtension(archiveKey))
            : archiveKey;
    }
}
