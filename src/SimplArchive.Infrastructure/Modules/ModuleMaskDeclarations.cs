using System.Text.RegularExpressions;
using SimplArchive.Application.Abstractions;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// The core's view of what loaded modules declare on their masks (ABI 1.18, ADR 0937), built once from the modules.
/// </summary>
/// <remarks>
/// <b>A content segment is validated here, at startup,</b> because a bad one fails silently later: a segment spelled
/// like a year (<c>2026</c>) would mix a module's content into the archive's year folders, and one named like a core area
/// (<c>users</c>, <c>audit-worm</c>) would mix it into that. A module declaring one is refused loudly instead.
/// </remarks>
public sealed partial class ModuleMaskDeclarations : IModuleMaskDeclarations
{
    /// <summary>The first path segments under <c>tenants/{tenant}/</c> the core itself uses (ObjectKeyPrefixes).</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "users", "groups", "mailboxes", "fs", "module-uploads", "audit-worm",
    };

    /// <summary>No module declares anything: what a host without modules, and a test constructing a service, gets.</summary>
    public static ModuleMaskDeclarations None { get; } = new([]);

    private readonly Dictionary<Guid, string> _segments = [];
    private readonly HashSet<string> _unrenderedSegments = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sealedSegments = new(StringComparer.Ordinal);

    public ModuleMaskDeclarations(IReadOnlyList<ModuleLoader.LoadedModule> modules)
    {
        var notSearched = new HashSet<Guid>();
        var notRetained = new HashSet<Guid>();
        var notHeld = new HashSet<Guid>();
        var notRendered = new HashSet<Guid>();
        var provisioned = new List<ProvisionedFolder>();
        var sealedMasks = new HashSet<Guid>();
        var unsealedSegments = new HashSet<string>(StringComparer.Ordinal);

        foreach (var loaded in modules)
        {
            foreach (var mask in loaded.Module.Masks)
            {
                if (mask.ContentSegment is { } segment)
                {
                    if (!Segment().IsMatch(segment) || Reserved.Contains(segment))
                    {
                        throw new InvalidOperationException(
                            $"Module '{loaded.Module.ModuleId}' declares the content segment '{segment}' for mask '{mask.Name}'. "
                            + "A segment is lowercase letters, digits and hyphens, starts with a letter (never a year), and is "
                            + $"none of the core's own areas ({string.Join(", ", Reserved.Order(StringComparer.Ordinal))}).");
                    }

                    _segments[mask.MaskId] = segment;
                    if (mask.Exemptions.HasFlag(ModuleMaskExemptions.Renditions))
                    {
                        _unrenderedSegments.Add(segment);
                    }

                    (mask.ContentIsSealed ? _sealedSegments : unsealedSegments).Add(segment);
                }

                if (mask.ContentIsSealed)
                {
                    if (mask.ContentSegment is null)
                    {
                        throw new InvalidOperationException(
                            $"Module '{loaded.Module.ModuleId}' declares the content of mask '{mask.Name}' sealed without a content "
                            + "segment of its own; the storage door knows an object only by its key.");
                    }

                    sealedMasks.Add(mask.MaskId);
                }

                Add(mask, ModuleMaskExemptions.Search, notSearched);
                Add(mask, ModuleMaskExemptions.Retention, notRetained);
                Add(mask, ModuleMaskExemptions.LegalHold, notHeld);
                Add(mask, ModuleMaskExemptions.Renditions, notRendered);

                if (mask.PersonalSpaceFolderName is { Length: > 0 } folderName)
                {
                    if (!mask.IsFolderMask)
                    {
                        throw new InvalidOperationException(
                            $"Module '{loaded.Module.ModuleId}' asks for '{mask.Name}' in every personal space, but it is not a folder mask.");
                    }

                    provisioned.Add(new ProvisionedFolder(loaded.Module.ModuleId, mask.MaskId, folderName));
                }
            }
        }

        // A segment is sealed only if EVERY mask storing there is: the door lets a sealed segment's bytes past the strict
        // tier, so one unsealed mask sharing it would put its plaintext behind a door that only checks the shape.
        if (_sealedSegments.Intersect(unsealedSegments).FirstOrDefault() is { } shared)
        {
            throw new InvalidOperationException(
                $"The content segment '{shared}' is declared by a mask whose content is sealed and by one whose content is not; "
                + "a sealed mask needs a segment of its own.");
        }

        (NotSearched, NotRetained, NotHeld, NotRendered, SealedContent, ProvisionedInPersonalSpace) =
            (notSearched, notRetained, notHeld, notRendered, sealedMasks, provisioned);
    }

    public IReadOnlySet<Guid> NotSearched { get; }

    public IReadOnlySet<Guid> NotRetained { get; }

    public IReadOnlySet<Guid> NotHeld { get; }

    public IReadOnlySet<Guid> NotRendered { get; }

    public IReadOnlySet<Guid> SealedContent { get; }

    public IReadOnlyList<ProvisionedFolder> ProvisionedInPersonalSpace { get; }

    public string? ContentSegmentFor(Guid? maskId) =>
        maskId is { } id && _segments.TryGetValue(id, out var segment) ? segment : null;

    public bool IsUnrenderedContent(string objectKey) => InSegment(objectKey, _unrenderedSegments);

    public bool IsSealedContent(string objectKey) => InSegment(objectKey, _sealedSegments);

    private static bool InSegment(string objectKey, HashSet<string> segments)
    {
        // tenants/{tenantId}/{segment}/…: the third segment is the one a module declared.
        var parts = objectKey.Split('/', 4);
        return parts.Length == 4 && parts[0] == "tenants" && segments.Contains(parts[2]);
    }

    private static void Add(ModuleMaskSeed mask, ModuleMaskExemptions flag, HashSet<Guid> into)
    {
        if (mask.Exemptions.HasFlag(flag))
        {
            into.Add(mask.MaskId);
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{1,39}$")]
    private static partial Regex Segment();
}
