namespace SimplArchive.Infrastructure.Certificates.Attestation;

/// <summary>
/// What an enrolling client hands over to prove a key was generated on a hardware token (#1790, ADR 0963), as it
/// arrives: the format names the verifier.
/// </summary>
/// <param name="Format">The attestation format; <see cref="PivAttestationVerifier.Format"/> is the one verified today.</param>
/// <param name="AttestationCertificatePem">The token's certificate for the key slot, carrying that slot's public key.</param>
/// <param name="IntermediateCertificatePem">
/// The token's attestation certificate that signed it, followed by any further certificates up to the root, concatenated.
/// </param>
/// <param name="Slot">The key slot the client read it from ("9d"); informational.</param>
public sealed record AttestationEvidence(string Format, string AttestationCertificatePem, string IntermediateCertificatePem, string? Slot);

/// <summary>What verifying evidence found.</summary>
public enum AttestationOutcome
{
    /// <summary>The chain verifies to a known root and attests the enrolled key.</summary>
    Verified,

    /// <summary>The evidence cannot be read, or names a format no verifier understands.</summary>
    Malformed,

    /// <summary>A signature in the chain does not verify: the evidence was altered, or assembled from parts.</summary>
    SignatureInvalid,

    /// <summary>The chain verifies, but attests a different key than the certificate being enrolled.</summary>
    KeyMismatch,

    /// <summary>The chain verifies as far as it goes, but ends at no root this installation knows.</summary>
    RootUnknown,
}

/// <summary>The verdict, and what the evidence says about the token where it verified.</summary>
public sealed record AttestationVerdict(
    AttestationOutcome Outcome, string Reason, string? RootThumbprint = null, string DeviceSerial = "", string FirmwareVersion = "")
{
    public bool Verified => Outcome == AttestationOutcome.Verified;
}

/// <summary>A root the verifier may end a chain at: its thumbprint (what an enrolment records) and its certificate.</summary>
public sealed record AttestationAnchor(string Thumbprint, System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate);
