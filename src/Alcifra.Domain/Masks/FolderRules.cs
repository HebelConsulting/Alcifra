namespace Alcifra.Domain.Masks;

/// <summary>A cap on how many children wearing one mask a folder may hold (#596).</summary>
/// <param name="FolderMaskId">The mask the folder wears.</param>
/// <param name="FolderName">The folder mask's display name, for the invariant's message.</param>
/// <param name="ChildMaskId">The mask being counted.</param>
/// <param name="ChildName">The counted mask's display name, for the invariant's message.</param>
/// <param name="Max">The most a folder may hold. Soft-deleted children do not count — consistent with sibling
/// name uniqueness, and the reason the check belongs in <c>SaveChanges</c>: a RESTORE is a save too, so
/// restoring a mailbox alongside a replacement is refused at the same point rather than needing its own rule.</param>
public sealed record ChildCardinalityRule(
    Guid FolderMaskId, string FolderName, Guid ChildMaskId, string ChildName, int Max);

/// <summary>A typed folder and the masks it admits (#562 slice 5, set-valued for #564's notebook sections).</summary>
/// <param name="FolderMaskId">The mask the folder wears.</param>
/// <param name="FolderName">The folder mask's display name, for the invariant's message.</param>
/// <param name="Admits">Each mask a child may wear, with its display name for the message.</param>
public sealed record TypedFolderRule(Guid FolderMaskId, string FolderName, IReadOnlyList<(Guid MaskId, string Name)> Admits)
{
    /// <summary>The admitted masks, listed for a human — "Section or Note"; a folder declaring none says so.</summary>
    public string AdmittedNames => Admits.Count == 0 ? "what the archive places there itself" : string.Join(" or ", Admits.Select(a => a.Name));
}
