using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Alcifra.Cli.Infrastructure;

namespace Alcifra.Cli.Commands;

/// <summary>
/// Files a local file as a document under a parent, with a mask and its index data, following rels the whole way
/// (ADR 1040). Re-runnable like <see cref="LicenceFiling"/>: a child of that name is reused, its mask and fields are
/// stated again, and its content is filed only when the bytes differ.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mask before the content.</b> A mask may keep its content in a segment of its own (a module's, ABI 1.18),
/// and the object key is chosen when the version is created, from the mask the document wears at that moment. So
/// the document is created bare, its detail saved with the mask and fields in ONE request (ADR 0794), and only
/// then is the version uploaded. Finalizing an upload reclassifies only a document still wearing the generic mask,
/// so the chosen one stays.
/// </para>
/// <para>
/// <b>Fields by name, as a person writes them.</b> The API addresses a field by its definition id; the mask's own
/// field list turns each <c>Name=Value</c> into one. A name used twice gives a list field two values. An unknown
/// name is refused before anything is written, and so is a mask no client may assign.
/// </para>
/// </remarks>
public static class DocumentFiling
{
    /// <summary>One <c>--field</c>: a field of the mask by its name, and one value for it.</summary>
    public sealed record FieldValue(string Name, string Value);

    /// <summary>Parses <c>Name=Value</c>; the first <c>=</c> separates, so a value may contain one.</summary>
    public static FieldValue ParseField(string text)
    {
        var at = text.IndexOf('=', StringComparison.Ordinal);
        return at <= 0
            ? throw new CliException($"'{text}' is not a field: write Name=Value.")
            : new FieldValue(text[..at].Trim(), text[(at + 1)..]);
    }

    public static async Task<(Guid DocumentId, LicenceFiling.Outcome Outcome)> FileAsync(
        AlcifraApi api,
        Hypermedia hypermedia,
        Guid parentId,
        string name,
        string fileExtension,
        byte[] content,
        string? mask,
        IReadOnlyList<FieldValue> fields,
        CancellationToken cancellationToken)
    {
        if (mask is null && fields.Count > 0)
        {
            throw new CliException("A --field needs the --mask that declares it.");
        }

        // Resolved first, so a misspelt mask or field writes nothing at all.
        var maskFields = mask is null ? null : await ResolveMaskAsync(api, hypermedia, mask, cancellationToken);
        List<object> groups = maskFields is null ? [] : FieldGroups(maskFields, fields);

        var parent = await LicenceFiling.ResolveAsync(api, hypermedia, parentId, cancellationToken);
        var childrenHref = Hypermedia.Href(LicenceFiling.Links(parent), "children", $"Document {parentId:D}");
        var existing = await LicenceFiling.FindChildAsync(api, childrenHref, name, cancellationToken);
        Guid documentId;
        if (existing is { } found)
        {
            documentId = found;
        }
        else
        {
            if (parent.TryGetProperty("canCreateChildren", out var permitted) && permitted.ValueKind == JsonValueKind.False)
            {
                throw new CliException($"Document {parentId:D} does not accept new children for this caller.");
            }

            var created = await api.PostAsync(childrenHref, new { name }, cancellationToken);
            documentId = created.TryGetProperty("id", out var id) && id.TryGetGuid(out var value)
                ? value
                : throw new CliException("Creating the document answered without an id.");
        }

        var document = LicenceFiling.Links(await LicenceFiling.ResolveAsync(api, hypermedia, documentId, cancellationToken));
        if (maskFields is { } chosen)
        {
            await SaveDetailAsync(api, Hypermedia.Href(document, "detail", $"Document {documentId:D}"), chosen.Id, groups, cancellationToken);
        }

        var versionsHref = Hypermedia.Href(document, "versions", $"Document {documentId:D}");
        if (existing is not null
            && await LicenceFiling.CurrentHashAsync(api, versionsHref, cancellationToken) == Convert.ToHexStringLower(SHA256.HashData(content)))
        {
            return (documentId, LicenceFiling.Outcome.Unchanged);
        }

        await LicenceFiling.FileVersionAsync(api, versionsHref, content, fileExtension, cancellationToken);
        return (documentId, existing is null ? LicenceFiling.Outcome.Filed : LicenceFiling.Outcome.NewVersion);
    }

