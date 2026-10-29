using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Infrastructure.Certificates.Attestation;

/// <summary>
/// Which attested certificates a tenant accepts NOW (#1790, ADR 0963): attested to a root the installation has switched
/// on and the tenant has not switched off; or, where the tenant allows hardware without attestation, any certificate.
/// Asked at every reader answer, so a root switch takes effect without touching a certificate.
/// </summary>
public sealed record AttestationTrust(bool UnattestedAllowed, IReadOnlySet<string> TrustedRoots, IReadOnlyDictionary<string, string> RootNames)
{
    /// <summary>The state a certificate attested to <paramref name="rootThumbprint"/> is in for this tenant.</summary>
    public AttestationState StateOf(string? rootThumbprint) =>
        rootThumbprint is null ? AttestationState.NotAttested
        : TrustedRoots.Contains(rootThumbprint) ? AttestationState.Attested
        : AttestationState.RootNotTrusted;

    /// <summary>Whether a certificate in this state addresses its holder: trusted, or the tenant accepts the rest.</summary>
    public bool Addresses(string? rootThumbprint) => StateOf(rootThumbprint) == AttestationState.Attested || UnattestedAllowed;

    /// <summary>The tenant's trust, read once.</summary>
    public static async Task<AttestationTrust> ForTenantAsync(AlcifraDbContext db, Guid tenantId, CancellationToken cancellationToken = default)
    {
        var allowed = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.CertificateUnattestedHardwareAllowed).FirstOrDefaultAsync(cancellationToken);
        // The tenant filter scopes this to the ambient tenant, the one asked about.
        var disabled = await db.TenantDisabledAttestationRoots.Select(d => d.RootId).ToListAsync(cancellationToken);
        var roots = await db.TrustedAttestationRoots.AsNoTracking().Select(r => new { r.Id, r.Thumbprint, r.Name, r.Enabled }).ToListAsync(cancellationToken);
        return new AttestationTrust(
            allowed,
            roots.Where(r => r.Enabled && !disabled.Contains(r.Id)).Select(r => r.Thumbprint).ToHashSet(StringComparer.Ordinal),
            roots.ToDictionary(r => r.Thumbprint, r => r.Name, StringComparer.Ordinal));
    }

    /// <summary>
    /// The root the stored evidence of <paramref name="documentId"/> proves for <paramref name="leaf"/>, or null: verified
    /// again from <c>CertificateAttestations</c> against every root the installation knows (ADR 0963). Never throws, since it
    /// runs inside a document's save; evidence that no longer verifies says why at Warning, because its holder silently
    /// stops being addressed.
    /// </summary>
    public static async Task<string?> RootOfAsync(
        AlcifraDbContext db, Guid documentId, X509Certificate2 leaf, ILogger logger, CancellationToken cancellationToken = default)
    {
        var stored = await db.CertificateAttestations.AsNoTracking().FirstOrDefaultAsync(a => a.DocumentId == documentId, cancellationToken);
        if (stored is null)
        {
            return null;
        }

        var verdict = await VerifyAsync(db, new AttestationEvidence(stored.Format, stored.AttestationCertificatePem, stored.IntermediateCertificatePem, stored.Slot), leaf, cancellationToken);
        if (!verdict.Verified)
        {
            logger.LogWarning(
                "The attestation stored for certificate document {DocumentId} does not verify ({Outcome}: {Reason}); the certificate is "
                + "treated as not attested", documentId, verdict.Outcome, verdict.Reason);
            return null;
        }

        return verdict.RootThumbprint;
    }

    /// <summary>Verifies evidence against every root the installation knows, switched on or not.</summary>
    public static async Task<AttestationVerdict> VerifyAsync(
        AlcifraDbContext db, AttestationEvidence evidence, X509Certificate2 leaf, CancellationToken cancellationToken = default)
    {
        var anchors = new List<AttestationAnchor>();
        foreach (var root in await db.TrustedAttestationRoots.AsNoTracking().ToListAsync(cancellationToken))
        {
            try
            {
                anchors.Add(new AttestationAnchor(root.Thumbprint, X509Certificate2.CreateFromPem(root.CertificatePem)));
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Refused at upload; a row that does not parse anchors nothing.
            }
        }

        try
        {
            return PivAttestationVerifier.Verify(evidence, leaf, anchors, AttestationCatalogue.Intermediates);
        }
        finally
        {
            foreach (var anchor in anchors)
            {
                anchor.Certificate.Dispose();
            }
        }
    }
}

/// <summary>A certificate's attestation as a tenant sees it.</summary>
public enum AttestationState
{
    /// <summary>Attested to a root the installation trusts and the tenant has not switched off.</summary>
    Attested,

    /// <summary>Attested, but to a root switched off for the installation or by the tenant.</summary>
    RootNotTrusted,

    /// <summary>No verified attestation: enrolled without one, or from a token that cannot attest.</summary>
    NotAttested,
}
