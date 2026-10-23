using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Certificates;

/// <summary>
/// The evidence a reader certificate's key was generated on a hardware token (#1790, ADR 0963): the token's own
/// statement about the key, signed by the token's attestation key, whose certificate chains to a manufacturer's root.
/// The SOURCE OF TRUTH for the evidence; <see cref="EnrolledCertificate.AttestationRootThumbprint"/> is derived from it
/// and verified again at every projection and rebuild.
/// </summary>
/// <remarks>
/// <para>
/// <b>Format "piv"</b>: <see cref="AttestationCertificatePem"/> is the certificate the token issues for one key slot,
/// carrying that slot's public key; <see cref="IntermediateCertificatePem"/> is the token's attestation certificate that
/// signed it, followed by any further certificates up to (not including) the root, concatenated.
/// </para>
/// <para>
/// <b>Not concurrency-tracked, by owner decision (2026-10-22)</b>: written once with the enrolment, never updated, and
/// guarded by the Certificate document it belongs to, which is tracked. A certificate enrolled without evidence may
/// gain it once, by enrolling the same key again with its attestation; that, too, is a write and not an edit.
/// </para>
/// </remarks>
public sealed class CertificateAttestation : ITenantScoped
{
    /// <summary>The Certificate document this evidence belongs to: one per document, so this is the key.</summary>
    public Guid DocumentId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The attestation format, which names the verifier ("piv").</summary>
    public string Format { get; set; } = string.Empty;

    /// <summary>The key slot's attestation certificate, PEM-encoded.</summary>
    public string AttestationCertificatePem { get; set; } = string.Empty;

    /// <summary>The token's attestation certificate and any further intermediates, PEM-encoded and concatenated.</summary>
    public string IntermediateCertificatePem { get; set; } = string.Empty;

    /// <summary>The token's serial number, as the attestation states it; empty when it states none.</summary>
    public string DeviceSerial { get; set; } = string.Empty;

    /// <summary>The token's firmware version ("5.7.4"), as the attestation states it; empty when it states none.</summary>
    public string FirmwareVersion { get; set; } = string.Empty;

    /// <summary>The key slot ("9d"), as the enrolling client named it.</summary>
    public string Slot { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
