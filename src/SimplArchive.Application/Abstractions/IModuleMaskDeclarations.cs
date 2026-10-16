namespace SimplArchive.Application.Abstractions;

/// <summary>
/// What loaded modules declare about their masks (ABI 1.18, ADR 0937), for the core services that must honour it: where
/// such a document's content is stored, what it is left out of, and which folders the core provisions for every user.
/// </summary>
/// <remarks>
/// Read from the loaded modules at runtime rather than stored on the mask row, so declaring it needs no schema change and
/// a module's new release changes it on the next start. Process-wide, like the module's masks themselves: a declaration
/// holds wherever such a document exists, whether or not the module is active for that tenant today.
/// </remarks>
public interface IModuleMaskDeclarations
{
    /// <summary>The content segment documents of <paramref name="maskId"/> store their bytes under; null for the archive layout.</summary>
    string? ContentSegmentFor(Guid? maskId);

    /// <summary>Masks left out of full-text search.</summary>
    IReadOnlySet<Guid> NotSearched { get; }

    /// <summary>Masks left out of retention schedules and disposition review.</summary>
    IReadOnlySet<Guid> NotRetained { get; }

    /// <summary>Masks that cannot be held, and which a hold above them does not freeze.</summary>
    IReadOnlySet<Guid> NotHeld { get; }

    /// <summary>Masks that get no preview rendition and no text recognition.</summary>
    IReadOnlySet<Guid> NotRendered { get; }

    /// <summary>Whether an object key lies in a content segment whose masks get no renditions: what a preview, keyed by
    /// object only, can ask.</summary>
    bool IsUnrenderedContent(string objectKey);

    /// <summary>The folder masks provisioned in every user's personal space, with the module that declared each.</summary>
    IReadOnlyList<ProvisionedFolder> ProvisionedInPersonalSpace { get; }
}

/// <summary>A folder a module asks the core to provision in each user's personal space (ABI 1.18).</summary>
public sealed record ProvisionedFolder(string ModuleId, Guid MaskId, string Name);
