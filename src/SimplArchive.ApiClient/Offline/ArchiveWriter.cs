using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SimplArchive.ApiClient.Offline;

/// <summary>A document as a client saw it online, to be kept in a cache.</summary>
/// <param name="ParentId">Its folder, or null for a repository; kept so the cache shows the same tree.</param>
/// <param name="DocumentType">The mask's name, as the listing showed it.</param>
public sealed record CachedDocument(Guid Id, Guid? ParentId, string Name, string DocumentType, string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>One confirmed version, with the bytes that were read for it.</summary>
/// <param name="ContentType">The type the server served it as, when that says more than the extension: a strict tenant's
/// content is a CMS envelope (<c>application/pkcs7-mime</c>), kept as it arrived and opened with the card when read.</param>
public sealed record CachedVersion(Guid Id, int Number, string? DocumentDate, DateTimeOffset FiledAt, string CreatedBy, string FileExtension, byte[] Content,
    string? ContentType = null);

/// <summary>One index field as the server answered it: its name, its type's name, and its values in order.</summary>
public sealed record CachedField(string Name, string DataType, IReadOnlyList<string> Values);

/// <summary>A notification as the server listed it, to be read offline.</summary>
public sealed record CachedNotification(Guid Id, string Type, string Title, string Body, Guid? DocumentId, DateTimeOffset CreatedAt, bool IsRead);

/// <summary>One chat message, with the people its "@[id]" tokens name (their real ids: the body refers to them).</summary>
public sealed record CachedMessage(Guid Id, Guid? ParentMessageId, string Body, string Author, DateTimeOffset CreatedAt,
    IReadOnlyList<(Guid UserId, string DisplayName)> Mentions);

/// <summary>
/// Writes a cache in the export layout (core ADR 0347), so the offline responder reads it like an export (ADR 0924;
/// SimplArchiveMobile #5): a directory, each file passed through <c>seal</c> on the way in and <c>open</c> on the way
/// back, added to and refreshed piece by piece. The index files are kept in memory and rewritten whole by
/// <see cref="Flush"/>; a blob is written once, named by its SHA-256, and never rewritten.
/// </summary>
/// <remarks>
/// <para>
/// A cache holds what a client saw, not the server's internals, so ids the layout needs and the API does not hand out
/// are DERIVED, deterministically from names: a mask version from its name, a field definition from mask and field
/// name, an author from their display name. A person a message mentions keeps their real id, because the message's
/// body refers to it.
/// </para>
/// <para>The manifest names no single root: a cache spans repositories, so the responder lists every document whose
/// folder is not in the cache as a repository.</para>
/// </remarks>
public sealed class ArchiveWriter
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _directory;
    private readonly Func<string, byte[], byte[]> _seal;
    private readonly string _name;
    private readonly Dictionary<Guid, CachedDocument> _documents = [];
    private readonly Dictionary<Guid, IReadOnlyList<(CachedVersion Version, string Sha256)>> _versions = [];
    private readonly Dictionary<Guid, IReadOnlyList<CachedField>> _fields = [];
    private readonly Dictionary<Guid, IReadOnlyList<CachedMessage>> _chat = [];
    private IReadOnlyList<CachedNotification>? _notifications;

    /// <summary>A writer over the cache in <paramref name="directory"/>, reading back what an earlier run kept.</summary>
    /// <param name="name">What the cache is called in the "Offline: …" banner.</param>
    /// <param name="seal">Encrypts one file's bytes; the identity for a plain archive.</param>
    /// <param name="open">Decrypts one INDEX file on reading back (blobs are never read back by the writer).</param>
    public static async Task<ArchiveWriter> OpenAsync(string directory, string name, Func<string, byte[], byte[]> seal,
        Func<string, byte[], CancellationToken, Task<byte[]>> open, CancellationToken cancellationToken = default)
    {
        var writer = new ArchiveWriter(directory, name, seal);
        if (File.Exists(Path.Combine(directory, "manifest.json")))
        {
            writer.Load(await OfflineArchive.OpenAsync(new DirectoryArchiveStorage(directory, open), cancellationToken));
        }

        return writer;
    }

    private ArchiveWriter(string directory, string name, Func<string, byte[], byte[]> seal)
    {
        (_directory, _name, _seal) = (directory, name, seal);
        foreach (var folder in new[] { "tree", "blobs", "masks", "principals" })
        {
            Directory.CreateDirectory(Path.Combine(directory, folder));
        }
    }

    /// <summary>Keeps or refreshes a document, with its confirmed versions' content, index data and chat.</summary>
    public void Put(CachedDocument document, IReadOnlyList<CachedVersion> versions, IReadOnlyList<CachedField> fields, IReadOnlyList<CachedMessage> chat)
    {
        _documents[document.Id] = document;
        _versions[document.Id] = [.. versions.Select(v => (v, PutBlob(v.Content)))];
        _fields[document.Id] = fields;
        _chat[document.Id] = chat;
    }

    /// <summary>Keeps a folder (or a repository) so the tree above a kept document stays intact.</summary>
    public void PutFolder(CachedDocument folder)
    {
        // A folder met through a document's folder chain arrives without its type; the type kept from a listing
        // (or the personal space's "User folder") stays rather than being erased by it.
        if (folder.DocumentType.Length == 0 && _documents.TryGetValue(folder.Id, out var known) && known.DocumentType.Length > 0)
        {
            folder = folder with { DocumentType = known.DocumentType };
        }

        _documents[folder.Id] = folder;
        _versions.Remove(folder.Id);
    }

    /// <summary>Forgets a document (a newer version exists, or the cache is full). Its blobs go at the next <see cref="Prune"/>.</summary>
    public void Remove(Guid id)
    {
        _documents.Remove(id);
        _versions.Remove(id);
        _fields.Remove(id);
        _chat.Remove(id);
    }

    public bool Contains(Guid id) => _documents.ContainsKey(id);

    /// <summary>Replaces the kept notification list (the newest ones the server listed).</summary>
    public void PutNotifications(IReadOnlyList<CachedNotification> notifications) => _notifications = notifications;

    /// <summary>Writes the manifest and the index files, sealed. Blobs were written as they arrived.</summary>
    public void Flush()
    {
        var people = new Dictionary<Guid, string>();
        Guid Person(string name)
        {
            var id = Derived($"person:{name}");
            people[id] = name;
            return id;
        }

        var masks = _documents.Values.Where(d => d.DocumentType.Length > 0).Select(d => d.DocumentType)
            .Concat(_fields.Keys.Where(_documents.ContainsKey).Select(id => _documents[id].DocumentType)).Distinct().ToList();
        var fieldsByMask = masks.ToDictionary(m => m, m => _fields.Where(f => _documents.TryGetValue(f.Key, out var d) && d.DocumentType == m)
            .SelectMany(f => f.Value).GroupBy(f => f.Name).Select(g => g.First()).ToList());

        Write("masks/masks.json", JsonSerializer.SerializeToUtf8Bytes(masks.Select(m => new
        {
            maskId = Derived($"mask:{m}"),
            version = new { maskVersionId = Derived($"mask:{m}"), name = m },
            fields = fieldsByMask[m].Select(f => new { fieldDefinitionId = Derived($"field:{m}:{f.Name}"), name = f.Name, dataType = TypeNumber(f.DataType) }),
        }), Web));

        Lines("tree/documents.jsonl", _documents.Values.Select(d => (object)new
        {
            id = d.Id,
            parentId = d.ParentId,
            name = d.Name,
            maskVersionId = d.DocumentType.Length > 0 ? Derived($"mask:{d.DocumentType}") : (Guid?)null,
            createdByUserId = Person(d.CreatedBy),
            createdAt = d.CreatedAt,
        }));
        Lines("tree/versions.jsonl", _versions.Where(v => _documents.ContainsKey(v.Key)).SelectMany(v => v.Value.Select(x => (object)new
        {
            id = x.Version.Id,
            documentId = v.Key,
            versionNumber = x.Version.Number,
            documentDate = x.Version.DocumentDate,
            filedAt = x.Version.FiledAt,
            createdByUserId = Person(x.Version.CreatedBy),
            sha256 = x.Sha256,
            blobRef = x.Sha256,
            fileExtension = x.Version.FileExtension,
            contentType = x.Version.ContentType,
        })));
        Lines("tree/index-data.jsonl", _fields.Where(f => _documents.ContainsKey(f.Key)).SelectMany(f => f.Value.SelectMany(field =>
            field.Values.Select((value, ordinal) => (object)new
            {
                documentId = f.Key,
                fieldDefinitionId = Derived($"field:{_documents[f.Key].DocumentType}:{field.Name}"),
                value,
                ordinal,
            }))));
        Lines("tree/chat.jsonl", _chat.Where(c => _documents.ContainsKey(c.Key)).SelectMany(c => c.Value.Select(m =>
        {
            foreach (var (userId, displayName) in m.Mentions)
            {
                people[userId] = displayName;
            }

            return (object)new
            {
                id = m.Id,
                documentId = c.Key,
                parentMessageId = m.ParentMessageId,
                body = m.Body,
                mentions = m.Mentions.Select(x => x.UserId),
                createdByUserId = Person(m.Author),
                createdAt = m.CreatedAt,
            };
        })));

        if (_notifications is { } notifications)
        {
            Lines("tree/notifications.jsonl", notifications.Select(n => (object)new
            {
                id = n.Id,
                type = n.Type,
                title = n.Title,
                body = n.Body,
                documentId = n.DocumentId,
                createdAt = n.CreatedAt,
                isRead = n.IsRead,
            }));
        }

        Write("principals/principals.json", JsonSerializer.SerializeToUtf8Bytes(new { users = people.Select(p => new { id = p.Key, displayName = p.Value }) }, Web));
        Write("manifest.json", JsonSerializer.SerializeToUtf8Bytes(
            new { formatVersion = 2, kind = "cache", name = _name, exportedAt = DateTimeOffset.UtcNow, personalRepositoryId = PersonalRoot }, Web));
    }

    /// <summary>Deletes the blobs no kept version names any more; returns the bytes freed.</summary>
    public long Prune()
    {
        var kept = _versions.Where(v => _documents.ContainsKey(v.Key)).SelectMany(v => v.Value.Select(x => x.Sha256)).ToHashSet(StringComparer.Ordinal);
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

    /// <summary>The cache's size on the device, every file included.</summary>
    public long SizeBytes() => Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    private string PutBlob(byte[] content)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!File.Exists(Path.Combine(_directory, "blobs", sha)))
        {
            Write($"blobs/{sha}", content);
        }

        return sha;
    }

    private void Lines(string path, IEnumerable<object> rows) =>
        Write(path, Encoding.UTF8.GetBytes(string.Join('\n', rows.Select(r => JsonSerializer.Serialize(r, Web)))));

    // Written beside and moved into place: a reader never sees half a file.
    private void Write(string path, byte[] bytes)
    {
        var target = Path.Combine(_directory, path);
        var temporary = $"{target}.tmp";
        File.WriteAllBytes(temporary, _seal(path, bytes));
        File.Move(temporary, target, overwrite: true);
    }

    /// <summary>Which root is the user's personal space, so the cache shows it first as the tree does.</summary>
    public Guid? PersonalRoot { get; set; }

    private void Load(OfflineArchive archive)
    {
        PersonalRoot = archive.PersonalRoot;
        foreach (var d in archive.AllDocuments())
        {
            var versions = archive.Versions(d.Id);
            _documents[d.Id] = new CachedDocument(d.Id, d.ParentId, d.Name, archive.MaskName(d), d.CreatedBy, d.CreatedAt);
            if (versions.Count > 0)
            {
                // The content stays where it is: only the rows are reloaded, with their blob names.
                _versions[d.Id] = [.. versions.Select(v => (new CachedVersion(v.Id, v.Number, v.DocumentDate, v.FiledAt, v.CreatedBy, v.FileExtension, [], v.ContentType), v.Sha256))];
            }

            _fields[d.Id] = [.. archive.Fields(d.Id).Select(f => new CachedField(f.Name, f.DataType, f.Values))];
            _chat[d.Id] = [.. archive.Chat(d.Id).Select(m => new CachedMessage(m.Id, m.ParentMessageId, m.Body, m.Author, m.CreatedAt, m.Mentions))];
        }

        _notifications = archive.Notifications?.Select(n => new CachedNotification(n.Id, n.Type, n.Title, n.Body, n.DocumentId, n.CreatedAt, n.IsRead)).ToList();
    }

    private static int TypeNumber(string name) => Math.Max(0, Array.IndexOf(OfflineArchiveFieldTypes.Names, name));

    /// <summary>A stable id from a name: the same name gives the same id in every run.</summary>
    internal static Guid Derived(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 16));
}
