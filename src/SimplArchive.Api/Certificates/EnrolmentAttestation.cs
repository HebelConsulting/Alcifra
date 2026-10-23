using System.Security.Cryptography.X509Certificates;
using SimplArchive.Api.Errors.Exceptions.Certificates;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Certificates;
using SimplArchive.Infrastructure.Certificates.Attestation;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Certificates;

/// <summary>
/// The enrolment door's decision on a key's attestation (#1790, ADR 0963): verified BEFORE anything is filed, refused by
/// name, and returned as the evidence row to store with the Certificate document.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the tenant's "hardware without attestation" switch admits, and what it never does.</b> It admits a key with no
/// evidence (a token that cannot attest) and a key whose evidence chains to a root not trusted here (unknown, or switched
/// off): both are then "not attested". It never admits evidence that is broken or attests another key, because that is
/// not hardware without attestation but a statement that is false.
/// </para>
/// <para>
/// Evidence admitted that way is still STORED: it is the truth about the key, and a root trusted later (uploaded or
/// switched on) makes the certificate attested without anybody enrolling again.
/// </para>
/// </remarks>
public sealed class EnrolmentAttestation(SimplArchiveDbContext dbContext, ICurrentTenantAccessor tenant, ILogger<EnrolmentAttestation> logger)
{
    /// <summary>The evidence row to store for <paramref name="leaf"/>, null when none was sent and none is required; throws a named refusal.</summary>
    public async Task<CertificateAttestation?> DecideAsync(AttestationEvidence? evidence, X509Certificate2 leaf, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Attested enrolment needs a current tenant.");
        var trust = await AttestationTrust.ForTenantAsync(dbContext, tenantId, cancellationToken);
        if (evidence is null)
        {
            return trust.UnattestedAllowed ? null : throw Refused(new CertificateNotAttestedException());
        }

        var verdict = await AttestationTrust.VerifyAsync(dbContext, evidence, leaf, cancellationToken);
        var row = new CertificateAttestation
        {
            TenantId = tenantId,
            Format = PivAttestationVerifier.Format,
            AttestationCertificatePem = evidence.AttestationCertificatePem,
            IntermediateCertificatePem = evidence.IntermediateCertificatePem,
            DeviceSerial = verdict.DeviceSerial,
            FirmwareVersion = verdict.FirmwareVersion,
            Slot = evidence.Slot ?? string.Empty,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        return verdict.Outcome switch
        {
            AttestationOutcome.Malformed or AttestationOutcome.SignatureInvalid => throw Refused(new CertificateAttestationInvalidException(verdict.Reason)),
            AttestationOutcome.KeyMismatch => throw Refused(new CertificateAttestationKeyMismatchException()),
            AttestationOutcome.RootUnknown => trust.UnattestedAllowed ? row : throw Refused(new CertificateAttestationRootNotTrustedException(verdict.Reason)),
            _ when trust.StateOf(verdict.RootThumbprint) == AttestationState.Attested || trust.UnattestedAllowed => row,
            _ => throw Refused(new CertificateAttestationRootNotTrustedException(
                $"it chains to '{trust.RootNames.GetValueOrDefault(verdict.RootThumbprint!, verdict.RootThumbprint!)}', which is switched off "
                + "for this installation or this tenant.")),
        };
    }

    // A refusal the caller sees by code, so Debug (CLAUDE.md: a handled 4xx); the enrolment door audits it.
    private CertificateException Refused(CertificateException refusal)
    {
        logger.LogDebug("Certificate enrolment refused: {ErrorCode}", refusal.ErrorCode);
        return refusal;
    }
}
