using System.Net.Http.Json;
using System.Text.Json;

namespace Alcifra.ApiClient;

/// <summary>
/// The masks area (#443's per-area shape): the tenant's mask catalogue, a mask's fields, and assigning one.
/// </summary>
/// <remarks>
/// Extracted from <c>DocumentsClient</c> rather than added to it. That file is on the standing-debt list
/// (#466) and may only get smaller, and masks are an area in their own right — every other area got its own
/// client in #443, so this is the shape the codebase already chose rather than a new one invented for a
/// ceiling.
/// </remarks>
public sealed class MasksClient
{
    private readonly ApiCore _core;

    public MasksClient(ApiCore core) => _core = core;

    /// <summary>A tenant mask option for the mask-change dropdown.</summary>
    /// <remarks>
    /// <c>SelfHref</c> is the address the catalogue advertised for this mask — reading its fields follows that
    /// rather than rebuilding <c>/api/masks/{id}</c> from the id beside it (ADR 0543/0555).
    /// </remarks>
    public sealed record MaskOptionInfo(Guid Id, string Name, string? SelfHref = null);

    /// <summary>A mask's field definition + type, for building the type-aware editor.</summary>
    // IsList defaults to false so the designer-preview rows below read as before; it is what tells the editor
    // to be a list rather than a single value (#703), and it comes from the SERVER rather than being inferred
    // from DataType — the two clients inferring a rule separately is how they came to disagree before (#671).
    public sealed record MaskFieldInfo(Guid Id, string Name, string DataType, bool IsRequired, bool IsList = false,
        bool ClassifierOwned = false,
        // Writing this field needs the manage-mail-routing right (#703) — the server marks it; combined with
        // whoami's flag it renders the editor read-only instead of offering an edit the save would 403.
        bool RequiresMailRouting = false,
        // The address of the values already filed under this field (#1127), when the server offers them —
        // absent means "no suggestions here", which is what the editor gates its autocomplete on (ADR 0543).
        string? ValuesHref = null,
        // A reference field naming the masks it accepts (#2214, ADR 1068): the address of its picker's candidates, and
        // the masks themselves; a picker offers to file a new item only where exactly one is named.
        string? TargetsHref = null,
        IReadOnlyList<AcceptedMask>? AcceptedMasks = null)
    {
        /// <summary>
        /// Where a module's vocabulary for this field's value answers <c>?q=</c> (core #2288), while that module is active:
        /// coded items with labels, completing the value and naming a stored code. Null for the ordinary field.
        /// </summary>
        /// <remarks>Init-only, not positional: a new parameter would change the constructor callers compiled against.</remarks>
        public string? VocabularyHref { get; init; }
    }

    /// <summary>One item of a module's field vocabulary: the stored <paramref name="Value"/>, and what it names.</summary>
    public sealed record VocabularyItem(string Value, string Label, string? Description = null);

    /// <summary>A mask a reference field accepts, as the server named it.</summary>
    public sealed record AcceptedMask(Guid Id, string Name);

    /// <summary>One document a reference field's picker offers, and the address its picture is reached through.</summary>
    /// <param name="DocumentHref">The target's own address, where only that is known (a value already stored, whose
    /// resolved target carries it): its versions are then read from it.</param>
    public sealed record ReferenceTarget(Guid Id, string Name, string? VersionsHref, string? DocumentHref = null);

