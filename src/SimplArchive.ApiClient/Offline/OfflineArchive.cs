using System.Text;
using System.Text.Json;

namespace SimplArchive.ApiClient.Offline;

/// <summary>
/// An archive in the export layout (core ADR 0347), read once into what the offline responder answers from: the
/// tree, each document's confirmed versions, its index values with their field names and types, and who made what.
/// </summary>
/// <remarks>
/// A document with versions is a document; one without is a folder (the layout carries no flag). Field types travel as
/// the domain enum's NUMBER and the API answers its NAME; the order is pinned by a unit test against the enum, since
/// this library may not reference the domain.
/// </remarks>
public sealed class OfflineArchive
{
    private static string[] FieldTypeNames => OfflineArchiveFieldTypes.Names;

    public sealed record ArchivedDocument(Guid Id, Guid? ParentId, string Name, Guid? MaskVersionId, string CreatedBy, DateTimeOffset CreatedAt);

    public sealed record ArchivedVersion(Guid Id, Guid DocumentId, int Number, string? DocumentDate, DateTimeOffset FiledAt, string CreatedBy, string Sha256, string FileExtension);

    public sealed record ArchivedField(string Name, string DataType, IReadOnlyList<string> Values);

    public sealed record ArchivedMessage(Guid Id, Guid DocumentId, Guid? ParentMessageId, string Body, string Author, DateTimeOffset CreatedAt,
        IReadOnlyList<(Guid UserId, string DisplayName)> Mentions);

    private readonly Dictionary<Guid, ArchivedDocument> _documents;
    private readonly ILookup<Guid?, ArchivedDocument> _children;
    private readonly ILookup<Guid, ArchivedVersion> _versions;
    private readonly Dictionary<Guid, IReadOnlyList<ArchivedField>> _fields;
    private readonly Dictionary<Guid, string> _maskNames;
    private readonly ILookup<Guid, ArchivedMessage> _chat;

    private OfflineArchive(IArchiveStorage storage)
    {
        Storage = storage;
        var manifest = Json(storage, "manifest.json") ?? throw new OfflineArchiveException("The archive has no manifest.json.");
        Name = manifest.TryGetProperty("root", out var root) && root.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
        RootId = manifest.GetProperty("root").GetProperty("documentId").GetGuid();
        ExportedAt = manifest.TryGetProperty("exportedAt", out var at) && at.TryGetDateTimeOffset(out var when) ? when : null;

        var people = People(Json(storage, "principals/principals.json"));
        string Who(JsonElement row) =>
            (row.TryGetProperty("createdByUserId", out var u) && u.ValueKind == JsonValueKind.String && people.TryGetValue(u.GetGuid(), out var user) ? user : null)
            ?? (row.TryGetProperty("createdByServiceAccountId", out var s) && s.ValueKind == JsonValueKind.String && people.TryGetValue(s.GetGuid(), out var account) ? account : null)
            ?? string.Empty;

        var masks = Json(storage, "masks/masks.json");
        _maskNames = [];
        var fieldDefinitions = new Dictionary<Guid, (string Name, string Type, int Order)>();
        if (masks is { ValueKind: JsonValueKind.Array } maskList)
        {
            foreach (var mask in maskList.EnumerateArray())
            {
                var version = mask.GetProperty("version");
                _maskNames[version.GetProperty("maskVersionId").GetGuid()] = version.GetProperty("name").GetString() ?? string.Empty;
                var order = 0;
                foreach (var field in mask.GetProperty("fields").EnumerateArray())
                {
                    var type = field.GetProperty("dataType").GetInt32();
                    fieldDefinitions[field.GetProperty("fieldDefinitionId").GetGuid()] =
                        (field.GetProperty("name").GetString() ?? string.Empty, type >= 0 && type < FieldTypeNames.Length ? FieldTypeNames[type] : "Text", order++);
                }
            }
        }

        _documents = Lines(storage, "tree/documents.jsonl").Select(d => new ArchivedDocument(
            d.GetProperty("id").GetGuid(),
            d.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : null,
            d.GetProperty("name").GetString() ?? string.Empty,
            d.TryGetProperty("maskVersionId", out var mv) && mv.ValueKind == JsonValueKind.String ? mv.GetGuid() : null,
            Who(d),
            d.TryGetProperty("createdAt", out var c) && c.TryGetDateTimeOffset(out var created) ? created : default)).ToDictionary(d => d.Id);
        _children = _documents.Values.ToLookup(d => d.ParentId);

        _versions = Lines(storage, "tree/versions.jsonl").Select(v => new ArchivedVersion(
            v.GetProperty("id").GetGuid(),
            v.GetProperty("documentId").GetGuid(),
            v.GetProperty("versionNumber").GetInt32(),
            v.TryGetProperty("documentDate", out var dd) ? dd.GetString() : null,
            v.TryGetProperty("filedAt", out var f) && f.TryGetDateTimeOffset(out var filed) ? filed : default,
            Who(v),
            (v.TryGetProperty("blobRef", out var b) ? b.GetString() : null) ?? v.GetProperty("sha256").GetString() ?? string.Empty,
            v.TryGetProperty("fileExtension", out var e) ? e.GetString() ?? string.Empty : string.Empty)).ToLookup(v => v.DocumentId);

        _fields = Lines(storage, "tree/index-data.jsonl")
            .Select(x => (Document: x.GetProperty("documentId").GetGuid(), Field: x.GetProperty("fieldDefinitionId").GetGuid(),
                Value: x.TryGetProperty("value", out var value) ? value.GetString() ?? string.Empty : string.Empty,
                Ordinal: x.TryGetProperty("ordinal", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0))
            .Where(x => fieldDefinitions.ContainsKey(x.Field))
            .GroupBy(x => x.Document)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ArchivedField>)[.. g.GroupBy(x => x.Field)
                .OrderBy(fg => fieldDefinitions[fg.Key].Order)
                .Select(fg => new ArchivedField(fieldDefinitions[fg.Key].Name, fieldDefinitions[fg.Key].Type, [.. fg.OrderBy(x => x.Ordinal).Select(x => x.Value)]))]);

