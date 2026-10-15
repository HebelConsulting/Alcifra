using SimplArchive.Domain.Abstractions;

namespace SimplArchive.Domain.Certificates;

/// <summary>
/// One enrolled certificate, projected from its document so "which certificates is this reader addressed by?" is
/// one indexed read (#1790, ADR 0935). The core-owned successor of the Encryption Module's own projection: one
/// registry for every module that encrypts to users (the Encryption Module, the Messenger), so a user enrols once
/// and a revocation reaches documents and messages alike.
/// </summary>
/// <remarks>
/// <para>
/// <b>DERIVED, by contract.</b> The certificate IS a document wearing the core <c>Certificate</c> mask: its content
/// is the certificate, and Holder, Label and Revoked on are its fields. Every row here is rebuilt from those
/// documents (at startup, and with every save of one, in the same transaction, ADR 0848); delete the table and
/// rebuild it and nothing is lost.
/// </para>
/// <para>
/// <b>Not concurrency-tracked, by owner decision (2026-10-15).</b> The rows are machine-owned: the projection
/// overwrites them from the document and nobody edits them. The document is tracked, and that is where a label is
/// changed or a revocation recorded (the standing principle's "machine-owned" exemption).
/// </para>
/// </remarks>
public sealed class EnrolledCertificate : ITenantScoped
{
    /// <summary>The certificate document this row projects: one row per document, so this is the key.</summary>
    public Guid DocumentId { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The holder: the user the document's Holder field resolves to (the core's ResourcePrincipals).</summary>
    public Guid UserId { get; set; }

    /// <summary>The END-ENTITY certificate, PEM-encoded: never the chain, never a private key.</summary>
    public string CertificatePem { get; set; } = string.Empty;

    /// <summary>What the holder calls this key ("YubiKey 5C"); the certificate's subject when the field is empty.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>SHA-256 of the leaf, uppercase hex: one enrolment per key, and revocation by reference.</summary>
    public string Thumbprint { get; set; } = string.Empty;

    /// <summary>The issuer's serial, uppercase hex: the join a certification authority's CRL can make.</summary>
    public string Serial { get; set; } = string.Empty;

    public DateTimeOffset NotBefore { get; set; }

    public DateTimeOffset NotAfter { get; set; }

    /// <summary>When the certificate was revoked, from the document's Revoked on field: a state, never a deletion.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
