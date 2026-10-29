using Alcifra.Domain.Abstractions;

namespace Alcifra.Domain.Documents;

// One member of a sealed item's declared AUDIENCE (ADR 0983): who is meant to decrypt it, and whether they may change
// that. Set on the document whose mask declares an audience — a Password Store (for everything beneath it) or a
// Sealed document — never on a folder or entry inside a store. Exactly one of UserId / GroupId is set (CHECK-enforced);
// a manager is always a user (CHECK-enforced) and always a member, because this one row is both.
//
// NOT IConcurrencyTracked — an exemption the owner granted (ADR 0878, "a child guarded by a tracked parent",
// 2026-10-26): rows are created and deleted, never edited, and only by one whole-list PUT under the OWNING DOCUMENT's
// If-Match, which moves that document's token. A stale second list is therefore a 412 on the document.
public class ContentAudienceMember : ITenantScoped
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    // The document that declares the audience: a Password Store or a Sealed document.
    public Guid DocumentId { get; set; }

    // Exactly one of these is set (CHECK-enforced). A group's members are in the audience while they belong to it.
    public Guid? UserId { get; set; }

    public Guid? GroupId { get; set; }

    // May add and remove members and managers. Only ever true on a user row (CHECK-enforced).
    public bool IsManager { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
