namespace Alcifra.Api.OfflineArchives;

/// <summary>
/// The installation's signing key for offline archives (ADR 0961): sourced from OpenBao's
/// <c>secret/simplarchive/archive-signing</c> (<c>current</c> = a PKI issue response, <c>previous</c> = the retired
/// certificates' PEM) like the OpenIddict certificates, or set directly in this section.
/// </summary>
public sealed class ArchiveSigningOptions
{
    public const string SectionName = "OfflineArchive";

    /// <summary>The current signing certificate (PEM).</summary>
    public string? SigningCertificatePem { get; set; }

    /// <summary>Its private key (PEM). A secret: never logged.</summary>
    public string? SigningKeyPem { get; set; }

    /// <summary>Every retired signing certificate (PEM, concatenated): kept published so old archives still verify.</summary>
    public string? PreviousSigningCertificatesPem { get; set; }

    /// <summary>A certificate without its key, or a key without its certificate: refused outside Development.</summary>
    public bool IsPartlyConfigured => string.IsNullOrWhiteSpace(SigningCertificatePem) != string.IsNullOrWhiteSpace(SigningKeyPem);
}
