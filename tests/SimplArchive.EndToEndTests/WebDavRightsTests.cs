using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace SimplArchive.EndToEndTests;

// The mount answers each VERB by the caller's rights on what it touches, as the REST door does — not merely by
// whether a path exists. PROPFIND always filtered by CanSee, which made the gap invisible from a mounted drive:
// GET/HEAD streamed a document to anybody who knew its path, MOVE renamed and re-parented one without asking, and a
// folder COPY copied children the caller could not read.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class WebDavRightsTests
{
    private readonly E2EApiFactory _factory;

    public WebDavRightsTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Archive(string RepoName, Guid RepoId, string FolderName, Guid FolderId, string DocName, Guid DocId, string HiddenName, Guid HiddenId, HttpClient Owner, Guid TenantId);

    // A repository holding a folder with two documents; the second breaks inheritance so a reader of the folder
    // cannot read it.
    private async Task<Archive> ArchiveAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoName = $"Acl{Guid.NewGuid():N}"[..12];
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = repoName })).GetProperty("id").GetGuid();
        var folderName = $"f{Guid.NewGuid():N}"[..8];
        var folderId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = folderName })).GetProperty("id").GetGuid();
        var docName = $"d{Guid.NewGuid():N}"[..8];
        var docId = await FileAsync(owner, folderId, docName, "secret");
        var hiddenName = $"h{Guid.NewGuid():N}"[..8];
        var hiddenId = await FileAsync(owner, folderId, hiddenName, "hidden");
        (await owner.PutAsJsonAsync($"/api/documents/{hiddenId}/acl-entries/inheritance", new { breaksInheritance = true })).EnsureSuccessStatusCode();
        return new Archive(repoName, repoId, folderName, folderId, docName, docId, hiddenName, hiddenId, owner, tenantId);
    }

    private static async Task<Guid> FileAsync(HttpClient api, Guid parentId, string name, string text)
    {
        var id = (await TestJson.Post(api, $"/api/documents/{parentId}/children", new { name })).GetProperty("id").GetGuid();
        var version = await TestJson.Post(api, $"/api/documents/{id}/versions", new { fileExtension = ".txt" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.ASCII.GetBytes(text)))).EnsureSuccessStatusCode();
        }

        (await api.PutAsJsonAsync($"/api/documents/{id}/versions/{version.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        return id;
    }

    // A user with a WebDAV password and, optionally, rights on the repository (inherited by the folder and the first
    // document, not by the document that broke inheritance before the grant).
    private async Task<(Guid Id, AuthenticationHeaderValue Basic)> PersonAsync(Archive archive, object? rightsOnRepository)
    {
        var email = $"davacl-{Guid.NewGuid():N}@e2e.local";
        const string password = "davacl-1234";
        var id = await _factory.SeedUserAsync(archive.TenantId, email, password, "Dav Acl User");
        if (rightsOnRepository is not null)
        {
            (await archive.Owner.PutAsJsonAsync($"/api/documents/{archive.RepoId}/acl-entries/users/{id}", rightsOnRepository)).EnsureSuccessStatusCode();
        }

        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        var davPassword = (await TestJson.Post(api, "/api/me/webdav-password", new { })).GetProperty("password").GetString()!;
        return (id, new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}"))));
    }

    private async Task<HttpResponseMessage> SendAsync(string method, string path, AuthenticationHeaderValue basic, string? destination = null)
    {
        using var dav = _factory.CreateClient();
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Headers = { Authorization = basic } };
        if (destination is not null)
        {
            request.Headers.Add("Destination", destination);
        }

        return await dav.SendAsync(request);
    }

    [Fact]
    public async Task A_document_is_read_only_by_somebody_who_may_read_it()
    {
        var archive = await ArchiveAsync();
        var path = $"/{(global::SimplArchive.Localization.ProductBuild.Name)}/{archive.RepoName}/{archive.FolderName}/{archive.DocName}.txt";
        var (_, outsider) = await PersonAsync(archive, rightsOnRepository: null);
        var (_, seer) = await PersonAsync(archive, new { canSee = true });
        var (_, reader) = await PersonAsync(archive, new { canSee = true, canReadContent = true });

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("GET", path, outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("HEAD", path, outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync("GET", path, seer)).StatusCode);

        var read = await SendAsync("GET", path, reader);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("secret", await read.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_document_is_moved_or_renamed_only_with_the_rights_the_rest_door_asks()
    {
        var archive = await ArchiveAsync();
        var path = $"/{(global::SimplArchive.Localization.ProductBuild.Name)}/{archive.RepoName}/{archive.FolderName}/{archive.DocName}.txt";
        var (_, outsider) = await PersonAsync(archive, rightsOnRepository: null);
        var (_, reader) = await PersonAsync(archive, new { canSee = true, canReadContent = true });
        var (_, mover) = await PersonAsync(archive, new { canSee = true, canReadContent = true, canMove = true, canCreateSubItems = true, canEditIndexData = true });

        var elsewhere = $"/{(global::SimplArchive.Localization.ProductBuild.Name)}/{archive.RepoName}/moved{Guid.NewGuid().ToString("N")[..6]}.txt";
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("MOVE", path, outsider, elsewhere)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync("MOVE", path, reader, elsewhere)).StatusCode);
        var unchanged = await TestJson.Get(archive.Owner, $"/api/documents/{archive.DocId}");
        Assert.Equal(archive.DocName, unchanged.GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.Created, (await SendAsync("MOVE", path, mover, elsewhere)).StatusCode);
    }

    [Fact]
    public async Task A_folder_copy_leaves_out_what_the_caller_may_not_read()
    {
        var archive = await ArchiveAsync();
        var (_, reader) = await PersonAsync(archive, new { canSee = true, canReadContent = true, canCreateSubItems = true });
        var copyName = $"c{Guid.NewGuid():N}"[..8];

        var copied = await SendAsync("COPY", $"/{(global::SimplArchive.Localization.ProductBuild.Name)}/{archive.RepoName}/{archive.FolderName}", reader, $"/{(global::SimplArchive.Localization.ProductBuild.Name)}/{archive.RepoName}/{copyName}");
        Assert.Equal(HttpStatusCode.Created, copied.StatusCode);

        var copy = (await TestJson.Get(archive.Owner, $"/api/documents/{archive.RepoId}/children"))
            .GetProperty("children").EnumerateArray().Single(c => c.GetProperty("name").GetString() == copyName);
        var names = (await TestJson.Get(archive.Owner, $"/api/documents/{copy.GetProperty("id").GetGuid()}/children"))
            .GetProperty("children").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains(archive.DocName, names);
        Assert.DoesNotContain(archive.HiddenName, names);
    }

    // The verbs that already asked rights before #1935, proven rather than read: an outsider who knows a path cannot
    // overwrite the document, delete it, create a folder beside it, or learn its properties.
    [Fact]
    public async Task An_outsider_who_knows_a_path_can_neither_write_delete_create_nor_inspect()
    {
        var archive = await ArchiveAsync();
        var folder = $"/{(global::SimplArchive.Localization.ProductBuild.Name)}/{archive.RepoName}/{archive.FolderName}";
        var path = $"{folder}/{archive.DocName}.txt";
        var (_, outsider) = await PersonAsync(archive, rightsOnRepository: null);

        using (var dav = _factory.CreateClient())
        {
            var put = new HttpRequestMessage(HttpMethod.Put, path) { Headers = { Authorization = outsider }, Content = new StringContent("overwritten") };
            Assert.False((await dav.SendAsync(put)).IsSuccessStatusCode);
        }

        Assert.False((await SendAsync("DELETE", path, outsider)).IsSuccessStatusCode);
        Assert.False((await SendAsync("MKCOL", $"{folder}/planted{Guid.NewGuid().ToString("N")[..6]}", outsider)).IsSuccessStatusCode);

        using (var dav = _factory.CreateClient())
        {
            var propfind = new HttpRequestMessage(new HttpMethod("PROPFIND"), path) { Headers = { Authorization = outsider } };
            propfind.Headers.Add("Depth", "0");
            var answer = await dav.SendAsync(propfind);
            Assert.DoesNotContain(archive.DocName, await answer.Content.ReadAsStringAsync());
        }

        // Nothing changed: the content, the document and the folder's children are as they were.
        using var plain = new HttpClient();
        var download = (await TestJson.Get(archive.Owner, $"/api/documents/{archive.DocId}/versions")).GetProperty("versions").EnumerateArray().Single()
            .GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "download").GetProperty("href").GetString()!;
        Assert.Equal("secret", await plain.GetStringAsync(download));
        var children = (await TestJson.Get(archive.Owner, $"/api/documents/{archive.FolderId}/children")).GetProperty("children").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(2, children.Count);

        // The control: the same PUT from somebody with edit rights is accepted, so the refusals above are the rights'
        // doing and not a request this mount refuses from anybody.
        var (_, editor) = await PersonAsync(archive, new { canSee = true, canReadContent = true, canEditContent = true });
        using var editorDav = _factory.CreateClient();
        var allowed = new HttpRequestMessage(HttpMethod.Put, path) { Headers = { Authorization = editor }, Content = new StringContent("edited") };
        var accepted = await editorDav.SendAsync(allowed);
        Assert.True(accepted.IsSuccessStatusCode, $"editor PUT: {(int)accepted.StatusCode}");
    }
}