    /// <summary>
    /// The documents a reference field may point at, narrowed by what was typed (#2214), from the field's own <c>targets</c>
    /// rel. Best-effort, as the value suggestions are: a list that cannot be read costs the picker its candidates, never the
    /// edit.
    /// </summary>
    public async Task<IReadOnlyList<ReferenceTarget>> FieldTargetsAsync(
        string targetsHref, string? typed, CancellationToken cancellationToken = default)
    {
        var href = typed is { Length: > 0 }
            ? $"{targetsHref}{(targetsHref.Contains('?') ? "&" : "?")}q={Uri.EscapeDataString(typed)}"
            : targetsHref;

        try
        {
            using var response = await _core.Http.GetAsync(href, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("targets", out var targets)
                ? [.. targets.EnumerateArray().Select(t => new ReferenceTarget(
                    t.GetProperty("id").GetGuid(),
                    t.GetProperty("name").GetString() ?? string.Empty,
                    ApiCore.ParseLinks(t)?.Href("versions")))]
                : [];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// A candidate's picture for the picker: its current version's preview, read through the one content funnel; null
    /// where it has none or the preview is not one an image can show.
    /// </summary>
    public async Task<byte[]?> TargetPictureAsync(ReferenceTarget target, CancellationToken cancellationToken = default)
    {
        try
        {
            var versionsHref = target.VersionsHref;
            if (versionsHref is null && target.DocumentHref is { } documentHref)
            {
                versionsHref = ApiCore.RelHref(await _core.Http.GetFromJsonAsync<JsonElement>(documentHref, cancellationToken), "versions");
            }

            if (versionsHref is null)
            {
                return null;
            }

            var preview = await new VersionsClient(_core).GetPreviewFromVersionsAsync(versionsHref, cancellationToken);
            return preview.PreviewUrl is { } url ? (await ApiCore.GetContentAsync(url, cancellationToken)).Bytes : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The values already filed under one index field, for an editor's type-ahead (#1127).
    /// </summary>
    /// <remarks>
    /// Followed from the field's own <c>values</c> rel, never composed (ADR 0543); the query is the client's
    /// own filter on an advertised href, which is following rather than composing (ADR 0557). Best-effort: a
    /// suggestion list that cannot be read costs the completion, never the edit.
    /// </remarks>
    public async Task<IReadOnlyList<string>> FieldValuesAsync(
        string valuesHref, string? typed, CancellationToken cancellationToken = default)
    {
        var href = typed is { Length: > 0 }
            ? $"{valuesHref}{(valuesHref.Contains('?') ? "&" : "?")}q={Uri.EscapeDataString(typed)}"
            : valuesHref;

        try
        {
            using var response = await _core.Http.GetAsync(href, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("values", out var values)
                ? [.. values.EnumerateArray().Select(v => v.GetString() ?? string.Empty)]
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// A module's vocabulary items matching what was typed (core #2288), from the field's own <c>vocabulary</c> rel, best
    /// first. Best-effort, as the values are: a vocabulary that cannot be read costs the completion, never the edit.
    /// </summary>
    public async Task<IReadOnlyList<VocabularyItem>> FieldVocabularyAsync(
        string vocabularyHref, string? typed, CancellationToken cancellationToken = default)
    {
        var href = $"{vocabularyHref}{(vocabularyHref.Contains('?') ? "&" : "?")}q={Uri.EscapeDataString(typed ?? string.Empty)}";

        try
        {
            using var response = await _core.Http.GetAsync(href, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                ? [.. items.EnumerateArray()
                    .Select(i => new VocabularyItem(
                        i.TryGetProperty("value", out var v) ? v.GetString() ?? string.Empty : string.Empty,
                        i.TryGetProperty("label", out var l) ? l.GetString() ?? string.Empty : string.Empty,
                        i.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null))
                    .Where(i => i.Value.Length > 0)]
                : [];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
        {
            return [];
        }
    }

    /// <summary>
    /// What a stored code names in the field's vocabulary — <c>P28A</c> → "Piper PA-28 Archer/Warrior/Cherokee" — or null
    /// when the vocabulary holds no item with exactly that value (free text is allowed, so that is ordinary).
    /// </summary>
    public async Task<VocabularyItem?> VocabularyLabelAsync(
        string vocabularyHref, string value, CancellationToken cancellationToken = default) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : (await FieldVocabularyAsync(vocabularyHref, value.Trim(), cancellationToken))
                .FirstOrDefault(i => string.Equals(i.Value, value.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The masks a user may actually CHOOSE for a document.</summary>
    /// <remarks>
    /// The SERVER decides which those are (ADR 0653): a folder mask types a folder, an extension-claimed mask
    /// is assigned by the classifier on upload, and a mask whose location is constrained belongs only in its
    /// admitting folder. Both clients used to derive this — differently — and offered masks the containment
    /// invariant then refused, so the user learned about it from a failed save (#580).
    /// </remarks>
    public async Task<IReadOnlyList<MaskOptionInfo>> GetMasksAsync(CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(
            await _core.RootHrefAsync("masks", cancellationToken), cancellationToken);

        var list = new List<MaskOptionInfo>();
        if (json.TryGetProperty("masks", out var masks))
        {
            foreach (var mask in masks.EnumerateArray().Where(Assignable))
            {
                list.Add(new MaskOptionInfo(
                    mask.GetProperty("id").GetGuid(),
                    mask.GetProperty("name").GetString() ?? string.Empty,
                    ApiCore.RelHref(mask, "self")));
            }
        }

        return list;
    }

    /// <summary>A mask's field definitions (+ types), for building the type-aware editors.</summary>
    public Task<IReadOnlyList<MaskFieldInfo>> GetMaskFieldsAsync(MaskOptionInfo mask, CancellationToken cancellationToken = default) =>
        GetMaskFieldsAsync(
            mask.SelfHref ?? throw new InvalidOperationException($"The mask '{mask.Name}' advertised no 'self' rel (ADR 0543/0555)."),
            cancellationToken);

    /// <summary>
    /// The same, from an advertised address: the <c>maskHref</c> an <c>admits</c> entry carries (#2217), which is how the
    /// New dialog learns which fields the mask requires before anything is created.
    /// </summary>
    public async Task<IReadOnlyList<MaskFieldInfo>> GetMaskFieldsAsync(string maskHref, CancellationToken cancellationToken = default)
    {
        var json = await _core.Http.GetFromJsonAsync<JsonElement>(maskHref, cancellationToken);

        var list = new List<MaskFieldInfo>();
        if (json.TryGetProperty("fields", out var fields))
        {
            foreach (var f in fields.EnumerateArray())
            {
                list.Add(new MaskFieldInfo(
                    f.GetProperty("id").GetGuid(),
                    f.GetProperty("name").GetString() ?? string.Empty,
                    f.TryGetProperty("dataType", out var dataType) ? dataType.GetString() ?? "Text" : "Text",
                    f.TryGetProperty("isRequired", out var required) && required.GetBoolean(),
                    f.TryGetProperty("isList", out var isList) && isList.GetBoolean(),
                    f.TryGetProperty("classifierOwned", out var owned) && owned.GetBoolean(),
                    f.TryGetProperty("requiresMailRouting", out var rmr) && rmr.GetBoolean(),
                    ApiCore.ParseLinks(f)?.Href("values"),
                    ApiCore.ParseLinks(f)?.Href("targets"),
                    f.TryGetProperty("acceptedMasks", out var accepted) && accepted.ValueKind == JsonValueKind.Array
                        ? [.. accepted.EnumerateArray().Select(a => new AcceptedMask(a.GetProperty("id").GetGuid(), a.GetProperty("name").GetString() ?? string.Empty))]
                        : [])
                {
                    VocabularyHref = ApiCore.ParseLinks(f)?.Href("vocabulary"),
                });
            }
        }

        return list;
    }

    /// <summary>Assigns (or changes) a document's mask. 400 REQUIRED_FIELD_MISSING surfaces as a friendly message.</summary>
    public async Task SetMaskAsync(string maskHref, Guid maskId, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.PutAsJsonAsync(maskHref, new { maskId }, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, "Could not assign the mask", cancellationToken);
    }

    /// <summary>Creates a tenant mask with no fields — freely assignable, since it types nothing in particular.</summary>
    /// <remarks>
    /// Needs CanManageMasks. A mask created this way is the ordinary case a tenant admin makes: not a folder
    /// mask, claimed by no extension, constrained to no parent — so it is exactly what "freely assignable"
    /// means (ADR 0653).
    /// </remarks>
    public async Task<MaskOptionInfo> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        var response = await _core.Http.PostAsJsonAsync(
            await _core.RootHrefAsync("masks", cancellationToken), new { name }, cancellationToken);
        await ApiCore.ThrowIfProblemAsync(response, $"Could not create the mask '{name}'", cancellationToken);

        var created = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return new MaskOptionInfo(
            created.GetProperty("id").GetGuid(),
            created.GetProperty("name").GetString() ?? name,
            ApiCore.RelHref(created, "self"));
    }

    /// <summary>Absent means assignable, so a server predating ADR 0653 fills the picker rather than emptying it.</summary>
    private static bool Assignable(JsonElement mask) =>
        !mask.TryGetProperty("isFreelyAssignable", out var value) || value.GetBoolean();
}
