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

    /// <summary>A mask's id and its fields by name, found through the root's <c>masks</c> rel by name or id.</summary>
    internal sealed record MaskFields(Guid Id, IReadOnlyDictionary<string, Guid> FieldIds);

    private static async Task<MaskFields> ResolveMaskAsync(
        AlcifraApi api, Hypermedia hypermedia, string mask, CancellationToken cancellationToken)
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
        if (row.TryGetProperty("isFreelyAssignable", out var assignable) && assignable.ValueKind == JsonValueKind.False)
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
