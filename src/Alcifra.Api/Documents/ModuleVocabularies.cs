using Microsoft.Extensions.Logging;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.Api.Documents;

/// <summary>
/// Which masks name themselves, and which fields fill themselves, from a module's vocabulary, and where that vocabulary
/// lives (the name: ABI 0.21; a field's value: ABI 2.2, core #2288).
/// </summary>
/// <remarks>
/// <para>
/// A module declares <see cref="Alcifra.ModuleAbi.ModuleMaskSeed.NameVocabularyRel"/> on a mask whose NAME is an
/// identifier rather than a label — a weather folder called <c>LSZH</c>. This resolves that rel to the
/// address the module actually serves it at, so the create dialog can complete as the user types instead of
/// asking them to remember four letters.
/// </para>
/// <para>
/// Resolved PER TENANT and gated on activation, because the module's routes are (ADR 0737): an inactive
/// module answers 404, so advertising its href would be an affordance that fails on click — precisely what
/// ADR 0543 says a missing rel must instead mean. The masks themselves survive deactivation (ADR 0740), so
/// "the mask exists" is not the same question as "the module will answer", and only the second one may
/// decide whether this is emitted.
/// </para>
/// </remarks>
public static class ModuleVocabularies
{
    /// <summary>Where a mask's name completes from, and whether the name may hold several such values.</summary>
    /// <param name="Href">The address whose <c>?q=</c> answers the vocabulary.</param>
    /// <param name="IsMultiple">
    /// True when the NAME is a list — a NOTAM briefing's "LSZH LSAS EDGG". The client then completes the
    /// value being typed and leaves the others alone; false means the whole box is one value.
    /// </param>
    public sealed record Vocabulary(string Href, bool IsMultiple);

    /// <summary>
    /// Mask id → where that mask's name completes from, for the ambient tenant. Empty when no module is
    /// loaded, none is active, or none declares one — the ordinary case.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, Vocabulary>> NamesForTenantAsync(
        IReadOnlyList<ModuleLoader.LoadedModule> modules,
        AlcifraDbContext dbContext,
        ILogger logger,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var declaring = modules
            .Where(m => m.Module.Masks.Any(mask => !string.IsNullOrWhiteSpace(mask.NameVocabularyRel)))
            .ToList();
        if (declaring.Count == 0)
        {
            return new Dictionary<Guid, Vocabulary>();
        }

        var active = await ModuleActivationCheck.ActiveIdsAsync(
            dbContext, declaring.Select(m => m.Module.ModuleId).ToList(), now, cancellationToken);

        var map = new Dictionary<Guid, Vocabulary>();
        foreach (var loaded in declaring.Where(m => active.Contains(m.Module.ModuleId)))
        {
            foreach (var mask in loaded.Module.Masks.Where(m => !string.IsNullOrWhiteSpace(m.NameVocabularyRel)))
            {
                if (Resolve(loaded, mask.NameVocabularyRel!, $"mask {mask.Name} ({mask.MaskId}) as naming itself", "the create dialog", logger) is { } path)
                {
                    map[mask.MaskId] = new Vocabulary(path, mask.NameVocabularyIsMultiple);
                }
            }
        }

        return map;
    }

    /// <summary>
    /// (Mask id, field name) → the address a field's VALUE completes from (ABI 2.2, core #2288), for the ambient tenant.
    /// Empty when no active module declares one, the ordinary case.
    /// </summary>
    public static async Task<IReadOnlyDictionary<(Guid MaskId, string FieldName), string>> FieldsForTenantAsync(
        IReadOnlyList<ModuleLoader.LoadedModule> modules,
        AlcifraDbContext dbContext,
        ILogger logger,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var declaring = modules
            .Where(m => m.Module.Masks.Any(mask => mask.Fields.Any(f => !string.IsNullOrWhiteSpace(f.ValueVocabularyRel))))
            .ToList();
        if (declaring.Count == 0)
        {
            return new Dictionary<(Guid, string), string>();
        }

        var active = await ModuleActivationCheck.ActiveIdsAsync(
            dbContext, declaring.Select(m => m.Module.ModuleId).ToList(), now, cancellationToken);

        var map = new Dictionary<(Guid, string), string>();
        foreach (var loaded in declaring.Where(m => active.Contains(m.Module.ModuleId)))
        {
            foreach (var mask in loaded.Module.Masks)
            {
                foreach (var field in mask.Fields.Where(f => !string.IsNullOrWhiteSpace(f.ValueVocabularyRel)))
                {
                    if (Resolve(loaded, field.ValueVocabularyRel!, $"field {mask.Name}.{field.Name} as filling itself", "the field editor", logger) is { } path)
                    {
                        map[(mask.MaskId, field.Name)] = path;
                    }
                }
            }
        }

        return map;
    }

    // The one resolution both share: the declared rel to the module's own root link. A rel naming none is a Warning that
    // names both halves of the mismatch (ADR 0626): the affordance simply stays absent, which nobody would notice.
    private static string? Resolve(ModuleLoader.LoadedModule loaded, string rel, string what, string where, ILogger logger)
    {
        if (loaded.Module.RootLinks.FirstOrDefault(l => string.Equals(l.Rel, rel, StringComparison.Ordinal)) is { } link)
        {
            return link.Path;
        }

        logger.LogWarning(
            "Module {ModuleId} declares {What} from rel {Rel}, but contributes no root link with that rel — {Where} will offer NO "
            + "completion for it. Declared rels: {DeclaredRels}",
            loaded.Module.ModuleId, what, rel, where, string.Join(", ", loaded.Module.RootLinks.Select(l => l.Rel)));
        return null;
    }
}
