using Alcifra.Domain.Abstractions;

namespace Alcifra.Domain.Masks;

/// <summary>
/// One mask a DocumentReference field accepts as its target (#2214, ADR 1068). A field with no rows accepts any document, as
/// every reference field did before. Not concurrency-tracked: a create-and-delete join row with no in-place edit (ADR 0878).
/// </summary>
public class FieldDefinitionAcceptedMask : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid FieldDefinitionId { get; set; }

    public Guid MaskId { get; set; }
}
