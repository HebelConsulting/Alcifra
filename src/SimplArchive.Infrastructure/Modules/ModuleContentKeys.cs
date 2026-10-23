using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Where a document whose mask declares a content segment keeps its bytes (ABI 1.18, ADR 0937; the core's sealed masks,
/// ADR 0964): under the segment rather than in the archive's year folders. Only an ARCHIVE key moves; staged content
/// keeps its own area. One implementation for the module facade and the core's own version upload, so the two cannot
/// file one mask's content in two layouts.
/// </summary>
public static class ContentSegmentKeys
{
    public static async Task<string> InSegmentAsync(
        SimplArchiveDbContext dbContext, IModuleMaskDeclarations declarations, string archiveKey, Guid maskVersionId, Guid tenantId,
        Guid storageFolderId, Guid versionId, CancellationToken cancellationToken)
    {
        if (ObjectKeyBuilder.IsModuleStagedContentKey(archiveKey))
        {
            return archiveKey;
        }

        var maskId = await dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.Id == maskVersionId).Select(v => (Guid?)v.MaskId).FirstOrDefaultAsync(cancellationToken);
        return declarations.ContentSegmentFor(maskId) is { } segment
            ? ObjectKeyBuilder.ModuleContentKey(tenantId, segment, storageFolderId, versionId, Path.GetExtension(archiveKey))
            : archiveKey;
    }
}

/// <summary>The module facade's door to <see cref="ContentSegmentKeys"/>, resolving the declarations from its services.</summary>
internal static class ModuleContentKeys
{
    public static Task<string> InSegmentAsync(
        SimplArchiveDbContext dbContext, IServiceProvider? services, string archiveKey, Guid maskVersionId, Guid tenantId,
        Guid storageFolderId, Guid versionId, CancellationToken cancellationToken) =>
        services?.GetService(typeof(IModuleMaskDeclarations)) is IModuleMaskDeclarations declarations
            ? ContentSegmentKeys.InSegmentAsync(dbContext, declarations, archiveKey, maskVersionId, tenantId, storageFolderId, versionId, cancellationToken)
            : Task.FromResult(archiveKey);
}
