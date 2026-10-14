using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SimplArchive.ApiClient.Offline;

/// <summary>
/// Merges offline exports (core ADR 0927) into a device's offline copy, kept as a directory in the export layout
/// (ADR 0347): the copy is made of what the server's exporter wrote, row for row, so it cannot disagree with an
/// export of the same documents (#1759 slice 2; the owner's test). Replaces the device-side writer as the producer of
/// the copy's content; notifications and the personal-space marker stay the copy's own additions.
/// </summary>
/// <remarks>
/// <para>
/// <b>An export is a full snapshot of its anchor's subtree.</b> Within it, everything the export carries replaces what
/// the copy held, and what the copy held there that the export no longer has goes (deleted, moved away, or no longer
/// readable by this user). The folder chain above the anchor only refreshes those folders' own rows. Masks, people and
/// labels are joined by id.
/// </para>
/// <para>
/// <b>Rows are carried as the exporter wrote them</b>, as JSON, never re-modelled, so a field the copy does not
/// understand still survives. Every file goes through <c>seal</c> on the way in and <c>open</c> on the way out, as
/// the responder reads it.
/// </para>
/// </remarks>
public sealed class ArchiveMerger
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // The per-document row files, by the property naming their document.
    private static readonly (string Path, string DocumentKey)[] DocumentRows =
    [
        ("tree/versions.jsonl", "documentId"),
        ("tree/index-data.jsonl", "documentId"),
        ("tree/chat.jsonl", "documentId"),
        ("tree/annotations.jsonl", "documentId"),
        ("tree/references.jsonl", "parentFolderId"),
    ];

    private readonly string _directory;
    private readonly string _name;
    private readonly Func<string, byte[], byte[]> _seal;
    private readonly Dictionary<Guid, JsonObject> _documents = [];
    private readonly Dictionary<string, List<JsonObject>> _rows = DocumentRows.ToDictionary(r => r.Path, _ => new List<JsonObject>());
    private readonly Dictionary<Guid, JsonObject> _maskVersions = [];
    private readonly Dictionary<string, Dictionary<Guid, JsonObject>> _principals = new()
    {
        ["users"] = [],
        ["serviceAccounts"] = [],
        ["groups"] = [],
    };
    private readonly Dictionary<string, JsonObject> _labels = [];
    private readonly Dictionary<string, JsonObject> _memberships = [];
    private List<JsonObject> _notifications = [];
    private bool _hasNotifications;

    private ArchiveMerger(string directory, string name, Func<string, byte[], byte[]> seal) =>
        (_directory, _name, _seal) = (directory, name, seal);

    /// <summary>The user's personal space among the roots, recorded in the copy's manifest (core ADR 0689).</summary>
    public Guid? PersonalRoot { get; set; }

    /// <summary>A merger over the copy in <paramref name="directory"/>, reading back what earlier merges kept.</summary>
    /// <param name="name">What the copy is called in the "Offline: …" banner.</param>
    /// <param name="seal">Encrypts one file's bytes; the identity for a plain copy.</param>
    /// <param name="open">Decrypts one file on reading back.</param>
    public static async Task<ArchiveMerger> OpenAsync(string directory, string name, Func<string, byte[], byte[]> seal,
        Func<string, byte[], CancellationToken, Task<byte[]>> open, CancellationToken cancellationToken = default)
    {
        var merger = new ArchiveMerger(directory, name, seal);
        Directory.CreateDirectory(Path.Combine(directory, "blobs"));
        if (File.Exists(Path.Combine(directory, "manifest.json")))
        {
            await merger.LoadAsync(new DirectoryArchiveStorage(directory, open), cancellationToken);
        }

        return merger;
    }

    /// <summary>Whether the copy holds this document.</summary>
    public bool Contains(Guid id) => _documents.ContainsKey(id);

    /// <summary>
    /// Merges one offline export: the anchor's subtree replaces what the copy held of it; the chain above refreshes
    /// those folders' rows; masks, people and labels are joined; new blobs are copied in, sealed.
    /// </summary>
    /// <returns>The ids of the documents in the export's subtree, for the copy's bookkeeping.</returns>
    public async Task<IReadOnlySet<Guid>> MergeAsync(IArchiveStorage export, CancellationToken cancellationToken = default)
    {
        var manifest = JsonNode.Parse(await export.ReadAsync("manifest.json", cancellationToken)
            ?? throw new OfflineArchiveException("The export has no manifest.json."))!.AsObject();
        var anchor = manifest["root"]?["documentId"]?.GetValue<Guid>() ?? throw new OfflineArchiveException("The export names no root.");

        var exported = (await LinesAsync(export, "tree/documents.jsonl", cancellationToken)).ToDictionary(Id);
        var subtree = Subtree(anchor, exported.Values);

        // What the copy held beneath the anchor and the export no longer has: deleted, moved away, or not readable.
        foreach (var gone in Subtree(anchor, _documents.Values).Where(id => !subtree.Contains(id)).ToList())
        {
            Remove(gone);
        }

        foreach (var (id, row) in exported)
        {
            _documents[id] = row;
        }

        foreach (var (path, key) in DocumentRows)
        {
            _rows[path].RemoveAll(r => subtree.Contains(Guid(r, key)));
            _rows[path].AddRange((await LinesAsync(export, path, cancellationToken)).Where(r => subtree.Contains(Guid(r, key))));
        }

        foreach (var mask in Array(JsonNode.Parse(await export.ReadAsync("masks/masks.json", cancellationToken) ?? "[]"u8.ToArray())))
        {
            _maskVersions[Guid(mask["version"]!.AsObject(), "maskVersionId")] = mask;
        }

        if (await export.ReadAsync("principals/principals.json", cancellationToken) is { } principals)
        {
            var people = JsonNode.Parse(principals)!.AsObject();
            foreach (var (kind, byId) in _principals)
            {
                foreach (var person in Array(people[kind]))
                {
                    byId[Id(person)] = person;
                }
            }

            foreach (var membership in Array(people["memberships"]))
            {
                _memberships[membership.ToJsonString()] = membership;
            }
        }

        foreach (var label in Array(JsonNode.Parse(await export.ReadAsync("labels/labels.json", cancellationToken) ?? "[]"u8.ToArray())))
        {
            _labels[label["name"]!.GetValue<string>()] = label;
        }

        foreach (var sha in _rows["tree/versions.jsonl"].Where(v => subtree.Contains(Guid(v, "documentId"))).Select(v => v["blobRef"]?.GetValue<string>()).OfType<string>().Distinct())
        {
            if (!File.Exists(Path.Combine(_directory, "blobs", sha)) && await export.ReadAsync($"blobs/{sha}", cancellationToken) is { } blob)
            {
                Write($"blobs/{sha}", blob);
            }
        }

        return subtree;
    }

    /// <summary>Drops a document and what belongs to it (a newer version removed, evicted, no longer readable).</summary>
    public void Remove(Guid id)
    {
        _documents.Remove(id);
        foreach (var (path, key) in DocumentRows)
        {
            _rows[path].RemoveAll(r => Guid(r, key) == id);
        }
    }

    /// <summary>The notification list as last read online (the copy's own file; an export carries none).</summary>
    public void PutNotifications(IEnumerable<CachedNotification> notifications)
    {
        _notifications = [.. notifications.Select(n => JsonSerializer.SerializeToNode(new
        {
            id = n.Id, type = n.Type, title = n.Title, body = n.Body, documentId = n.DocumentId, createdAt = n.CreatedAt, isRead = n.IsRead,
        }, Web)!.AsObject())];
        _hasNotifications = true;
    }

    /// <summary>Writes the manifest and every index file, sealed. Blobs were written as they arrived.</summary>
    public void Flush()
    {
        WriteLines("tree/documents.jsonl", _documents.Values);
        foreach (var (path, _) in DocumentRows)
        {
            WriteLines(path, _rows[path]);
        }

        if (_hasNotifications)
        {
            WriteLines("tree/notifications.jsonl", _notifications);
        }

        Write("masks/masks.json", Bytes(new JsonArray([.. _maskVersions.Values.Select(m => m.DeepClone())])));
        Write("labels/labels.json", Bytes(new JsonArray([.. _labels.Values.Select(l => l.DeepClone())])));
        var principals = new JsonObject(_principals.Select(p =>
            KeyValuePair.Create<string, JsonNode?>(p.Key, new JsonArray([.. p.Value.Values.Select(v => v.DeepClone())]))));
        principals["memberships"] = new JsonArray([.. _memberships.Values.Select(m => m.DeepClone())]);
        Write("principals/principals.json", Bytes(principals));
        Write("manifest.json", JsonSerializer.SerializeToUtf8Bytes(
            new { formatVersion = 2, kind = "cache", name = _name, exportedAt = DateTimeOffset.UtcNow, personalRepositoryId = PersonalRoot }, Web));
    }

    /// <summary>Deletes blobs no kept version refers to; returns the bytes freed.</summary>
    public long Prune()
    {
        var kept = _rows["tree/versions.jsonl"].Select(v => v["blobRef"]?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        long freed = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_directory, "blobs")))
        {
            if (!kept.Contains(Path.GetFileName(file)))
            {
                freed += new FileInfo(file).Length;
                File.Delete(file);
            }
        }

        return freed;
    }

    private async Task LoadAsync(IArchiveStorage copy, CancellationToken cancellationToken)
    {
        foreach (var row in await LinesAsync(copy, "tree/documents.jsonl", cancellationToken))
        {
            _documents[Id(row)] = row;
        }

        foreach (var (path, _) in DocumentRows)
        {
            _rows[path].AddRange(await LinesAsync(copy, path, cancellationToken));
        }

        if (await copy.ReadAsync("tree/notifications.jsonl", cancellationToken) is { } notifications)
        {
            _notifications = [.. Parse(notifications)];
            _hasNotifications = true;
        }

        foreach (var mask in Array(JsonNode.Parse(await copy.ReadAsync("masks/masks.json", cancellationToken) ?? "[]"u8.ToArray())))
        {
            _maskVersions[Guid(mask["version"]!.AsObject(), "maskVersionId")] = mask;
        }

        if (await copy.ReadAsync("principals/principals.json", cancellationToken) is { } principals)
        {
            var people = JsonNode.Parse(principals)!.AsObject();
            foreach (var (kind, byId) in _principals)
            {
                foreach (var person in Array(people[kind]))
                {
                    byId[Id(person)] = person;
                }
            }

            foreach (var membership in Array(people["memberships"]))
            {
                _memberships[membership.ToJsonString()] = membership;
            }
        }

        foreach (var label in Array(JsonNode.Parse(await copy.ReadAsync("labels/labels.json", cancellationToken) ?? "[]"u8.ToArray())))
        {
            _labels[label["name"]!.GetValue<string>()] = label;
        }

        var manifest = JsonNode.Parse((await copy.ReadAsync("manifest.json", cancellationToken))!)!.AsObject();
        PersonalRoot = manifest["personalRepositoryId"] is JsonValue p && p.TryGetValue<Guid>(out var personal) ? personal : null;
    }

    // The anchor and everything beneath it, among the given rows.
    private static HashSet<Guid> Subtree(Guid anchor, IEnumerable<JsonObject> documents)
    {
        var children = documents.Where(d => d["parentId"] is not null).GroupBy(d => Guid(d, "parentId")).ToDictionary(g => g.Key, g => g.Select(Id).ToList());
        var subtree = new HashSet<Guid>();
        var pending = new Stack<Guid>([anchor]);
        while (pending.TryPop(out var id))
        {
            if (subtree.Add(id))
            {
                foreach (var child in children.GetValueOrDefault(id, []))
                {
                    pending.Push(child);
                }
            }
        }

        return subtree;
    }

    private static async Task<List<JsonObject>> LinesAsync(IArchiveStorage storage, string path, CancellationToken cancellationToken) =>
        await storage.ReadAsync(path, cancellationToken) is { } bytes ? [.. Parse(bytes)] : [];

    private static IEnumerable<JsonObject> Parse(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonNode.Parse(line)!.AsObject());

    private static IEnumerable<JsonObject> Array(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>().Select(o => o.DeepClone().AsObject()) : [];

    private static Guid Id(JsonObject row) => Guid(row, "id");

    private static Guid Guid(JsonObject row, string property) => row[property] is JsonValue value && value.TryGetValue<Guid>(out var id) ? id : System.Guid.Empty;

    private static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(Web));

    private void WriteLines(string path, IEnumerable<JsonObject> rows) =>
        Write(path, Encoding.UTF8.GetBytes(string.Join('\n', rows.Select(r => r.ToJsonString(Web)))));

    private void Write(string path, byte[] bytes)
    {
        var target = Path.Combine(_directory, path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = $"{target}.tmp";
        File.WriteAllBytes(temporary, _seal(path, bytes));
        File.Move(temporary, target, overwrite: true);
    }
}
