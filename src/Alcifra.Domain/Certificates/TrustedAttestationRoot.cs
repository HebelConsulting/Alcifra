using Alcifra.Domain.Abstractions;

namespace Alcifra.Domain.Certificates;

/// <summary>
/// A certification authority whose attestations this INSTALLATION accepts as proof that a reader's key was generated
/// on, and never leaves, a hardware token (#1790, ADR 0963). Set by the platform administrator; a tenant administrator
/// may switch some of them off for their tenant (<see cref="TenantDisabledAttestationRoot"/>), never add one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Installation-wide, so NOT tenant-scoped</b>, like <c>PlatformAdministrator</c>: the organisation running the
/// installation decides which hardware it trusts (owner decision 2026-10-22), not each tenant and not us.
/// </para>
/// <para>
/// <b>Catalogue roots</b> (<see cref="CatalogueKey"/> set) are the attestation roots of the hardware the project tests
/// with, shipped as resources and seeded <b>switched off</b>: a fresh installation trusts nothing until an
/// administrator decides. An uploaded root has no catalogue key.
/// </para>
/// <para>
/// <b>Concurrency-tracked</b>: a rename and a switch are edits a second administrator can collide with, so the ETag goes
/// out and <c>If-Match</c> comes in.
/// </para>
/// </remarks>
public sealed class TrustedAttestationRoot : IConcurrencyTracked
{
    public Guid Id { get; set; }

    /// <summary>What an administrator calls it; the certificate's subject by default.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The root's PUBLIC certificate, PEM-encoded. Never a private key.</summary>
    public string CertificatePem { get; set; } = string.Empty;

    /// <summary>SHA-256 of the certificate, uppercase hex: one row per root, and what an enrolment records.</summary>
    public string Thumbprint { get; set; } = string.Empty;

    /// <summary>The shipped catalogue entry this row is, or null for a root an administrator uploaded.</summary>
    public string? CatalogueKey { get; set; }

    /// <summary>Whether the installation trusts it. A catalogue root starts switched off.</summary>
    public bool Enabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid ConcurrencyToken { get; set; }
}
