namespace Alcifra.Application.Abstractions;

/// <summary>
/// Purges a module's document and everything below it at once, with its stored objects (ABI 1.18, ADR 0937): the core's
/// one purge path (the recycle bin's), reached from the module facade, which cannot reference the Api layer.
/// </summary>
public interface IModuleDocumentPurger
{
    /// <summary>Purges <paramref name="documentId"/> and its subtree; false when it does not exist.</summary>
    /// <param name="respectLegalHold">False for a mask declared exempt from legal hold.</param>
    Task<bool> PurgeAsync(Guid documentId, bool respectLegalHold, CancellationToken cancellationToken);
}