    /// <summary>
    /// A folder under a parent, wearing a mask, and its fields (ADR 1046). Re-runnable: a child of that name is reused and
    /// its fields stated again. The mask is given at creation, the path a folder mask is made by.
    /// </summary>
    public static async Task<Guid> FolderAsync(
        AlcifraApi api, Hypermedia hypermedia, Guid parentId, string name, string mask, IReadOnlyList<FieldValue> fields, CancellationToken cancellationToken)
    {
        var maskFields = await ResolveMaskAsync(api, hypermedia, mask, cancellationToken, assigning: false);
        var groups = FieldGroups(maskFields, fields);

        var parent = await LicenceFiling.ResolveAsync(api, hypermedia, parentId, cancellationToken);
        var childrenHref = Hypermedia.Href(LicenceFiling.Links(parent), "children", $"Document {parentId:D}");
        Guid folderId;
        if (await LicenceFiling.FindChildAsync(api, childrenHref, name, cancellationToken) is { } found)
        {
            folderId = found;
        }
        else
        {
            var created = await api.PostAsync(childrenHref, new { name, maskId = maskFields.Id }, cancellationToken);
            folderId = created.TryGetProperty("id", out var id) && id.TryGetGuid(out var value)
                ? value
                : throw new CliException("Creating the folder answered without an id.");
        }

        if (groups.Count > 0)
        {
            var folder = LicenceFiling.Links(await LicenceFiling.ResolveAsync(api, hypermedia, folderId, cancellationToken));
            await SaveDetailAsync(api, Hypermedia.Href(folder, "detail", $"Document {folderId:D}"), maskFields.Id, groups, cancellationToken);
        }

        return folderId;
    }

    /// <summary>Places a reference to a document in a folder (ADR 1046); true when placed, false when it was there already.</summary>
    public static async Task<bool> PlaceReferenceAsync(
        AlcifraApi api, Hypermedia hypermedia, Guid targetId, Guid folderId, CancellationToken cancellationToken)
    {
        var folder = LicenceFiling.Links(await LicenceFiling.ResolveAsync(api, hypermedia, folderId, cancellationToken));
        var href = Hypermedia.Href(folder, "references", $"Document {folderId:D}");

        // Following, not composing (ADR 0557): a query on the advertised address.
        var listing = await api.GetAsync($"{href}?limit=200", cancellationToken);
        if (listing.GetProperty("references").EnumerateArray().Any(r => r.GetProperty("id").GetGuid() == targetId))
        {
            return false;
        }

        await api.PostAsync(href, new { targetId }, cancellationToken);
        return true;
    }

    /// <summary>
    /// A document's mask and its fields by name, with their stored values (#2258): what <c>alcifra document fields show</c>
    /// prints, and what <see cref="SetFieldsAsync"/> merges into.
    /// </summary>
    public sealed record StoredFields(string MaskName, IReadOnlyList<(string Name, IReadOnlyList<string> Values)> Fields);

    /// <summary>The fields of <paramref name="documentId"/> by name, read from its own <c>detail</c> and its mask.</summary>
    public static async Task<StoredFields> ShowFieldsAsync(AlcifraApi api, Hypermedia hypermedia, Guid documentId, CancellationToken cancellationToken)
    {
        var (stored, mask, _, _) = await ReadDetailAsync(api, hypermedia, documentId, cancellationToken);
        var names = mask.FieldIds.ToDictionary(f => f.Value, f => f.Key);
        var values = StoredGroups(stored);
        return new StoredFields(
            stored.GetProperty("maskName").GetString() ?? string.Empty,
            [.. names.OrderBy(n => n.Value, StringComparer.Ordinal)
                .Select(n => (n.Value, (IReadOnlyList<string>)(values.GetValueOrDefault(n.Key) ?? [])))]);
    }

    /// <summary>
    /// Replaces the named fields of an existing document and keeps every other one as stored (#2258): ONE request, the
    /// detail stated back with the <c>If-Match</c> of the read it was taken from (ADR 0794). A name given twice is a list
    /// field's values. An unknown name is refused before anything is written. A module's mask works like any other.
    /// </summary>
    public static async Task SetFieldsAsync(
        AlcifraApi api, Hypermedia hypermedia, Guid documentId, IReadOnlyList<FieldValue> fields, CancellationToken cancellationToken)
    {
        if (fields.Count == 0)
        {
            throw new CliException("Name at least one --field Name=Value to set.");
        }

        var (stored, mask, detailHref, etag) = await ReadDetailAsync(api, hypermedia, documentId, cancellationToken);
        var named = FieldGroups(mask, fields);
        var namedIds = fields.Select(f => mask.FieldIds[f.Name]).ToHashSet();
        var kept = StoredGroups(stored).Where(g => !namedIds.Contains(g.Key))
            .Select(g => (object)new { fieldDefinitionId = g.Key, values = g.Value });
        await PutDetailAsync(api, detailHref, stored, etag, mask.Id, [.. kept, .. named], cancellationToken);
    }

