using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Alcifra.Application.Abstractions;
using Alcifra.Domain.Documents;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Encryption;
using Alcifra.Infrastructure.Persistence;
using Alcifra.ModuleAbi;

namespace Alcifra.Infrastructure.Modules;

/// <summary>The facade's content links (ABI 1.8 and 1.28): the presigned URL a module hands a client, with the player's
/// form carrying a content type and an inline disposition (ADR 1038). Split from the facade by responsibility.</summary>
public sealed partial class ModuleArchiveFacade
{
    /// <summary>
    /// A presigned URL for the current content (ABI 1.8, ADR 0899): the module-side form of "the Api never proxies
    /// stored bytes". Same consent gate as a content read; through <c>IObjectStorageClient</c>, so the at-rest
    /// decorator applies and the strict tier answers null; the ttl is clamped to <see cref="ContentUrlCeiling"/>.
    /// A download is saved under the document's name, not the storage key's. The URL is logged never, the document id at Debug.
    /// </summary>
    public Task<Uri?> GetDocumentContentUrlAsync(Guid documentId, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        PresignContentAsync(documentId, ttl, contentType: null, inline: false, cancellationToken);

    /// <summary>The player's form (ABI 1.28, ADR 1038): the content type on the link and an inline disposition.</summary>
    public Task<Uri?> GetDocumentContentUrlAsync(Guid documentId, TimeSpan ttl, string contentType, bool inline, CancellationToken cancellationToken = default) =>
        PresignContentAsync(documentId, ttl, contentType, inline, cancellationToken);

    private async Task<Uri?> PresignContentAsync(Guid documentId, TimeSpan ttl, string? contentType, bool inline, CancellationToken cancellationToken)
    {
        if (!await ModuleMaySeeAsync(documentId, cancellationToken) || await IsAudienceContentAsync(documentId, cancellationToken))
        {
            return null;
        }

        var document = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => new { d.CurrentVersionId, d.Name })
            .SingleOrDefaultAsync(cancellationToken);
        var version = await CurrentVersion.ResolveAsync(_dbContext.DocumentVersions, documentId, document?.CurrentVersionId, cancellationToken);
        if (version is null)
        {
            return null;
        }

        if (_objectStorage is null)
        {
            throw new InvalidOperationException(
                "Content URLs need an object-storage client; the host wires one — a test facade that presigns must supply it.");
        }

        var lifetime = ttl <= TimeSpan.Zero || ttl > ContentUrlCeiling ? ContentUrlCeiling : ttl;
        var url = inline
            ? await _objectStorage.GetPresignedPreviewUrlAsync(version.ObjectKey, lifetime, contentType: contentType, cancellationToken: cancellationToken)
            : await _objectStorage.GetPresignedDownloadUrlAsync(version.ObjectKey, lifetime, document?.Name, contentType, cancellationToken: cancellationToken);
        _logger?.LogDebug(
            "Module {ModuleId} presigned the content of document {DocumentId} for {Lifetime}; issued: {Issued}",
            _identity?.ModuleId, documentId, lifetime, url is not null);

        return url;
    }
}
