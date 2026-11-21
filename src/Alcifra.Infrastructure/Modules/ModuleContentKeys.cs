using Microsoft.EntityFrameworkCore;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Infrastructure.Modules;

/// <summary>
/// Where a document whose mask declares a content segment keeps its bytes (ABI 1.18, ADR 0937; the core's sealed masks,
/// ADR 0964): under the segment rather than in the archive's year folders. Only an ARCHIVE key moves; staged content
/// keeps its own area. One implementation for the module facade, the core's own version upload and every other writer
/// of document content — so no path can file one mask's content in two layouts (#2304).
/// </summary>
public static class ContentSegmentKeys
{
    public static async Task<string> InSegmentAsync(
        AlcifraDbContext dbContext, IModuleMaskDeclarations declarations, string archiveKey, Guid maskVersionId, Guid tenantId,
        Guid storageFolderId, Guid versionId, CancellationToken cancellationToken)
    {
        if (ObjectKeyBuilder.IsModuleStagedContentKey(archiveKey))
        {
            return archiveKey;
        }

        return await SegmentOfAsync(dbContext, declarations, maskVersionId, cancellationToken) is { } segment
            ? ObjectKeyBuilder.ModuleContentKey(tenantId, segment, storageFolderId, versionId, Path.GetExtension(archiveKey))
            : archiveKey;
    }

    /// <summary>
    /// One document-content key from scratch (#2304): the segment's key when the mask version declares one, the
    /// archive key otherwise. THE way a writer builds document content, so the WebDAV door, the module facade, the
    /// importer and the client upload cannot disagree about where a mask's bytes live.
    /// </summary>
    public static async Task<string> ContentKeyAsync(
        AlcifraDbContext dbContext, IModuleMaskDeclarations declarations, Guid tenantId, Guid? maskVersionId,
        Guid storageFolderId, Guid versionId, DateTimeOffset filingDate, string? extension, CancellationToken cancellationToken) =>
        ContentKey(tenantId, await SegmentOfAsync(dbContext, declarations, maskVersionId, cancellationToken),
            storageFolderId, versionId, filingDate, extension);

    /// <summary>The sync half, for a caller that already resolved the segment (a lambda that only receives ids).</summary>
    public static string ContentKey(
        Guid tenantId, string? segment, Guid storageFolderId, Guid versionId, DateTimeOffset filingDate, string? extension) =>
        segment is { } under
            ? ObjectKeyBuilder.ModuleContentKey(tenantId, under, storageFolderId, versionId, extension)
            : ObjectKeyBuilder.Build(tenantId, filingDate, storageFolderId, versionId, extension);

    /// <summary>
    /// The content segment the given mask version declares, or null — the one lookup every writer and the mask-change
    /// move share. A maskless document (an upload not yet finalized) has no segment.
    /// </summary>
    public static async Task<string?> SegmentOfAsync(
        AlcifraDbContext dbContext, IModuleMaskDeclarations declarations, Guid? maskVersionId, CancellationToken cancellationToken)
    {
        if (maskVersionId is not { } versionId)
        {
            return null;
        }

        var maskId = await dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.Id == versionId).Select(v => (Guid?)v.MaskId).FirstOrDefaultAsync(cancellationToken);
        return declarations.ContentSegmentFor(maskId);
    }
}

/// <summary>The module facade's door to <see cref="ContentSegmentKeys"/>, resolving the declarations from its services.</summary>
internal static class ModuleContentKeys
{
    public static Task<string> InSegmentAsync(
        AlcifraDbContext dbContext, IServiceProvider? services, string archiveKey, Guid maskVersionId, Guid tenantId,
        Guid storageFolderId, Guid versionId, CancellationToken cancellationToken) =>
        services?.GetService(typeof(IModuleMaskDeclarations)) is IModuleMaskDeclarations declarations
            ? ContentSegmentKeys.InSegmentAsync(dbContext, declarations, archiveKey, maskVersionId, tenantId, storageFolderId, versionId, cancellationToken)
            : Task.FromResult(archiveKey);
}