        _chat = Lines(storage, "tree/chat.jsonl").Select(m => new ArchivedMessage(
            m.GetProperty("id").GetGuid(),
            m.GetProperty("documentId").GetGuid(),
            m.TryGetProperty("parentMessageId", out var pm) && pm.ValueKind == JsonValueKind.String ? pm.GetGuid() : null,
            m.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
            Who(m),
            m.TryGetProperty("createdAt", out var ca) && ca.TryGetDateTimeOffset(out var posted) ? posted : default,
            // The body's "@[id]" tokens, named from the archive's people (ADR 0347 carries the ids).
            m.TryGetProperty("mentions", out var mentioned) && mentioned.ValueKind == JsonValueKind.Array
                ? [.. mentioned.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String && x.TryGetGuid(out _))
                    .Select(x => (x.GetGuid(), people.GetValueOrDefault(x.GetGuid()) ?? string.Empty))]
                : [])).ToLookup(m => m.DocumentId);
    }

    public IArchiveStorage Storage { get; }

    /// <summary>The exported root's name, for the "Offline: …" banner.</summary>
    public string Name { get; }

    public Guid RootId { get; }

    public DateTimeOffset? ExportedAt { get; }

    public static OfflineArchive Open(IArchiveStorage storage) => new(storage);

    public ArchivedDocument? Document(Guid id) => _documents.GetValueOrDefault(id);

    public IEnumerable<ArchivedDocument> Children(Guid id) => _children[id];

    /// <summary>Confirmed versions, newest first.</summary>
    public IReadOnlyList<ArchivedVersion> Versions(Guid id) => [.. _versions[id].OrderByDescending(v => v.Number)];

    public bool IsFolder(Guid id) => !_versions[id].Any();

    public IReadOnlyList<ArchivedField> Fields(Guid id) => _fields.GetValueOrDefault(id) ?? [];

    public IReadOnlyList<ArchivedMessage> Chat(Guid id) => [.. _chat[id].OrderBy(m => m.CreatedAt)];

    public string MaskName(ArchivedDocument document) =>
        document.MaskVersionId is { } mv ? _maskNames.GetValueOrDefault(mv) ?? string.Empty : string.Empty;

    private static Dictionary<Guid, string> People(JsonElement? principals)
    {
        var people = new Dictionary<Guid, string>();
        if (principals is not { } p)
        {
            return people;
        }

        if (p.TryGetProperty("users", out var users))
        {
            foreach (var u in users.EnumerateArray())
            {
                people[u.GetProperty("id").GetGuid()] = u.TryGetProperty("displayName", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            }
        }

        if (p.TryGetProperty("serviceAccounts", out var accounts))
        {
            foreach (var s in accounts.EnumerateArray())
            {
                people[s.GetProperty("id").GetGuid()] = s.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            }
        }

        return people;
    }

    private static JsonElement? Json(IArchiveStorage storage, string path) =>
        storage.Read(path) is { } bytes ? JsonDocument.Parse(bytes).RootElement.Clone() : null;

    private static IEnumerable<JsonElement> Lines(IArchiveStorage storage, string path)
    {
        if (storage.Read(path) is not { } bytes)
        {
            yield break;
        }

        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            if (line.Trim() is { Length: > 0 } text)
            {
                yield return JsonDocument.Parse(text).RootElement.Clone();
            }
        }
    }
}

/// <summary>An archive the responder cannot read: no manifest, or a blob that is not what its name says.</summary>
public sealed class OfflineArchiveException(string message) : Exception(message);

/// <summary>
/// The domain's <c>FieldDataType</c> names in declaration order: the archive stores the number, the API answers the
/// name, and this library may not reference the domain. Pinned against the enum by OfflineResponderTests.
/// </summary>
public static class OfflineArchiveFieldTypes
{
    public static readonly string[] Names =
        ["Text", "Number", "Date", "DateTime", "Boolean", "SingleSelect", "MultiSelect", "EmailAddress", "Url", "DocumentReference"];
}
