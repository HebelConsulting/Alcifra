using System.Security.Cryptography.X509Certificates;
using SimplArchive.Api.Audience;

namespace SimplArchive.Api.PasswordStores;

/// <summary>One reader of a password store and the certificates their entries are sealed to (possibly none).</summary>
public sealed record PasswordStoreReader(Guid UserId, string DisplayName, IReadOnlyList<string> CertificatePems);

/// <summary>
/// Who a password store's entries are sealed to: the store's EFFECTIVE AUDIENCE (ADR 0983, superseding ADR 0964's
/// "every reader") — its declared members who are active readers, each with the attested certificates the core's
/// registry addresses them by.
/// </summary>
/// <remarks>
/// The store's view of <see cref="ContentAudience"/>, kept under its old name because every store service asks it; a
/// "reader" here means a member of the audience. Memoised for the request by the audience itself.
/// </remarks>
public sealed class PasswordStoreReaders(ContentAudience audience)
{
    public async Task<IReadOnlyList<PasswordStoreReader>> ForStoreAsync(Guid storeId, CancellationToken cancellationToken) =>
        await audience.RootOfAsync(storeId, cancellationToken) is { } root
            ? [.. (await audience.EffectiveAsync(root, cancellationToken)).Select(m => new PasswordStoreReader(m.UserId, m.DisplayName, m.CertificatePems))]
            : [];

    /// <summary>Every certificate the store's entries must be sealed to, each once.</summary>
    public async Task<IReadOnlyList<X509Certificate2>> CertificatesAsync(Guid storeId, CancellationToken cancellationToken) =>
        [.. (await ForStoreAsync(storeId, cancellationToken))
            .SelectMany(r => r.CertificatePems)
            .Distinct(StringComparer.Ordinal)
            .Select(pem => X509Certificate2.CreateFromPem(pem))];
}