    private static async Task<(JsonElement Stored, MaskFields Mask, string DetailHref, string? ETag)> ReadDetailAsync(
        AlcifraApi api, Hypermedia hypermedia, Guid documentId, CancellationToken cancellationToken)
    {
        var document = LicenceFiling.Links(await LicenceFiling.ResolveAsync(api, hypermedia, documentId, cancellationToken));
        var detailHref = Hypermedia.Href(document, "detail", $"Document {documentId:D}");
        var (stored, etag) = await api.GetWithETagAsync(detailHref, cancellationToken);
        var maskId = stored.TryGetProperty("maskId", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetGuid()
            : throw new CliException($"Document {documentId:D} wears no mask, so it has no fields to set.");
        return (stored, await ResolveMaskAsync(api, hypermedia, maskId.ToString("D"), cancellationToken, assigning: false), detailHref, etag);
    }

    private static Dictionary<Guid, List<string>> StoredGroups(JsonElement stored) =>
        stored.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.Array
            ? fields.EnumerateArray().ToDictionary(
                f => f.GetProperty("fieldDefinitionId").GetGuid(),
                f => f.GetProperty("values").EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToList())
            : [];

    /// <summary>
    /// Moves a document, and the subtree under it, to the recycle bin (ADR 1078): a DELETE on its own address with the
    /// <c>If-Match</c> of the read that showed it, refused up front where its <c>canDelete</c> is false. Restorable; never a
    /// purge. Answers the name it deleted, for the log.
    /// </summary>
    public static async Task<string> DeleteAsync(AlcifraApi api, Hypermedia hypermedia, Guid documentId, CancellationToken cancellationToken)
    {
        var self = Hypermedia.Href(LicenceFiling.Links(await LicenceFiling.ResolveAsync(api, hypermedia, documentId, cancellationToken)), "self", $"Document {documentId:D}");
        var (document, etag) = await api.GetWithETagAsync(self, cancellationToken);
        if (!document.TryGetProperty("canDelete", out var permitted) || permitted.ValueKind != JsonValueKind.True)
        {
            throw new CliException($"Document {documentId:D} may not be deleted by this caller.");
        }

        await api.DeleteWithETagAsync(self, etag, cancellationToken);
        return document.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>One row of <see cref="ListAsync"/>.</summary>
    public sealed record ListedDocument(Guid Id, bool IsFolder, string Type, string Name);

    /// <summary>
    /// The repositories visible to the caller (the root's <c>repositories</c> rel), or the children of
    /// <paramref name="parentId"/> (its <c>children</c> rel), every page followed by its <c>next</c> link (ADR 1077).
    /// </summary>
    public static async Task<IReadOnlyList<ListedDocument>> ListAsync(
        AlcifraApi api, Hypermedia hypermedia, Guid? parentId, CancellationToken cancellationToken)
    {
        var (href, property) = parentId is { } id
            ? (Hypermedia.Href(LicenceFiling.Links(await LicenceFiling.ResolveAsync(api, hypermedia, id, cancellationToken)), "children", $"Document {id:D}"), "children")
            : (await hypermedia.RootHrefAsync("repositories", cancellationToken), "repositories");
        var rows = new List<ListedDocument>();
        // Following, not composing (ADR 0557): a query on the advertised address, then each page's own next link.
        for (string? page = parentId is null ? href : $"{href}?limit=200"; page is not null;)
        {
            var listing = await api.GetAsync(page, cancellationToken);
            if (listing.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array)
            {
                rows.AddRange(items.EnumerateArray().Select(r => new ListedDocument(
                    r.GetProperty("id").GetGuid(),
                    parentId is null || (r.TryGetProperty("isFolder", out var folder) && folder.ValueKind == JsonValueKind.True),
                    r.TryGetProperty("documentType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString()! : string.Empty,
                    r.GetProperty("name").GetString() ?? string.Empty)));
            }

            page = Hypermedia.LinksOf(listing).TryGetValue("next", out var next) ? next : null;
        }

        return rows;
    }

    /// <summary>A mask's id and its fields by name, found through the root's <c>masks</c> rel by name or id.</summary>
    internal sealed record MaskFields(Guid Id, IReadOnlyDictionary<string, Guid> FieldIds);

    private static async Task<MaskFields> ResolveMaskAsync(
        AlcifraApi api, Hypermedia hypermedia, string mask, CancellationToken cancellationToken, bool assigning = true)
    {
        var listing = await api.GetAsync(await hypermedia.RootHrefAsync("masks", cancellationToken), cancellationToken);
        var row = listing.GetProperty("masks").EnumerateArray().FirstOrDefault(m =>
            string.Equals(m.GetProperty("name").GetString(), mask, StringComparison.Ordinal)
            || (Guid.TryParse(mask, out var wanted) && m.GetProperty("id").GetGuid() == wanted));
        if (row.ValueKind != JsonValueKind.Object)
        {
            throw new CliException($"No mask named '{mask}' is visible to this caller.");
        }

        // The same question the clients' pickers ask (ADR 0653): a folder mask, a typed item or a sealed mask is
        // made by its own path, never assigned to a document whose content already exists.
        if (assigning && row.TryGetProperty("isFreelyAssignable", out var assignable) && assignable.ValueKind == JsonValueKind.False)
        {
            throw new CliException($"The mask '{mask}' cannot be assigned to a filed document.");
        }

        var detail = await api.GetAsync(Hypermedia.Href(Hypermedia.LinksOf(row), "self", $"Mask '{mask}'"), cancellationToken);
        var fieldIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var field in detail.GetProperty("fields").EnumerateArray())
        {
            fieldIds[field.GetProperty("name").GetString()!] = field.GetProperty("id").GetGuid();
        }

        return new MaskFields(row.GetProperty("id").GetGuid(), fieldIds);
    }

    /// <summary>The fields by id, a repeated name grouped into one list; an unknown name is refused.</summary>
    private static List<object> FieldGroups(MaskFields mask, IReadOnlyList<FieldValue> fields)
    {
        var groups = new List<object>();
        foreach (var group in fields.GroupBy(f => f.Name, StringComparer.Ordinal))
        {
            var id = mask.FieldIds.TryGetValue(group.Key, out var known)
                ? known
                : throw new CliException(
                    $"The mask has no field '{group.Key}'. Its fields: {string.Join(", ", mask.FieldIds.Keys.Order(StringComparer.Ordinal))}.");
            groups.Add(new { fieldDefinitionId = id, values = group.Select(f => f.Value).ToList() });
        }

        return groups;
    }

    /// <summary>
    /// The detail as ONE request (ADR 0794): the stored aspects echoed from the read that the <c>If-Match</c> belongs
    /// to, with the mask and the fields stated. A <c>PUT</c> states the whole value, so the fields given ARE the
    /// document's index data afterwards.
    /// </summary>
    private static async Task SaveDetailAsync(
        AlcifraApi api, string detailHref, Guid maskId, IReadOnlyList<object> groups, CancellationToken cancellationToken)
    {
        var (stored, etag) = await api.GetWithETagAsync(detailHref, cancellationToken);
        await PutDetailAsync(api, detailHref, stored, etag, maskId, groups, cancellationToken);
    }

    // The stored aspects echoed from the read the If-Match belongs to, with the mask and the fields stated.
    private static async Task PutDetailAsync(
        AlcifraApi api, string detailHref, JsonElement stored, string? etag, Guid maskId, IReadOnlyList<object> groups, CancellationToken cancellationToken)
    {
        string? Text(string property) =>
            stored.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        List<string> Texts(string property) =>
            stored.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
                ? [.. value.EnumerateArray().Select(v => v.GetString() ?? string.Empty)]
                : [];
        Guid? label = stored.TryGetProperty("sensitivityLabelId", out var labelValue) && labelValue.ValueKind == JsonValueKind.String
            ? Guid.Parse(labelValue.GetString()!, CultureInfo.InvariantCulture)
            : null;

        await api.PutWithETagAsync(detailHref, new
        {
            name = Text("name"),
            documentDate = Text("documentDate"),
            documentTime = Text("documentTime"),
            ocrLanguages = Texts("ocrLanguages"),
            sensitivityLabelId = label,
            tags = Texts("tags"),
            fields = groups,
            maskId,
            confirmDuplicateClaims = false,
        }, etag, cancellationToken);
    }
}
