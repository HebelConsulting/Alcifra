using SimplArchive.Api.Errors.Exceptions.LegalHolds;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.Documents;

/// <summary>The module facade's way into <see cref="DocumentPurger"/> (ABI 1.18, ADR 0937): purge at once, no recycle bin.</summary>
public sealed class ModuleDocumentPurger(DocumentPurger purger) : IModuleDocumentPurger
{
    public async Task<bool> PurgeAsync(Guid documentId, bool respectLegalHold, CancellationToken cancellationToken)
    {
        if (await purger.CollectSubtreeAsync(documentId, cancellationToken) is not { Count: > 0 } subtree)
        {
            return false;
        }

        if (respectLegalHold && await purger.AnyHeldAsync([.. subtree.Select(d => d.Id)], cancellationToken))
        {
            throw new DocumentUnderLegalHoldException();
        }

        await purger.PurgeAsync(subtree, cancellationToken);   // the WORM guard inside still refuses a locked object
        return true;
    }
}
