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

    public sealed record ArchivedVersion(Guid Id, Guid DocumentId, int Number, string? DocumentDate, DateTimeOffset FiledAt, string CreatedBy, string Sha256, string FileExtension,
        string? ContentType = null);

    public sealed record ArchivedField(string Name, string DataType, IReadOnlyList<string> Values);

    /// <summary>A notification a cache kept (exports carry none): what the notifications page lists offline.</summary>
    public sealed record ArchivedNotification(Guid Id, string Type, string Title, string Body, Guid? DocumentId, DateTimeOffset CreatedAt, bool IsRead);

    public sealed record ArchivedMessage(Guid Id, Guid DocumentId, Guid? ParentMessageId, string Body, string Author, DateTimeOffset CreatedAt,
        IReadOnlyList<(Guid UserId, string DisplayName)> Mentions);

    private readonly Dictionary<Guid, ArchivedDocument> _documents;
    private readonly ILookup<Guid?, ArchivedDocument> _children;
    private readonly ILookup<Guid, ArchivedVersion> _versions;
    private readonly Dictionary<Guid, IReadOnlyList<ArchivedField>> _fields;
    private readonly Dictionary<Guid, string> _maskNames;
    private readonly ILookup<Guid, ArchivedMessage> _chat;
    private readonly IReadOnlyList<ArchivedNotification>? _notifications;

    private OfflineArchive(IArchiveStorage storage, IReadOnlyDictionary<string, byte[]?> files)
    {
        Storage = storage;
        JsonElement? Json(string path) => files[path] is { } bytes ? JsonDocument.Parse(bytes).RootElement.Clone() : null;
        IEnumerable<JsonElement> Lines(string path) => LinesOf(files[path]);
        var manifest = Json("manifest.json") ?? throw new OfflineArchiveException("The archive has no manifest.json.");
        // An export names its root; a cache (SimplArchiveMobile #5) spans repositories and names none.
        var hasRoot = manifest.TryGetProperty("root", out var root) && root.ValueKind == JsonValueKind.Object;
        Name = (hasRoot && root.TryGetProperty("name", out var n) ? n.GetString() : null)
            ?? (manifest.TryGetProperty("name", out var cacheName) ? cacheName.GetString() : null) ?? string.Empty;
        RootId = hasRoot ? root.GetProperty("documentId").GetGuid() : null;
        ExportedAt = manifest.TryGetProperty("exportedAt", out var at) && at.TryGetDateTimeOffset(out var when) ? when : null;
        PersonalRoot = manifest.TryGetProperty("personalRepositoryId", out var personal) && personal.ValueKind == JsonValueKind.String ? personal.GetGuid() : null;

        var people = People(Json("principals/principals.json"));
        string Who(JsonElement row) =>
            (row.TryGetProperty("createdByUserId", out var u) && u.ValueKind == JsonValueKind.String && people.TryGetValue(u.GetGuid(), out var user) ? user : null)
            ?? (row.TryGetProperty("createdByServiceAccountId", out var s) && s.ValueKind == JsonValueKind.String && people.TryGetValue(s.GetGuid(), out var account) ? account : null)
            ?? string.Empty;

        var masks = Json("masks/masks.json");
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

        _documents = Lines("tree/documents.jsonl").Select(d => new ArchivedDocument(
            d.GetProperty("id").GetGuid(),
            d.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetGuid() : null,
            d.GetProperty("name").GetString() ?? string.Empty,
            d.TryGetProperty("maskVersionId", out var mv) && mv.ValueKind == JsonValueKind.String ? mv.GetGuid() : null,
            Who(d),
            d.TryGetProperty("createdAt", out var c) && c.TryGetDateTimeOffset(out var created) ? created : default)).ToDictionary(d => d.Id);
        _children = _documents.Values.ToLookup(d => d.ParentId);

        _versions = Lines("tree/versions.jsonl").Select(v => new ArchivedVersion(
            v.GetProperty("id").GetGuid(),
            v.GetProperty("documentId").GetGuid(),
            v.GetProperty("versionNumber").GetInt32(),
            v.TryGetProperty("documentDate", out var dd) ? dd.GetString() : null,
            v.TryGetProperty("filedAt", out var f) && f.TryGetDateTimeOffset(out var filed) ? filed : default,
            Who(v),
            (v.TryGetProperty("blobRef", out var b) ? b.GetString() : null) ?? v.GetProperty("sha256").GetString() ?? string.Empty,
            v.TryGetProperty("fileExtension", out var e) ? e.GetString() ?? string.Empty : string.Empty,
            v.TryGetProperty("contentType", out var ct) && ct.ValueKind == JsonValueKind.String ? ct.GetString() : null)).ToLookup(v => v.DocumentId);

        _fields = Lines("tree/index-data.jsonl")
            .Select(x => (Document: x.GetProperty("documentId").GetGuid(), Field: x.GetProperty("fieldDefinitionId").GetGuid(),
                Value: x.TryGetProperty("value", out var value) ? value.GetString() ?? string.Empty : string.Empty,
                Ordinal: x.TryGetProperty("ordinal", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0))
            .Where(x => fieldDefinitions.ContainsKey(x.Field))
            .GroupBy(x => x.Document)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ArchivedField>)[.. g.GroupBy(x => x.Field)
                .OrderBy(fg => fieldDefinitions[fg.Key].Order)
                .Select(fg => new ArchivedField(fieldDefinitions[fg.Key].Name, fieldDefinitions[fg.Key].Type, [.. fg.OrderBy(x => x.Ordinal).Select(x => x.Value)]))]);

        // Cache-only (SimplArchiveMobile #5): absent from an export, and then no notifications link is offered at all.
        _notifications = files["tree/notifications.jsonl"] is null ? null : [.. Lines("tree/notifications.jsonl").Select(x => new ArchivedNotification(
            x.GetProperty("id").GetGuid(),
            x.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty,
            x.TryGetProperty("title", out var title) ? title.GetString() ?? string.Empty : string.Empty,
            x.TryGetProperty("body", out var nb) ? nb.GetString() ?? string.Empty : string.Empty,
            x.TryGetProperty("documentId", out var nd) && nd.ValueKind == JsonValueKind.String ? nd.GetGuid() : null,
            x.TryGetProperty("createdAt", out var nc) && nc.TryGetDateTimeOffset(out var noted) ? noted : default,
            x.TryGetProperty("isRead", out var r) && r.ValueKind == JsonValueKind.True))];

        _chat = Lines("tree/chat.jsonl").Select(m => new ArchivedMessage(
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

    /// <summary>The exported root; null for a cache, which spans repositories.</summary>
    public Guid? RootId { get; }

    /// <summary>
    /// What the archive lists as its repositories: an export's root, or for a cache every document whose folder is not
    /// in it (the repositories its documents were kept from).
    /// </summary>
    /// <summary>
    /// The user's personal space among the roots, when the archive knows it (a phone's cache records it): answered
    /// at <c>personalRepository</c> and left out of the repositories, as the server does (core ADR 0689), so the tree
    /// shows it first.
    /// </summary>
    public Guid? PersonalRoot { get; }

    public IEnumerable<ArchivedDocument> Roots() =>
        RootId is { } root
            ? _documents.TryGetValue(root, out var exported) ? [exported] : []
            : _documents.Values.Where(d => d.ParentId is not { } parent || !_documents.ContainsKey(parent)).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase);

    public IEnumerable<ArchivedDocument> AllDocuments() => _documents.Values;

    /// <summary>The kept notifications, newest first, or null when the archive keeps none (an export).</summary>
    public IReadOnlyList<ArchivedNotification>? Notifications => _notifications?.OrderByDescending(n => n.CreatedAt).ToList();

    public DateTimeOffset? ExportedAt { get; }

    /// <summary>The index files the model is built from, read once.</summary>
    private static readonly string[] IndexFiles =
        ["manifest.json", "principals/principals.json", "masks/masks.json", "tree/documents.jsonl", "tree/versions.jsonl", "tree/index-data.jsonl",
         "tree/chat.jsonl", "tree/notifications.jsonl"];

    public static async Task<OfflineArchive> OpenAsync(IArchiveStorage storage, CancellationToken cancellationToken = default)
    {
        var files = new Dictionary<string, byte[]?>();
        foreach (var path in IndexFiles)
        {
            files[path] = await storage.ReadAsync(path, cancellationToken);
        }

        return new OfflineArchive(storage, files);
    }

    public ArchivedDocument? Document(Guid id) => _documents.GetValueOrDefault(id);

    public IEnumerable<ArchivedDocument> Children(Guid id) => _children[id];

    /// <summary>Confirmed versions, newest first.</summary>
    public IReadOnlyList<ArchivedVersion> Versions(Guid id) => [.. _versions[id].OrderByDescending(v => v.Number)];

    public bool IsFolder(Guid id) => !_versions[id].Any();

    /// <summary>The type a blob was served as, where the cache recorded one (a strict tenant's envelope); else null.</summary>
    public string? ContentTypeOf(string sha256) =>
        _versions.SelectMany(g => g).FirstOrDefault(v => string.Equals(v.Sha256, sha256, StringComparison.OrdinalIgnoreCase))?.ContentType;

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

    private static IEnumerable<JsonElement> LinesOf(byte[]? bytes)
    {
        if (bytes is null)
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
