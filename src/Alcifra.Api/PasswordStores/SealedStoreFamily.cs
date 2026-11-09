using Alcifra.Api.Controllers;
using Alcifra.Domain.Masks;

namespace Alcifra.Api.PasswordStores;

/// <summary>
/// One kind of sealed store (ADR 1084): the Password Store (ADR 0964) or the CI/CD Store. What differs between them is
/// declared here once, and every walk, door and sweep over a store reads it, so the two share one implementation
/// (ADR 0877) rather than two that drift.
/// </summary>
/// <param name="StoreMask">The store's folder mask, which declares the audience its entries are sealed to.</param>
/// <param name="FolderMask">The mask of a folder inside the store; null for a flat store.</param>
/// <param name="EntryMask">The mask of a sealed entry.</param>
/// <param name="EntryCreated">The audit action of an entry's first version.</param>
/// <param name="EntryChanged">The audit action of a new value.</param>
/// <param name="EntryResealed">The audit action of a re-seal, the same value to other certificates.</param>
/// <param name="EntryExposed">The audit action of an entry a revoked certificate could read.</param>
/// <param name="EntryAbandonedRemoved">The audit action of an entry whose first upload never finished.</param>
public sealed record SealedStoreFamily(
    Guid StoreMask, Guid? FolderMask, Guid EntryMask,
    string EntryCreated, string EntryChanged, string EntryResealed, string EntryExposed, string EntryAbandonedRemoved)
{
    public static SealedStoreFamily Password { get; } = new(
        WellKnownMaskIds.PasswordStore, WellKnownMaskIds.PasswordFolder, WellKnownMaskIds.PasswordEntry,
        AuditActions.PasswordEntryCreated, AuditActions.PasswordEntryChanged, AuditActions.PasswordEntryResealed,
        AuditActions.PasswordEntryExposed, AuditActions.PasswordEntryAbandonedRemoved);

    public static SealedStoreFamily CiCd { get; } = new(
        WellKnownMaskIds.CiCdStore, null, WellKnownMaskIds.CiCdSecret,
        AuditActions.CiCdSecretCreated, AuditActions.CiCdSecretChanged, AuditActions.CiCdSecretResealed,
        AuditActions.CiCdSecretExposed, AuditActions.CiCdSecretAbandonedRemoved);

    /// <summary>Every family, for the sweeps that serve them all.</summary>
    public static IReadOnlyList<SealedStoreFamily> All { get; } = [Password, CiCd];

    /// <summary>The entry masks of every family: what the history rules (ADR 0964) apply to, and nothing else.</summary>
    public static IReadOnlySet<Guid> EntryMasks { get; } = All.Select(f => f.EntryMask).ToHashSet();

    /// <summary>The family a store, folder or entry mask belongs to, or null.</summary>
    public static SealedStoreFamily? Of(Guid? maskId) =>
        maskId is not { } mask ? null : All.FirstOrDefault(f => f.StoreMask == mask || f.FolderMask == mask || f.EntryMask == mask);
}
