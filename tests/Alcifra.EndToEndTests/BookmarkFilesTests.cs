using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Alcifra.ApiClient;
using Alcifra.ApiClient.Bookmarks;
using Alcifra.Domain.Masks;

namespace Alcifra.EndToEndTests;

// ADR 1062: the bookmark files a browser keeps in step with, over the real API and real storage. Found by mask, a folder's
// rights decide, another person's space stays out, and a save carries the tag it started from.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class BookmarkFilesTests(E2EApiFactory factory)
{
    private const string Html = "<!DOCTYPE NETSCAPE-Bookmark-file-1>\n<DL><p>\n</DL><p>\n";

    private static async Task<JsonElement> VersionAsync(HttpClient client, Guid document, string extension, string body)
    {
        var version = await TestJson.Post(client, $"/api/documents/{document}/versions", new { fileExtension = extension });
        using var storage = new HttpClient();
        using var put = await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new StringContent(body, Encoding.UTF8));
        put.EnsureSuccessStatusCode();
        return version;
    }

    private static async Task<HttpResponseMessage> ConfirmAsync(HttpClient client, Guid document, JsonElement version, string? ifMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{document}/versions/{version.GetProperty("id").GetGuid()}")
        {
            Content = JsonContent.Create(new { }),
        };
        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(ifMatch));
        }

        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> EnsureAsync(HttpClient client)
    {
        using var ensured = await client.PostAsync("/api/bookmarks", null);
        Assert.True(ensured.IsSuccessStatusCode, await ensured.Content.ReadAsStringAsync());
        return await ensured.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<(HttpClient Client, Guid Id)> PersonWithIdAsync(Guid tenantId)
    {
        var email = $"bookmarks-{Guid.NewGuid():N}@e2e.local";
        var id = await factory.SeedUserAsync(tenantId, email, "bookmarks-1234", "Somebody Else");
        return (factory.CreateAuthedClient(await factory.GetUserTokenAsync(email, "bookmarks-1234")), id);
    }

    private async Task<HttpClient> PersonAsync(Guid tenantId) => (await PersonWithIdAsync(tenantId)).Client;

    [Fact]
    public async Task The_root_advertises_the_files_and_the_personal_one_is_made_once_at_first_sync()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        var root = await TestJson.Get(rig.Admin, "/api");
        Assert.Contains(root.GetProperty("links").EnumerateArray(), l => l.GetProperty("rel").GetString() == "bookmarks");
        Assert.Empty((await TestJson.Get(rig.Admin, "/api/bookmarks")).EnumerateArray());

        var first = await EnsureAsync(rig.Admin);
        var again = await EnsureAsync(rig.Admin);

        Assert.Equal(first.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid());
        Assert.True(first.GetProperty("isPersonal").GetBoolean());
        Assert.True(first.GetProperty("canEdit").GetBoolean());
        Assert.False(first.GetProperty("hasContent").GetBoolean());
        Assert.Single((await TestJson.Get(rig.Admin, "/api/bookmarks")).EnumerateArray().ToList());
    }

    [Fact]
    public async Task Another_persons_file_stays_out_of_the_list_even_for_an_administrator()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        var theirs = (await EnsureAsync(await PersonAsync(rig.TenantId))).GetProperty("id").GetGuid();

        Assert.DoesNotContain((await TestJson.Get(rig.Admin, "/api/bookmarks")).EnumerateArray(), f => f.GetProperty("id").GetGuid() == theirs);
    }

    // Something the person already called Bookmarks is theirs: the file takes another name rather than failing the sync.
    [Fact]
    public async Task A_folder_already_named_bookmarks_does_not_stop_the_first_sync()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        var person = await PersonAsync(rig.TenantId);
        var personal = (await TestJson.Post(person, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
        var myDocuments = (await TestJson.Get(person, $"/api/documents/{personal}/children"))
            .GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "My Documents")
            .GetProperty("id").GetGuid();
        await TestJson.Post(person, $"/api/documents/{myDocuments}/children", new { name = "Bookmarks" });

        var file = await EnsureAsync(person);

        Assert.Equal("Bookmarks (2)", file.GetProperty("name").GetString());
        Assert.Equal("My Documents", file.GetProperty("folderName").GetString());
        Assert.Equal(file.GetProperty("id").GetGuid(), (await EnsureAsync(person)).GetProperty("id").GetGuid());
    }

    // A file in a shared folder reaches whoever may read it, read-only for someone who may not write, and nobody else.
    [Fact]
    public async Task A_folders_rights_decide_who_gets_a_shared_file()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        var shared = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children", new { name = "Team links" })).GetProperty("id").GetGuid();
        await TestJson.Put(rig.Owner, $"/api/documents/{shared}/mask", new { maskId = WellKnownMaskIds.Bookmarks });

        var (reader, readerId) = await PersonWithIdAsync(rig.TenantId);
        var (writer, writerId) = await PersonWithIdAsync(rig.TenantId);
        var outsider = await PersonAsync(rig.TenantId);
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{readerId}", new { canSee = true, canReadContent = true });
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{writerId}", new { canSee = true, canReadContent = true, canEditContent = true });

        static JsonElement Shared(JsonElement list, Guid id) => list.EnumerateArray().Single(f => f.GetProperty("id").GetGuid() == id);

        var asReader = Shared(await TestJson.Get(reader, "/api/bookmarks"), shared);
        Assert.False(asReader.GetProperty("canEdit").GetBoolean());
        Assert.False(asReader.GetProperty("isPersonal").GetBoolean());
        Assert.DoesNotContain(asReader.GetProperty("links").EnumerateArray(), l => l.GetProperty("rel").GetString() == "versions");
        Assert.True(Shared(await TestJson.Get(writer, "/api/bookmarks"), shared).GetProperty("canEdit").GetBoolean());
        Assert.Empty((await TestJson.Get(outsider, "/api/bookmarks")).EnumerateArray());
    }

    // The client a browser's host runs, over the real API and real storage: the first sync, a change coming back, and a
    // save that started from a tag somebody has moved since.
    [Fact]
    public async Task The_client_syncs_a_file_through_the_real_doors()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        var remote = new BookmarksClient(ApiCore.Over(await PersonAsync(rig.TenantId)));

        var file = Assert.Single(await BookmarkSync.FilesAsync(remote, default));
        Assert.True(file.IsPersonal);
        Assert.False(file.HasContent);

        var first = await BookmarkSync.RunAsync(remote, file.Id, null, null, default);
        Assert.True(first.Saved);
        Assert.Empty(first.Tree);

        BookmarkNode mark = new("a", "Bücher & <more>", "https://example.test/?a=1&b=2");
        var second = await BookmarkSync.RunAsync(remote, file.Id, [], [mark], default);
        Assert.True(second.Saved);

        var third = await BookmarkSync.RunAsync(remote, file.Id, [mark], [mark], default);
        Assert.False(third.Saved);
        Assert.Equal(mark, Assert.Single(third.Tree));

        var stale = Assert.Single(await remote.ListAsync(default));
        await BookmarkSync.RunAsync(remote, file.Id, [mark], [mark, new BookmarkNode("b", "Other", "https://other.test/")], default);
        await Assert.ThrowsAsync<BookmarkFileChangedException>(() => remote.SaveAsync(stale, BookmarkHtml.Write([mark]), default));
        Assert.Equal(2, (await BookmarkSync.RunAsync(remote, file.Id, null, null, default)).Tree.Count);
    }

    [Fact]
    public async Task The_client_reads_a_shared_file_read_only_and_writes_it_where_the_folder_allows()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        var shared = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children", new { name = "Team links" })).GetProperty("id").GetGuid();
        await TestJson.Put(rig.Owner, $"/api/documents/{shared}/mask", new { maskId = WellKnownMaskIds.Bookmarks });
        var (reader, readerId) = await PersonWithIdAsync(rig.TenantId);
        var (writer, writerId) = await PersonWithIdAsync(rig.TenantId);
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{readerId}", new { canSee = true, canReadContent = true });
        await TestJson.Put(rig.Owner, $"/api/documents/{rig.RepoId}/acl-entries/users/{writerId}", new { canSee = true, canReadContent = true, canEditContent = true });

        BookmarkNode handbook = new("h", "Handbook", "https://handbook.test/");
        Assert.True((await BookmarkSync.RunAsync(new BookmarksClient(ApiCore.Over(writer)), shared, null, [handbook], default)).Saved);

        var asReader = await BookmarkSync.RunAsync(
            new BookmarksClient(ApiCore.Over(reader)), shared, [handbook], [handbook, new BookmarkNode("x", "Mine", "https://mine.test/")], default);

        Assert.False(asReader.Saved);
        Assert.Equal(handbook, Assert.Single(asReader.Tree));
    }

    // A save carries the tag it started from: a stale one is refused, and a missing one too once there is content.
    [Fact]
    public async Task A_change_needs_the_tag_it_started_from()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        var id = (await EnsureAsync(rig.Admin)).GetProperty("id").GetGuid();

        using (var first = await ConfirmAsync(rig.Admin, id, await VersionAsync(rig.Admin, id, ".html", Html), null))
        {
            Assert.True(first.IsSuccessStatusCode, await first.Content.ReadAsStringAsync());
        }

        var listed = (await TestJson.Get(rig.Admin, "/api/bookmarks")).EnumerateArray().Single();
        Assert.True(listed.GetProperty("hasContent").GetBoolean());
        Assert.Contains(listed.GetProperty("links").EnumerateArray(), l => l.GetProperty("rel").GetString() == "currentVersion");
        var tag = listed.GetProperty("eTag").GetString()!;

        using (var missing = await ConfirmAsync(rig.Admin, id, await VersionAsync(rig.Admin, id, ".html", Html), null))
        {
            Assert.Equal((HttpStatusCode)428, missing.StatusCode);
        }

        using (var fresh = await ConfirmAsync(rig.Admin, id, await VersionAsync(rig.Admin, id, ".html", Html), tag))
        {
            Assert.True(fresh.IsSuccessStatusCode, await fresh.Content.ReadAsStringAsync());
        }

        using var stale = await ConfirmAsync(rig.Admin, id, await VersionAsync(rig.Admin, id, ".html", Html), tag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    [Fact]
    public async Task A_bookmark_file_holds_only_html()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        var id = (await EnsureAsync(rig.Admin)).GetProperty("id").GetGuid();

        using var refused = await rig.Admin.PostAsJsonAsync($"/api/documents/{id}/versions", new { fileExtension = ".txt" });
        Assert.False(refused.IsSuccessStatusCode);
    }
}
