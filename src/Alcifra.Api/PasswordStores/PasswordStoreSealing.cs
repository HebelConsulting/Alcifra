using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Alcifra.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Alcifra.Domain.Documents;
using Alcifra.Infrastructure.Persistence;
using Alcifra.SealedArchive;

namespace Alcifra.Api.PasswordStores;

/// <summary>
/// The recipients each confirmed entry version is sealed to, remembered per version (ADR 0964). Safe to keep for the
/// life of the process because a confirmed version's bytes never change; bounded, because a store is read often and a
/// tenant may hold many.
/// </summary>
public sealed class SealedRecipientCache
{
    private const int Capacity = 20_000;
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<CmsRecipient>> _byVersion = new();

    public async Task<IReadOnlyList<CmsRecipient>> GetOrReadAsync(Guid versionId, Func<Task<IReadOnlyList<CmsRecipient>>> read)
    {
        if (_byVersion.TryGetValue(versionId, out var known))
        {
            return known;
        }

        var recipients = await read();
        if (_byVersion.Count >= Capacity)
        {
            _byVersion.Clear();
        }

        _byVersion[versionId] = recipients;
        return recipients;
    }
}

/// <summary>A certificate revoked in this tenant's registry, and when.</summary>
public sealed record RevokedCertificate(X509Certificate2 Certificate, DateTimeOffset RevokedAt);

/// <summary>A store's sealing state: the entries to seal again, and whether a revoked certificate can still read one.</summary>
public sealed record PasswordStoreSealState(IReadOnlyList<PasswordItem> EntriesNeedingReseal, bool Urgent);

/// <summary>
/// "Needs re-seal", COMPUTED (ADR 0964): an entry whose current version is not sealed to exactly its store's readers'
/// current certificates. The envelope names its recipients in the clear, so the server answers this without a key and
/// without a stored flag that could drift from the bytes.
/// </summary>
public sealed class PasswordStoreSealing(
    AlcifraDbContext dbContext,
    IObjectStorageClient storage,
    PasswordStoreTree tree,
    PasswordStoreReaders readers,
    SealedRecipientCache cache,
    ILogger<PasswordStoreSealing> logger)
{
    /// <summary>The entries beneath <paramref name="store"/> whose recipients differ from its readers' certificates.</summary>
    /// <remarks>Empty while the readers hold no certificate at all: there is nobody to seal to, so nothing to do.</remarks>
    public async Task<IReadOnlyList<PasswordItem>> EntriesNeedingResealAsync(PasswordItem store, CancellationToken cancellationToken)
    {
        var certificates = await readers.CertificatesAsync(store.StoreId, cancellationToken);
        if (certificates.Count == 0)
        {
            return [];
        }

        var stale = new List<PasswordItem>();
        foreach (var entry in await tree.EntriesAsync(store, cancellationToken))
        {
            if (await NeedsResealAsync(entry, certificates, cancellationToken))
            {
                stale.Add(entry);
            }
        }

        if (stale.Count > 0)
        {
            logger.LogDebug("Password store {StoreId}: {Count} entr(ies) are not sealed to exactly its readers' certificates",
                store.StoreId, stale.Count);
        }

        return stale;
    }

    /// <summary>
    /// The store's state: the entries to re-seal, and URGENT when some entry's current version is still sealed to a
    /// revoked certificate (ADR 0964's compromise workflow): the secret is exposed until it is re-sealed and changed.
    /// </summary>
    public async Task<PasswordStoreSealState> StateAsync(PasswordItem store, CancellationToken cancellationToken)
    {
        var stale = await EntriesNeedingResealAsync(store, cancellationToken);
        var revoked = await RevokedAsync(cancellationToken);
        var urgent = false;
        if (revoked.Count > 0)
        {
            foreach (var entry in await tree.EntriesAsync(store, cancellationToken))
            {
                if (await CurrentVersion.ResolveAsync(dbContext.DocumentVersions, entry.Id, entry.CurrentVersionId, cancellationToken)
                        is { Status: DocumentVersionStatus.Confirmed } version
                    && (await RecipientsAsync(version, cancellationToken)).Any(r => revoked.Any(c => r.Is(c.Certificate))))
                {
                    urgent = true;
                    break;
                }
            }
        }

        if (urgent)
        {
            logger.LogDebug("Password store {StoreId} needs re-sealing URGENTLY: an entry is still sealed to a revoked certificate", store.StoreId);
        }

        return new PasswordStoreSealState(stale, urgent);
    }

    /// <summary>The tenant's revoked certificates (a revocation is a state on the registry row, never a deletion).</summary>
    public async Task<IReadOnlyList<RevokedCertificate>> RevokedAsync(CancellationToken cancellationToken)
    {
        if (_revoked is not null)
        {
            return _revoked;
        }

        var rows = await dbContext.EnrolledCertificates.AsNoTracking()
            .Where(c => c.RevokedAt != null)
            .Select(c => new { c.CertificatePem, c.RevokedAt })
            .ToListAsync(cancellationToken);
        _revoked = [.. rows.Select(r => new RevokedCertificate(X509Certificate2.CreateFromPem(r.CertificatePem), r.RevokedAt!.Value))];
        return _revoked;
    }

    private IReadOnlyList<RevokedCertificate>? _revoked;

    /// <summary>Whether one entry's current version is sealed to other certificates than <paramref name="certificates"/>.</summary>
    public async Task<bool> NeedsResealAsync(PasswordItem entry, IReadOnlyList<X509Certificate2> certificates, CancellationToken cancellationToken)
    {
        if (certificates.Count == 0
            || await CurrentVersion.ResolveAsync(dbContext.DocumentVersions, entry.Id, entry.CurrentVersionId, cancellationToken)
                is not { Status: DocumentVersionStatus.Confirmed } version)
        {
            return false;
        }

        var recipients = await RecipientsAsync(version, cancellationToken);
        return !CmsRecipients.AddressedToExactly(recipients, certificates);
    }

    /// <summary>The recipients of one version's envelope, read once per version.</summary>
    public Task<IReadOnlyList<CmsRecipient>> RecipientsAsync(DocumentVersion version, CancellationToken cancellationToken) =>
        cache.GetOrReadAsync(version.Id, async () =>
        {
            using var buffer = new MemoryStream();
            await using (var content = await storage.GetObjectAsync(version.ObjectKey, cancellationToken))
            {
                await content.CopyToAsync(buffer, cancellationToken);
            }

            var recipients = CmsRecipients.Of(buffer.ToArray());
            logger.LogTrace("Version {VersionId} of password entry {DocumentId} is sealed to {Count} recipient(s)",
                version.Id, version.DocumentId, recipients.Count);
            return recipients;
        });
}
