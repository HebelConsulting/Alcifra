using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SimplArchive.Messenger.Client;

namespace Alcifra.UiEndToEndTests;

/// <summary>
/// An archive offering the Messenger on a loopback port, in memory, for the desktop's Messages tab (ADR 0967): the
/// root, whoami, conversations with their members and per-epoch keys, messages, files through a presigned-style store,
/// the people search with certificates, and the member and epoch acts — the wire the Messenger client speaks, by its
/// links. The self-hosted app runs no Messenger, and the question here is the desktop, not the module, which its own
/// repository tests against its real controllers.
/// </summary>
/// <remarks>
/// It holds SEALED bytes only, as the server does: conversation keys wrapped to each member's certificates, message
/// bodies sealed by the client, files as CMS envelopes. A test that needs another member to have written something
/// seals it itself with the conversation key it made (<see cref="Seal"/>).
/// </remarks>
internal sealed class FakeMessenger : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Chat> _chats = [];
    private readonly Dictionary<Guid, byte[]> _store = [];        // upload id → the sealed bytes PUT there
    private readonly Dictionary<Guid, byte[]> _documents = [];    // filed document → its sealed bytes

    public FakeMessenger(Guid me, string myName, X509Certificate2 myCertificate)
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add($"{BaseUrl}/");
        _listener.Start();
        Me = me;
        AddPerson(me, myName, myCertificate);
        _ = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }

    public Guid Me { get; }

    /// <summary>Everyone with an enrolled key: their name, e-mail and certificate.</summary>
    public Dictionary<Guid, (string Name, string Email, X509Certificate2 Certificate)> People { get; } = [];

    /// <summary>Every request, in order: method, path and body.</summary>
    public List<(string Method, string Path, string Body)> Requests { get; } = [];

    /// <summary>Holds every message send until released: a send on its way, for the one-at-a-time gate.</summary>
    public TaskCompletionSource? HoldSends { get; set; }

    /// <summary>Offers the "is typing" link on conversations (the user's preference on).</summary>
    public bool OffersTyping { get; set; }

    public void AddPerson(Guid id, string name, X509Certificate2 certificate) =>
        People[id] = (name, $"{name.ToLowerInvariant()}@example.com", certificate);

    /// <summary>A conversation made here, its key (returned) wrapped to every member's certificate.</summary>
    public (Guid Id, byte[] Key) AddConversation(string name, bool isDirect, IReadOnlyList<Guid> members, Guid? admin = null, bool filesStayInApp = false)
    {
        var key = ConversationKeys.NewKey();
        var chat = new Chat(Guid.NewGuid(), name, isDirect) { FilesStayInApp = filesStayInApp };
        foreach (var member in members)
        {
            chat.Members.Add(new Membership(member, member == (admin ?? Me)));
        }

        chat.Keys[1] = members.ToDictionary(m => m, m => Wrap(key, m));
        lock (_gate)
        {
            _chats[chat.Id] = chat;
        }

        return (chat.Id, key);
    }

    /// <summary>Another member writes: their text sealed with the conversation key, as their client would.</summary>
    public Guid Seal(Guid conversation, byte[] key, Guid sender, string text, int epoch = 1)
    {
        lock (_gate)
        {
            var chat = _chats[conversation];
            var id = Guid.NewGuid();
            var sealedText = MessageFormat.Seal(key, new MessageContext(chat.Id, epoch, id, sender), Encoding.UTF8.GetBytes(text));
            chat.Messages.Add(new Post(id, epoch, sender, sealedText, DateTimeOffset.UtcNow.AddSeconds(chat.Messages.Count)));
            return id;
        }
    }

    public Chat ChatOf(Guid id)
    {
        lock (_gate)
        {
            return _chats[id];
        }
    }

    public IReadOnlyList<Chat> Chats
    {
        get
        {
            lock (_gate)
            {
                return [.. _chats.Values];
            }
        }
    }

    public byte[] Document(Guid id)
    {
        lock (_gate)
        {
            return _documents[id];
        }
    }

    public int RequestCount(string method, string pathPart)
    {
        lock (Requests)
        {
            return Requests.Count(r => r.Method == method && r.Path.Contains(pathPart, StringComparison.Ordinal));
        }
    }

    private byte[] Wrap(byte[] key, Guid user)
    {
        using var publicOnly = X509CertificateLoader.LoadCertificate(People[user].Certificate.RawData);
        return ConversationKeys.SealTo(key, [publicOnly]);
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

            _ = Task.Run(() => AnswerAsync(context));
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await context.Request.InputStream.CopyToAsync(buffer);
            body = buffer.ToArray();
        }

        var (method, path) = (context.Request.HttpMethod, context.Request.Url!.AbsolutePath);
        lock (Requests)
        {
            Requests.Add((method, path, path.StartsWith("/store/", StringComparison.Ordinal) ? string.Empty : Encoding.UTF8.GetString(body)));
        }

        if (method == "POST" && path.EndsWith("/messages", StringComparison.Ordinal) && HoldSends is { } hold)
        {
            await hold.Task;
        }

        int status;
        object? json;
        byte[]? raw = null;
        try
        {
            lock (_gate)
            {
                (status, json, raw) = Route(method, path, context.Request.QueryString["term"], body, context.Request.Headers["If-Match"]);
            }
        }
        catch (Refused refused)
        {
            (status, json) = (refused.Status, new { title = refused.Message, detail = refused.Message, errorCode = refused.Code });
        }

        context.Response.StatusCode = status;
        var bytes = raw ?? (json is null ? [] : JsonSerializer.SerializeToUtf8Bytes(json, Json));
        context.Response.ContentType = raw is null ? "application/json" : "application/octet-stream";
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private (int, object?, byte[]?) Route(string method, string path, string? term, byte[] body, string? ifMatch)
    {
        var segments = path.Trim('/').Split('/');
        var request = body.Length > 0 && !path.StartsWith("/store/", StringComparison.Ordinal) ? JsonNode.Parse(body) : null;
        switch (method, segments)
        {
            case ("GET", ["api"]):
                return (200, new
                {
                    links = new[]
                    {
                        L("messenger:conversations", "/m/conversations"), L("messenger:people", "/m/people"), L("whoami", "/api/diagnostics/whoami"),
                    },
                }, null);
            case ("GET", ["api", "diagnostics", "whoami"]):
                return (200, new { userId = Me, userName = People[Me].Name }, null);
            case ("GET", ["m", "conversations"]):
                return (200, new
                {
                    items = _chats.Values.Where(c => c.Members.Any(m => m.User == Me)).Select(ConversationJson).ToList(),
                    links = new[] { L("open-direct", "/m/direct", "POST"), L("create-group", "/m/groups", "POST") },
                }, null);
            case ("GET", ["m", "people"]):
                return (200, new
                {
                    items = People.Where(p => string.IsNullOrEmpty(term) || p.Value.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                            || p.Value.Email.Contains(term, StringComparison.OrdinalIgnoreCase))
                        .Select(p => new { id = p.Key, displayName = p.Value.Name, email = p.Value.Email, links = new[] { L("certificates", $"/m/certificates/{p.Key}") } }),
                }, null);
            case ("GET", ["m", "certificates", var user]):
                return (200, new { certificatesPem = new[] { People[Guid.Parse(user)].Certificate.ExportCertificatePem() } }, null);
            case ("POST", ["m", "direct"]):
                return (200, ConversationJson(Create(string.Empty, isDirect: true, request!["keys"]!.AsArray())), null);
            case ("POST", ["m", "groups"]):
                return (200, ConversationJson(Create(request!["name"]!.GetValue<string>(), isDirect: false, request["keys"]!.AsArray())), null);
            case ("GET", ["m", "c", var id]):
                return (200, ConversationJson(Mine(id)), null);
            case ("GET", ["m", "c", var id, "messages"]):
                return (200, PageJson(Mine(id)), null);
            case ("POST", ["m", "c", var id, "messages"]):
                return (200, MessageJson(Mine(id), Send(Mine(id), request!)), null);
            case ("POST", ["m", "c", var id, "receipt"]):
                Mine(id).Members.Single(m => m.User == Me).ReadUpTo = request!["readUpTo"]!.GetValue<DateTimeOffset>();
                return (204, null, null);
            case ("POST", ["m", "c", _, "typing"]):
                return (204, null, null);
            case ("POST", ["m", "c", _, "upload"]):
                var begun = Guid.NewGuid();
                return (200, new { uploadId = begun.ToString(), url = $"{BaseUrl}/store/{begun}", maxBytes = 100L * 1024 * 1024, links = new[] { L("complete", $"/m/uploads/{begun}", "POST") } }, null);
            case ("PUT", ["store", var put]):
                _store[Guid.Parse(put)] = body;
                return (200, null, null);
            case ("POST", ["m", "uploads", var completed]):
                var filed = Guid.NewGuid();
                _documents[filed] = _store[Guid.Parse(completed)];
                return (200, new { documentId = filed }, null);
            case ("GET", ["m", "files", var file]):
                return (200, null, _documents[Guid.Parse(file)]);
            case ("PUT", ["m", "c", var id, "messages", var message]):
                return (200, MessageJson(Mine(id), Edit(Mine(id), Guid.Parse(message), request!, ifMatch)), null);
            case ("DELETE", ["m", "c", var id, "messages", var message]):
                Mine(id).Messages.Single(p => p.Id == Guid.Parse(message)).DeletedAt = DateTimeOffset.UtcNow;
                return (204, null, null);
            case ("POST", ["m", "c", var id, "messages", var message, "hide"]):
                Mine(id).Hidden.Add(Guid.Parse(message));
                return (204, null, null);
            case ("POST", ["m", "c", var id, "members"]):
                AddMember(Mine(id), request!);
                return (204, null, null);
            case ("POST", ["m", "c", var id, "members", var user, "remove"]):
                Remove(Mine(id), Guid.Parse(user), request!["nextKeys"]!.AsArray());
                return (204, null, null);
            case ("POST", ["m", "c", var id, "members", var user, "make-admin"]):
                Mine(id).Members.Single(m => m.User == Guid.Parse(user)).IsAdmin = true;
                return (204, null, null);
            case ("POST", ["m", "c", var id, "members", var user, "revoke-admin"]):
                RevokeAdmin(Mine(id), Guid.Parse(user));
                return (204, null, null);
            case ("POST", ["m", "c", var id, "epoch"]):
                Publish(Mine(id), request!["keys"]!.AsArray());
                return (204, null, null);
            default:
                return (404, new { title = "Not here" }, null);
        }
    }

    private Chat Mine(string id) =>
        _chats.TryGetValue(Guid.Parse(id), out var chat) && chat.Members.Any(m => m.User == Me)
            ? chat
            : throw new Refused(404, "MESSENGER_NOT_A_MEMBER", "Not a member.");

    private Chat Create(string name, bool isDirect, JsonArray keys)
    {
        var chat = new Chat(Guid.NewGuid(), name, isDirect);
        chat.Keys[1] = [];
        foreach (var key in keys)
        {
            var user = key!["userId"]!.GetValue<Guid>();
            chat.Members.Add(new Membership(user, user == Me));
            chat.Keys[1][user] = Convert.FromBase64String(key["key"]!.GetValue<string>());
        }

        _chats[chat.Id] = chat;
        return chat;
    }

    private Post Send(Chat chat, JsonNode request)
    {
        var epoch = request["epoch"]!.GetValue<int>();
        if (chat.NeedsNewEpoch || epoch != chat.Epoch)
        {
            throw new Refused(409, chat.NeedsNewEpoch ? "MESSENGER_REKEY_NEEDED" : "MESSENGER_STALE_EPOCH", "Sealed under an old key.");
        }

        var files = request["attachmentDocumentIds"]?.AsArray().Select(f => f!.GetValue<Guid>()).ToList() ?? [];
        var post = new Post(request["id"]!.GetValue<Guid>(), epoch, Me, Convert.FromBase64String(request["ciphertext"]!.GetValue<string>()), DateTimeOffset.UtcNow)
        {
            ReplyTo = request["replyToId"]?.GetValue<Guid?>(),
            Files = files,
        };
        chat.Messages.Add(post);
        return post;
    }

    private Post Edit(Chat chat, Guid message, JsonNode request, string? ifMatch)
    {
        var post = chat.Messages.Single(p => p.Id == message);
        if (ifMatch != post.ETag)
        {
            throw new Refused(412, "ETAG_MISMATCH", "Changed meanwhile.");
        }

        (post.Ciphertext, post.Epoch, post.EditedAt) = (Convert.FromBase64String(request["ciphertext"]!.GetValue<string>()), request["epoch"]!.GetValue<int>(), DateTimeOffset.UtcNow);
        post.ETag = Guid.NewGuid().ToString("N");
        return post;
    }

    private void AddMember(Chat chat, JsonNode request)
    {
        var user = request["userId"]!.GetValue<Guid>();
        chat.Members.Add(new Membership(user, false));
        Publish(chat, request["keys"]!.AsArray());
    }

    private void Remove(Chat chat, Guid user, JsonArray nextKeys)
    {
        chat.Members.RemoveAll(m => m.User == user);
        if (nextKeys.Count == 0)
        {
            chat.NeedsNewEpoch = true;   // left: somebody who stays makes the next key
            return;
        }

        Publish(chat, nextKeys);
    }

    private void RevokeAdmin(Chat chat, Guid user)
    {
        if (chat.Members.Count(m => m.IsAdmin) == 1 && chat.Members.Single(m => m.User == user).IsAdmin)
        {
            throw new Refused(409, "MESSENGER_LAST_ADMIN", "A group keeps at least one administrator.");
        }

        chat.Members.Single(m => m.User == user).IsAdmin = false;
    }

    private static void Publish(Chat chat, JsonArray keys)
    {
        var epoch = chat.Epoch + 1;
        chat.Keys[epoch] = keys.ToDictionary(k => k!["userId"]!.GetValue<Guid>(), k => Convert.FromBase64String(k!["key"]!.GetValue<string>()));
        chat.NeedsNewEpoch = false;
        chat.Published.Add(epoch);
    }

    private object ConversationJson(Chat chat)
    {
        var me = chat.Members.Single(m => m.User == Me);
        var links = new List<object>
        {
            L("self", $"/m/c/{chat.Id}"), L("messages", $"/m/c/{chat.Id}/messages"), L("receipt", $"/m/c/{chat.Id}/receipt", "POST"),
            L("upload-file", $"/m/c/{chat.Id}/upload", "POST"), L("publish-epoch", $"/m/c/{chat.Id}/epoch", "POST"),
        };
        if (OffersTyping)
        {
            links.Add(L("typing", $"/m/c/{chat.Id}/typing", "POST"));
        }

        if (!chat.IsDirect && me.IsAdmin)
        {
            links.Add(L("add-member", $"/m/c/{chat.Id}/members", "POST"));
        }

        var counterpart = chat.IsDirect ? chat.Members.FirstOrDefault(m => m.User != Me) : null;
        return new
        {
            id = chat.Id,
            isDirect = chat.IsDirect,
            name = chat.Name,
            counterpartUserId = counterpart?.User,
            counterpartName = counterpart is null ? null : People[counterpart.User].Name,
            epoch = chat.Epoch,
            myKey = chat.Keys[chat.Epoch].GetValueOrDefault(Me) ?? [],
            myKeys = chat.Keys.Where(k => k.Value.ContainsKey(Me)).Select(k => new { epoch = k.Key, key = k.Value[Me] }),
            needsNewEpoch = chat.NeedsNewEpoch,
            canManageMembers = me.IsAdmin,
            filesStayInApp = chat.FilesStayInApp,
            unread = chat.Messages.Count(p => p.Sender != Me && (me.ReadUpTo is not { } read || p.SentAt > read)),
            members = chat.Members.Select(m => new
            {
                userId = m.User,
                isAdmin = m.IsAdmin,
                displayName = People[m.User].Name,
                email = People[m.User].Email,
                readUpTo = m.ReadUpTo,
                links = MemberLinks(chat, me, m),
            }),
            links,
        };
    }

    private object[] MemberLinks(Chat chat, Membership me, Membership member)
    {
        var at = $"/m/c/{chat.Id}/members/{member.User}";
        var links = new List<object> { L("certificates", $"/m/certificates/{member.User}") };
        if (!chat.IsDirect && (me.IsAdmin || member.User == Me))
        {
            links.Add(L("remove", $"{at}/remove", "POST"));
        }

        if (!chat.IsDirect && me.IsAdmin)
        {
            links.Add(member.IsAdmin ? L("revoke-admin", $"{at}/revoke-admin", "POST") : L("make-admin", $"{at}/make-admin", "POST"));
        }

        return [.. links];
    }

    private object PageJson(Chat chat) => new
    {
        items = chat.Messages.Where(p => !chat.Hidden.Contains(p.Id)).Select(p => MessageJson(chat, p)),
        links = new[] { L("send", $"/m/c/{chat.Id}/messages", "POST") },
    };

    private object MessageJson(Chat chat, Post post)
    {
        var at = $"/m/c/{chat.Id}/messages/{post.Id}";
        var links = new List<object> { L("hide", $"{at}/hide", "POST") };
        if (post.Sender == Me && post.DeletedAt is null)
        {
            links.Add(L("edit", at, "PUT"));
            links.Add(L("delete", at, "DELETE"));
        }

        if (post.Files.Count > 0)
        {
            links.Add(L("attachment", $"/m/files/{post.Files[0]}"));
        }

        return new
        {
            id = post.Id,
            epoch = post.Epoch,
            senderUserId = post.Sender,
            sentAt = post.SentAt,
            editedAt = post.EditedAt,
            deletedAt = post.DeletedAt,
            replyToId = post.ReplyTo,
            attachmentDocumentId = post.Files.Count > 0 ? post.Files[0] : (Guid?)null,
            files = post.Files.Select(f => new { documentId = f, links = new[] { L("content", $"/m/files/{f}") } }),
            ciphertext = post.DeletedAt is null ? post.Ciphertext : [],
            etag = post.ETag,
            links,
        };
    }

    private static object L(string rel, string href, string method = "GET") => new { rel, href, method };

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }

    public sealed class Chat(Guid id, string name, bool isDirect)
    {
        public Guid Id { get; } = id;

        public string Name { get; } = name;

        public bool IsDirect { get; } = isDirect;

        public bool FilesStayInApp { get; set; }

        public bool NeedsNewEpoch { get; set; }

        public List<Membership> Members { get; } = [];

        public Dictionary<int, Dictionary<Guid, byte[]>> Keys { get; } = [];

        public int Epoch => Keys.Keys.Max();

        public List<int> Published { get; } = [];

        public List<Post> Messages { get; } = [];

        public HashSet<Guid> Hidden { get; } = [];
    }

    public sealed class Membership(Guid user, bool isAdmin)
    {
        public Guid User { get; } = user;

        public bool IsAdmin { get; set; } = isAdmin;

        public DateTimeOffset? ReadUpTo { get; set; }
    }

    public sealed class Post(Guid id, int epoch, Guid sender, byte[] ciphertext, DateTimeOffset sentAt)
    {
        public Guid Id { get; } = id;

        public int Epoch { get; set; } = epoch;

        public Guid Sender { get; } = sender;

        public byte[] Ciphertext { get; set; } = ciphertext;

        public DateTimeOffset SentAt { get; } = sentAt;

        public DateTimeOffset? EditedAt { get; set; }

        public DateTimeOffset? DeletedAt { get; set; }

        public Guid? ReplyTo { get; init; }

        public List<Guid> Files { get; init; } = [];

        public string ETag { get; set; } = Guid.NewGuid().ToString("N");
    }

    private sealed class Refused(int status, string code, string message) : Exception(message)
    {
        public int Status { get; } = status;

        public string Code { get; } = code;
    }
}
