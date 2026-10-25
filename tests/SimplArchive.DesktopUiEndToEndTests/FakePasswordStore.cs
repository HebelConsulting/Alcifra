using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace SimplArchive.UiEndToEndTests;

/// <summary>
/// The password store's API as core ADR 0964 states it, in memory on a loopback port, for the desktop's Passwords tab
/// (ADR 0968): the root's <c>passwordStores</c>, My Passwords created on first use, folders and entries with their flags
/// and rels, the recipients, the re-seal list, versions filed by presigned-style upload under If-Match, rename, move and
/// delete under the row's ETag. The self-hosted app's tenants have no enrolled attested keys, and the question here is
/// the desktop, which the core's own end-to-end tests answer against the real controllers.
/// </summary>
/// <remarks>It holds SEALED bytes only, as the server does: whatever a test or the tab files is a CMS envelope.</remarks>
internal sealed class FakePasswordStore : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;
    private readonly object _gate = new();

    public FakePasswordStore(params X509Certificate2[] readers)
    {
        Readers = [.. readers];
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}";
        }

        _listener.Prefixes.Add($"{BaseUrl}/");
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }

    public Guid StoreId { get; } = Guid.NewGuid();

    /// <summary>Whether My Passwords exists yet: created by the <c>my-passwords</c> rel.</summary>
    public bool MineExists { get; set; }

    /// <summary>My Passwords as the server lists it, parent-qualified by the personal space's name (ADR 0619).</summary>
    public const string MineDisplayName = "Anna Berger / My Passwords";

    /// <summary>Another user's My Passwords, listed (an administrator's x-ray, ADR 0670) but never opened here.</summary>
    public (Guid Id, string DisplayName)? OthersMyPasswords { get; set; }

    public List<X509Certificate2> Readers { get; }

    /// <summary>Every folder and entry of My Passwords, by id.</summary>
    public Dictionary<Guid, Item> Items { get; } = [];

    /// <summary>The store says it needs re-sealing, and offers the re-seal to this caller.</summary>
    public bool NeedsReseal { get; set; }

    /// <summary>
    /// Why new entries are withheld (ADR 0982): when set, the folders send no <c>password-entries</c> rel and say this
    /// reason, and a POST of an entry is refused as the server refuses it; null offers entries.
    /// </summary>
    public string? NewEntryUnavailableReason { get; set; }

    /// <summary>Called before a version is finalized: a test plays "somebody else saved meanwhile".</summary>
    public Action<Item>? BeforeFinalize { get; set; }

    /// <summary>Every request, in order: method, path, If-Match.</summary>
    public List<(string Method, string Path, string? IfMatch)> Requests { get; } = [];

    public sealed class Item(Guid id, string name, Guid parentId, bool isFolder)
    {
        public Guid Id { get; } = id;

        public string Name { get; set; } = name;

        public Guid ParentId { get; set; } = parentId;

        public bool IsFolder { get; } = isFolder;

        public int Tag { get; set; } = 1;

        public byte[]? Content { get; set; }

        public byte[]? Pending { get; set; }

        public int Versions { get; set; }

        public int? LastCommentKind { get; set; }

        public bool? LastPasswordChanged { get; set; }

        public bool Deleted { get; set; }

        public string ETag => $"\"{Tag}\"";
    }

    /// <summary>An entry already there, its content as given (sealed by the test).</summary>
    public Item AddEntry(string name, byte[] sealedContent, Guid? parent = null)
    {
        lock (_gate)
        {
            MineExists = true;
            var entry = new Item(Guid.NewGuid(), name, parent ?? StoreId, isFolder: false) { Content = sealedContent, Versions = 1 };
            Items[entry.Id] = entry;
            return entry;
        }
    }

    public Item AddFolder(string name, Guid? parent = null)
    {
        lock (_gate)
        {
            MineExists = true;
            var folder = new Item(Guid.NewGuid(), name, parent ?? StoreId, isFolder: true);
            Items[folder.Id] = folder;
            return folder;
        }
    }

    public Item Entry(string name)
    {
        lock (_gate)
        {
            return Items.Values.Single(i => !i.IsFolder && !i.Deleted && i.Name == name);
        }
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }

            _ = Task.Run(() => AnswerAsync(context));
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        byte[] body;
        using (var read = new MemoryStream())
        {
            await context.Request.InputStream.CopyToAsync(read);
            body = read.ToArray();
        }

        var (method, path, ifMatch) = (context.Request.HttpMethod, context.Request.Url!.AbsolutePath.Trim('/').Split('/'), context.Request.Headers["If-Match"]);
        int status;
        object? json;
        byte[]? raw = null;
        try
        {
            lock (_gate)
            {
                Requests.Add((method, context.Request.Url!.AbsolutePath, ifMatch));
                (status, json, raw) = Route(method, path, body, ifMatch);
            }
        }
        catch (Exception e)
        {
            (status, json) = (500, new { detail = e.Message });
        }

        var bytes = raw ?? (json is null ? [] : JsonSerializer.SerializeToUtf8Bytes(json));
        context.Response.StatusCode = status;
        context.Response.ContentType = raw is null ? "application/json" : "application/octet-stream";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private (int, object?, byte[]?) Route(string method, string[] path, byte[] body, string? ifMatch)
    {
        switch (method, path)
        {
            case ("GET", ["api"]):
                return (200, new { links = new[] { Link("passwordStores", "/api/password-stores") } }, null);
            case ("GET", ["api", "password-stores"]):
                return (200, new
                {
                    stores = new[]
                    {
                        MineExists ? (StoreId, MineDisplayName, true) : ((Guid, string, bool)?)null,
                        OthersMyPasswords is { } other ? (other.Id, other.DisplayName, false) : null,
                    }.OfType<(Guid Id, string DisplayName, bool IsPersonal)>().Select(s => new
                    {
                        id = s.Id,
                        name = "My Passwords",
                        displayName = s.DisplayName,
                        isPersonal = s.IsPersonal,
                        links = new[] { Link("self", $"/api/password-folders/{s.Id}") },
                    }),
                    links = MineExists ? Array.Empty<object>() : [Link("my-passwords", "/api/password-stores/mine", "POST")],
                }, null);
            case ("POST", ["api", "password-stores", "mine"]):
                MineExists = true;
                return (200, Folder(StoreId), null);
            case ("GET", ["api", "password-folders", var id]):
                return (200, Folder(Guid.Parse(id)), null);
            case ("POST", ["api", "password-folders", _, "entries"]) when NewEntryUnavailableReason is not null:
                return (403, new { errorCode = "PASSWORD_ENTRY_ENROLMENT_REQUIRED" }, null);
            case ("POST", ["api", "password-folders", var id, "entries" or "folders"]):
                var name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!;
                var parent = Guid.Parse(id);
                if (Live().Any(i => i.ParentId == parent && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return (409, new { errorCode = "DOCUMENT_NAME_TAKEN" }, null);
                }

                var made = new Item(Guid.NewGuid(), name, parent, isFolder: path[^1] == "folders");
                Items[made.Id] = made;
                return (201, made.IsFolder ? Folder(made.Id) : EntryJson(made), null);
            case ("GET", ["api", "password-stores", _, "recipients"]):
                return (200, new
                {
                    certificates = Readers.Select(c => new { userId = Guid.NewGuid(), displayName = c.Subject, certificatePem = c.ExportCertificatePem() }),
                    readersWithoutCertificate = Array.Empty<object>(),
                }, null);
            case ("GET", ["api", "password-stores", _, "reseal"]):
                return (200, new { entries = Live().Where(e => !e.IsFolder && e.Content is not null).OrderBy(e => e.Name).Select(EntryJson) }, null);
            case ("GET", ["api", "password-entries", var id]):
                return (200, EntryJson(Items[Guid.Parse(id)]), null);
            case ("GET", ["blobs", var id]):
                return (200, null, Items[Guid.Parse(id)].Content!);
            case ("POST", ["api", "documents", var id, "versions"]):
                var entry = Items[Guid.Parse(id)];
                return (201, new { uploadUrl = $"{BaseUrl}/uploads/{entry.Id}", links = new[] { Link("self", $"/api/documents/{entry.Id}/versions/pending", "PUT") } }, null);
            case ("PUT", ["uploads", var id]):
                Items[Guid.Parse(id)].Pending = body;
                return (200, null, null);
            case ("PUT", ["api", "documents", var id, "versions", "pending"]):
                return Finalize(Items[Guid.Parse(id)], ifMatch, body);
            case ("PUT", ["api", "documents", var id]):
                return Tagged(Items[Guid.Parse(id)], ifMatch, item => item.Name = JsonDocument.Parse(body).RootElement.GetProperty("name").GetString()!);
            case ("PUT", ["api", "documents", var id, "parent"]):
                return Tagged(Items[Guid.Parse(id)], ifMatch, item => item.ParentId = JsonDocument.Parse(body).RootElement.GetProperty("parentId").GetGuid());
            case ("DELETE", ["api", "documents", var id]):
                return Tagged(Items[Guid.Parse(id)], ifMatch, item => item.Deleted = true);
            default:
                return (404, new { title = "Not here" }, null);
        }
    }

    private IEnumerable<Item> Live() => Items.Values.Where(i => !i.Deleted);

    private (int, object?, byte[]?) Tagged(Item item, string? ifMatch, Action<Item> change)
    {
        if (ifMatch != item.ETag)
        {
            return (ifMatch is null ? 428 : 412, new { errorCode = ifMatch is null ? "IF_MATCH_REQUIRED" : "ETAG_MISMATCH" }, null);
        }

        change(item);
        item.Tag++;
        return (204, null, null);
    }

    private (int, object?, byte[]?) Finalize(Item entry, string? ifMatch, byte[] body)
    {
        BeforeFinalize?.Invoke(entry);
        if (entry.Content is not null && ifMatch != entry.ETag)
        {
            return (ifMatch is null ? 428 : 412, new { errorCode = ifMatch is null ? "IF_MATCH_REQUIRED" : "ETAG_MISMATCH" }, null);
        }

        var finalize = JsonDocument.Parse(body).RootElement;
        (entry.Content, entry.Pending) = (entry.Pending, null);
        entry.Versions++;
        entry.Tag++;
        entry.LastCommentKind = finalize.TryGetProperty("commentKind", out var k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : null;
        entry.LastPasswordChanged = finalize.TryGetProperty("passwordChanged", out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;
        return (200, new { id = entry.Id }, null);
    }

    private object Folder(Guid id)
    {
        var isStore = id == StoreId;
        var links = new List<object>
        {
            Link("self", $"/api/password-folders/{id}"),
            Link("password-store-recipients", $"/api/password-stores/{StoreId}/recipients"),
            Link("password-folders", $"/api/password-folders/{id}/folders", "POST"),
        };
        if (NewEntryUnavailableReason is null)
        {
            links.Add(Link("password-entries", $"/api/password-folders/{id}/entries", "POST"));
        }

        if (!isStore)
        {
            links.Add(Link("store", $"/api/password-folders/{StoreId}"));
        }

        if (NeedsReseal)
        {
            links.Add(Link("reseal", $"/api/password-stores/{StoreId}/reseal"));
        }

        return new
        {
            id,
            name = isStore ? "My Passwords" : Items[id].Name,
            displayName = isStore ? MineDisplayName : Items[id].Name,
            kind = isStore ? "store" : "folder",
            storeId = StoreId,
            isPersonal = true,
            eTag = isStore ? "\"1\"" : Items[id].ETag,
            needsReseal = NeedsReseal,
            urgentReseal = false,
            readerCertificateCount = Readers.Count,
            newEntryUnavailableReason = NewEntryUnavailableReason,
            items = Live().Where(i => i.ParentId == id).OrderBy(i => i.Name).Select(i => new
            {
                id = i.Id,
                name = i.Name,
                kind = i.IsFolder ? "folder" : "entry",
                eTag = i.ETag,
                canEdit = true,
                canRename = true,
                canDelete = true,
                canMove = true,
                links = new[]
                {
                    Link("self", i.IsFolder ? $"/api/password-folders/{i.Id}" : $"/api/password-entries/{i.Id}"),
                    Link("document", $"/api/documents/{i.Id}"),
                    Link("move", $"/api/documents/{i.Id}/parent", "PUT"),
                },
            }),
            links,
        };
    }

    private object EntryJson(Item e)
    {
        var links = new List<object>
        {
            Link("self", $"/api/password-entries/{e.Id}"),
            Link("document", $"/api/documents/{e.Id}"),
            Link("password-store-recipients", $"/api/password-stores/{StoreId}/recipients"),
            Link("versions", $"/api/documents/{e.Id}/versions", "POST"),
            Link("move", $"/api/documents/{e.Id}/parent", "PUT"),
        };
        if (e.Content is not null)
        {
            links.Add(Link("content", $"{BaseUrl}/blobs/{e.Id}"));
        }

        return new
        {
            id = e.Id,
            name = e.Name,
            storeId = StoreId,
            parentId = e.ParentId,
            versionNumber = e.Versions == 0 ? (int?)null : e.Versions,
            eTag = e.ETag,
            needsReseal = NeedsReseal,
            exposedSince = (string?)null,
            canEdit = true,
            canRename = true,
            canDelete = true,
            canMove = true,
            links,
        };
    }

    private static object Link(string rel, string href, string method = "GET") => new { rel, href, method };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        _listener.Close();
        try
        {
            await _serving;
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
