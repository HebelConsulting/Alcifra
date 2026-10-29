using Alcifra.Domain.Abstractions;

namespace Alcifra.Domain.Certificates;

/// <summary>
/// A trusted attestation root a tenant has switched off for itself (#1790, ADR 0963): a tenant administrator may narrow
/// what the installation trusts, never widen it.
/// </summary>
/// <remarks>
/// <b>Not concurrency-tracked, by owner decision (2026-10-22): a create-and-delete join row.</b> A row is added when a
/// root is switched off and deleted when it is switched back on; nothing edits one in place. The tenant's selection is
/// written as ONE <c>PUT</c> of the whole list, guarded by the TENANT's ETag, so two administrators cannot silently
/// overwrite each other's selection.
/// </remarks>
public sealed class TenantDisabledAttestationRoot : ITenantScoped
{
    public Guid TenantId { get; set; }

    public Guid RootId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
