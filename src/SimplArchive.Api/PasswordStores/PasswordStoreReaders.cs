using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Certificates;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.PasswordStores;

/// <summary>One reader of a password store and the certificates their entries are sealed to (possibly none).</summary>
public sealed record PasswordStoreReader(Guid UserId, string DisplayName, IReadOnlyList<string> CertificatePems);

/// <summary>
/// Who a password store's entries are sealed to (ADR 0964): every active user GRANTED read on the store, each with the
/// certificates the core's attested registry addresses them by.
/// </summary>
/// <remarks>
/// <para>
/// <b>Explicit grants only</b> (owner decision 2026-10-23): a reader holds a grant of read on the store, their own or a
/// group's, inherited down the tree as any grant is. The tenant-administrator bypass makes nobody a reader: an
/// administrator may still manage the store and grant themselves access, which is audited, but is never silently
/// sealed to. My Passwords' reader is its owner, by the personal space's own grant.
/// </para>
/// <para>
/// <b>Only an ANSWER counts</b>: the certificates come from <see cref="ReaderCertificateSet"/>, and only when a module
/// answered (the registry, which addresses attested keys only, ADR 0963). Its fall-back to the reader's own column
/// would address a certificate nobody proved lives on a token, so a reader without an answer is a reader without
/// certificates, said at Debug and visible in the recipients resource.
/// </para>
/// <para>
/// Memoised for the request (registered scoped): the store resource, its re-seal list and its recipients each ask.
/// </para>
/// </remarks>
public sealed class PasswordStoreReaders(
    SimplArchiveDbContext dbContext,
    IEffectiveRightsCalculator rights,
    ReaderCertificateSet readerCertificates,
    ILogger<PasswordStoreReaders> logger)
{
    private readonly Dictionary<Guid, IReadOnlyList<PasswordStoreReader>> _byStore = [];

    public async Task<IReadOnlyList<PasswordStoreReader>> ForStoreAsync(Guid storeId, CancellationToken cancellationToken)
    {
        if (_byStore.TryGetValue(storeId, out var known))
        {
            return known;
        }

        var users = await dbContext.Users
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName)
            .Select(u => new { u.Id, u.DisplayName })
            .ToListAsync(cancellationToken);

        var readers = new List<PasswordStoreReader>();
        foreach (var user in users)
        {
            if (!(await rights.GetGrantedRightsAsync(user.Id, storeId, cancellationToken)).CanReadContent)
            {
                continue;
            }

            var (certificates, outcome) = await readerCertificates.ForUserAsync(user.Id, cancellationToken);
            if (outcome != ReaderCertificateOutcome.Answered && certificates.Count > 0)
            {
                logger.LogDebug(
                    "User {UserId} reads password store {StoreId} but no module answered for their certificates ({Outcome}); "
                    + "their own certificate column is not an attested enrolment, so nothing is sealed to it", user.Id, storeId, outcome);
                certificates = [];
            }

            readers.Add(new PasswordStoreReader(user.Id, user.DisplayName, certificates));
        }

        logger.LogDebug("Password store {StoreId} has {Readers} reader(s) holding {Certificates} certificate(s)",
            storeId, readers.Count, readers.Sum(r => r.CertificatePems.Count));
        _byStore[storeId] = readers;
        return readers;
    }

    /// <summary>Every certificate the store's entries must be sealed to, each once.</summary>
    public async Task<IReadOnlyList<X509Certificate2>> CertificatesAsync(Guid storeId, CancellationToken cancellationToken) =>
        [.. (await ForStoreAsync(storeId, cancellationToken))
            .SelectMany(r => r.CertificatePems)
            .Distinct(StringComparer.Ordinal)
            .Select(pem => X509Certificate2.CreateFromPem(pem))];
}
