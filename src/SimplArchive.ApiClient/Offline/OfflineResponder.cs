using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace SimplArchive.ApiClient.Offline;

/// <summary>
/// The API's own links, answered in process from an archive (#1697; owner decisions 2026-10-06 and 2026-10-13): no
/// port, no socket, no file URL. A client keeps following the links it is handed (ADR 0543), so it browses the
/// archive exactly as it browses the server, and its affordances follow from which links exist.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only, and only what the archive carries:</b> the root, the repository (the exported root), folder contents
/// and the references filed in a folder (core ADR 0959), a document's detail, its versions, its index data, its chat, and the content of each version. No write link, no
/// search, no sensitivity or workflow: a client reads their absence as "not available here, now" and hides them.
/// </para>
/// <para>
/// <b>Every blob is checked against its SHA-256 before it is served</b>; a mismatch is a named refusal
/// (<c>OFFLINE_BLOB_CORRUPT</c>), never content. The responder's own addresses live under <c>offline/</c>: they are
/// the links it advertises, not a copy of the server's routes, and a client never composes them.
/// </para>
/// </remarks>
public sealed class OfflineResponder(OfflineArchive archive) : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public OfflineArchive Archive => archive;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var segments = request.RequestUri!.AbsolutePath.Trim('/').Split('/');

        // Read-only by design. The one POST answered is the personal space's: on the server it is get-or-create, and
        // here it only gets.
        if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head
            && !(request.Method == HttpMethod.Post && segments is ["offline", "personal"]))
        {
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        if (segments is ["offline", "blobs", var name])
        {
            return await BlobAsync(name, request.Method == HttpMethod.Head, cancellationToken);
        }

        return segments switch
        {
            ["api"] => Json(new { links = Root() }),
            ["offline", "notifications"] when archive.Notifications is { } kept => Json(new
            {
                notifications = kept.Select(n => new
                {
                    id = n.Id,
                    type = n.Type,
                    title = n.Title,
                    body = n.Body,
                    documentId = n.DocumentId,
                    createdAt = n.CreatedAt,
                    isRead = n.IsRead,
                    // What it is about, where the cache kept that too; a "read" link is not offered: nothing is written offline.
                    links = n.DocumentId is { } about && archive.Document(about) is not null
                        ? new[] { Link("document", $"/offline/documents/{about}") }
                        : [],
                }),
                unreadCount = kept.Count(n => !n.IsRead),
                links = new[] { Link("self", "/offline/notifications") },
            }),
            ["offline", "me"] => Json(new { links = new[] { Link("self", "/offline/me"), Link("personalRepository", "/offline/personal") } }),
            ["offline", "archive"] => Json(new { name = archive.Name, exportedAt = archive.ExportedAt, links = new[] { Link("self", "/offline/archive") } }),
            ["offline", "repositories"] => Json(new { repositories = archive.Roots().Where(r => r.Id != archive.PersonalRoot).Select(Row), links = new[] { Link("self", "/offline/repositories") } }),
            ["offline", "personal"] => archive.PersonalRoot is { } personal && archive.Document(personal) is { } space
                ? Json(Row(space))
                : new HttpResponseMessage(HttpStatusCode.NotFound),
            ["offline", "documents", var id] => Guid.TryParse(id, out var d) && archive.Document(d) is { } document ? Json(Detail(document)) : NotFound(),
            ["offline", "documents", var id, "children"] => Guid.TryParse(id, out var f) && archive.Document(f) is not null
                ? Json(new
                {
                    children = archive.Children(f).OrderByDescending(c => archive.IsFolder(c.Id)).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(Row),
                    contentsSortOrder = 1,
                    links = new[] { Link("self", $"/offline/documents/{f}/children") },
                })
                : NotFound(),
            ["offline", "documents", var id, "references"] => Guid.TryParse(id, out var rf) && archive.Document(rf) is not null
                ? Json(new { references = archive.References(rf).Select(Reference), links = new[] { Link("self", $"/offline/documents/{rf}/references") } })
                : NotFound(),
            ["offline", "documents", var id, "versions"] => Guid.TryParse(id, out var v) && archive.Document(v) is not null
                ? Json(new { versions = archive.Versions(v).Select(Version), links = new[] { Link("self", $"/offline/documents/{v}/versions") } })
                : NotFound(),
            ["offline", "documents", var id, "index-data"] => Guid.TryParse(id, out var x) && archive.Document(x) is not null
                ? Json(new { fields = archive.Fields(x).Select(field => new { fieldName = field.Name, values = field.Values, dataType = field.DataType }) })
                : NotFound(),
            ["offline", "documents", var id, "chat"] => Guid.TryParse(id, out var c) && archive.Document(c) is not null
                ? Json(new
                {
                    messages = archive.Chat(c).Select(m => new
                    {
                        id = m.Id,
                        parentMessageId = m.ParentMessageId,
                        body = m.Body,
                        authorName = m.Author,
                        createdAt = m.CreatedAt,
                        mentions = m.Mentions.Select(x => new { userId = x.UserId, displayName = x.DisplayName }),
                    }),
                    links = new[] { Link("self", $"/offline/documents/{c}/chat") },
                })
                : NotFound(),
            _ => NotFound(),
        };
    }

    // The root's links: the notifications only where the archive keeps them (a cache), so an export offers no bell.
    private object[] Root()
    {
        var links = new List<object> { Link("repositories", "/offline/repositories"), Link("me", "/offline/me"), Link("offline", "/offline/archive") };
        if (archive.Notifications is not null)
        {
            links.Add(Link("notifications", "/offline/notifications"));
        }

        return [.. links];
    }

    private object Row(OfflineArchive.ArchivedDocument d)
    {
        var folder = archive.IsFolder(d.Id);
        var current = archive.Versions(d.Id).FirstOrDefault();
        return new
        {
            id = d.Id,
            name = d.Name,
            isFolder = folder,
            // Anything filed here, a reference too (core #376).
            hasChildren = archive.Children(d.Id).Any() || archive.References(d.Id).Count > 0,
            hasSubfolders = archive.Children(d.Id).Any(c => archive.IsFolder(c.Id)),
            hasVersions = !folder,
            versionCount = archive.Versions(d.Id).Count,
            documentType = archive.MaskName(d),
            documentDate = current?.DocumentDate,
            createdBy = d.CreatedBy,
            versionCreatedAt = current?.FiledAt,
            links = Links(d, folder),
        };
    }

    /// <summary>
    /// A reference row as the server's folder references listing has it (core ADR 0959): the target's row with the
    /// reference's own fields, its links the target's in the copy. A target the copy does not hold is listed with no
    /// link and says why in <c>offlineUnavailable</c>: <c>NotInCopy</c> (not brought in, or evicted). A reference whose
    /// target the user may not see is not in the copy at all, as online (ADR 0765). No <c>delete</c>: nothing is written offline.
    /// </summary>
    private object Reference(OfflineArchive.ArchivedReference r)
    {
        if (archive.Document(r.TargetId) is { } target)
        {
            var folder = archive.IsFolder(target.Id);
            var current = archive.Versions(target.Id).FirstOrDefault();
            var links = Links(target, folder).ToList();
            if (target.ParentId is { } parent && archive.Document(parent) is not null)
            {
                links.Add(Link("go-to", $"/offline/documents/{parent}"));
            }

            return new
            {
                referenceId = r.Id,
                id = target.Id,
                name = target.Name,
                isReference = true,
                isFolder = folder,
                hasChildren = archive.Children(target.Id).Any() || archive.References(target.Id).Count > 0,
                hasSubfolders = archive.Children(target.Id).Any(c => archive.IsFolder(c.Id)),
                hasVersions = !folder,
                realParentId = target.ParentId,
                versionCount = archive.Versions(target.Id).Count,
                documentType = archive.MaskName(target),
                documentDate = current?.DocumentDate,
                createdBy = target.CreatedBy,
                versionCreatedAt = current?.FiledAt,
                links,
            };
        }

        var isFolder = r.TargetIsFolder ?? false;
        return new
        {
            referenceId = r.Id,
            id = r.TargetId,
            name = r.TargetName ?? string.Empty,
            isReference = true,
            isFolder,
            hasChildren = false,
            hasSubfolders = false,
            hasVersions = !isFolder,
            offlineUnavailable = "NotInCopy",
            links = Array.Empty<object>(),
        };
    }

    private object Detail(OfflineArchive.ArchivedDocument d) => new
    {
        id = d.Id,
        name = d.Name,
        contentsSortOrder = 1,
        links = Links(d, archive.IsFolder(d.Id)),
    };

    private object Version(OfflineArchive.ArchivedVersion v)
    {
        var blob = $"/offline/blobs/{v.Sha256}{v.FileExtension}";
        return new
        {
            id = v.Id,
            versionNumber = v.Number,
            status = "Confirmed",
            documentDate = v.DocumentDate,
            createdAt = v.FiledAt,
            createdBy = v.CreatedBy,
            fileExtension = v.FileExtension,
            objectKey = $"content{v.FileExtension}",
            previewConverted = false,
            links = new[] { Link("download", blob), Link("preview", blob) },
        };
    }

    private static object[] Links(OfflineArchive.ArchivedDocument d, bool folder)
    {
        var self = $"/offline/documents/{d.Id}";
        var links = new List<object> { Link("self", self), Link("chat", $"{self}/chat") };
        if (folder)
        {
            links.Add(Link("children", $"{self}/children"));
            links.Add(Link("references", $"{self}/references"));
        }
        else
        {
            links.Add(Link("versions", $"{self}/versions"));
        }

        links.Add(Link("index-data", $"{self}/index-data"));
        return [.. links];
    }

    private async Task<HttpResponseMessage> BlobAsync(string name, bool head, CancellationToken cancellationToken)
    {
        var sha = Path.GetFileNameWithoutExtension(name);
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit) || await archive.Storage.ReadAsync($"blobs/{sha}", cancellationToken) is not { } bytes)
        {
            return NotFound();
        }

        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), sha, StringComparison.OrdinalIgnoreCase))
        {
            // Never content: a blob that is not what its name says is refused by name (#1697).
            var refusal = new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = JsonContent.Create(new { title = "The archive's copy of this content is damaged.", errorCode = "OFFLINE_BLOB_CORRUPT" }),
            };
            refusal.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/problem+json");
            return refusal;
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(head ? [] : bytes) };
        // As it was served where the cache recorded it: a strict tenant's envelope stays an envelope, and the client's
        // content funnel opens it with the card, offline as online (ADR 0830).
        response.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
            archive.ContentTypeOf(sha) ?? DocumentsClient.GuessContentType(name));
        return response;
    }

    private static object Link(string rel, string href) => new { rel, href, method = "GET" };

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: Web) };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);
}

/// <summary>The address an offline client is based at. Nothing listens there: the responder answers every request.</summary>
public static class OfflineAddress
{
    public const string Root = "http://offline.simplarchive.invalid/";
}
