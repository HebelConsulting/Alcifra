using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.ApiClient;
using SimplArchive.ApiClient.Offline;

namespace SimplArchive.UnitTests.ApiClient;

// The offline copy shows references (core ADR 0959, #1881): a folder lists the references filed in it through the
// same rel and row shape as the server's listing, a target the copy holds opens from the row, and one it does not
// hold is listed saying why, with no link to follow.
public sealed class OfflineResponderReferenceTests
{
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid MyDocuments = Guid.NewGuid();
    private static readonly Guid OtherRepo = Guid.NewGuid();
    private static readonly Guid Target = Guid.NewGuid();
    private static readonly Guid Evicted = Guid.NewGuid();
    private static readonly Guid Hidden = Guid.NewGuid();
    private static readonly Guid TargetFolder = Guid.NewGuid();
    private static readonly byte[] Content = Encoding.UTF8.GetBytes("the referenced report");

    private static byte[] Archive()
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(Content));
        var placed = DateTimeOffset.Parse("2026-10-22T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string path, string text) { using var w = new StreamWriter(zip.CreateEntry(path).Open()); w.Write(text); }

            Entry("manifest.json", JsonSerializer.Serialize(new { formatVersion = 2, kind = "cache", name = "Offline copies" }));
            Entry("tree/documents.jsonl", string.Join('\n',
                JsonSerializer.Serialize(new { id = Root, name = "Personal" }),
                JsonSerializer.Serialize(new { id = MyDocuments, parentId = Root, name = "My documents" }),
                JsonSerializer.Serialize(new { id = OtherRepo, name = "Contracts" }),
                JsonSerializer.Serialize(new { id = Target, parentId = OtherRepo, name = "Framework agreement" }),
                JsonSerializer.Serialize(new { id = TargetFolder, parentId = OtherRepo, name = "Templates" })));
            Entry("tree/versions.jsonl", JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid(),
                documentId = Target,
                versionNumber = 1,
                documentDate = "2026-10-01",
                filedAt = placed,
                sha256 = sha,
                blobRef = sha,
                fileExtension = ".txt",
            }));
            Entry("tree/references.jsonl", string.Join('\n',
                JsonSerializer.Serialize(new { id = Guid.NewGuid(), parentFolderId = MyDocuments, targetDocumentId = Target, createdAt = placed, targetAccessible = true, targetName = "Framework agreement", targetIsFolder = false }),
                JsonSerializer.Serialize(new { id = Guid.NewGuid(), parentFolderId = MyDocuments, targetDocumentId = Evicted, createdAt = placed.AddMinutes(1), targetAccessible = true, targetName = "Old offer", targetIsFolder = false }),
                JsonSerializer.Serialize(new { id = Guid.NewGuid(), parentFolderId = MyDocuments, targetDocumentId = Hidden, createdAt = placed.AddMinutes(2), targetAccessible = false, targetName = (string?)null, targetIsFolder = (bool?)null }),
                JsonSerializer.Serialize(new { id = Guid.NewGuid(), parentFolderId = MyDocuments, targetDocumentId = TargetFolder, createdAt = placed.AddMinutes(3), targetAccessible = true, targetName = "Templates", targetIsFolder = true })));
            using var blob = zip.CreateEntry($"blobs/{sha}").Open();
            blob.Write(Content);
        }

        return buffer.ToArray();
    }

    private static async Task<OfflineResponder> ResponderAsync() => new(await OfflineArchive.OpenAsync(new ZipArchiveStorage(Archive())));

    private static async Task<List<ReferencesClient.Reference>> ListedAsync(OfflineResponder responder)
    {
        var client = new HttpClient(responder) { BaseAddress = new Uri(OfflineAddress.Root) };
        var folder = await client.GetFromJsonAsync<JsonElement>($"offline/documents/{MyDocuments}");
        var references = ApiCore.FindLink(folder, "references");
        Assert.NotNull(references);   // the rel the online folder advertises, so a client finds the listing the same way
        return await new ReferencesClient(ApiCore.FromArchive(responder)).GetReferencesAsync(references);
    }

    [Fact]
    public async Task A_folder_lists_its_references_in_the_servers_order_and_shape()
    {
        var listed = await ListedAsync(await ResponderAsync());

        Assert.Equal([Target, Evicted, Hidden, TargetFolder], listed.Select(r => r.TargetId));
        var target = listed[0];
        Assert.Equal(("Framework agreement", false, 1, (string?)null), (target.Name, target.IsFolder, target.VersionCount, target.OfflineUnavailable));
        Assert.True(listed[3].IsFolder);
        Assert.Null(target.DeleteHref);   // nothing is written offline
    }

    [Fact]
    public async Task A_folder_holding_only_references_has_contents()
    {
        var client = new HttpClient(await ResponderAsync()) { BaseAddress = new Uri(OfflineAddress.Root) };
        var children = await client.GetFromJsonAsync<JsonElement>($"offline/documents/{Root}/children");

        Assert.True(children.GetProperty("children").EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == MyDocuments).GetProperty("hasChildren").GetBoolean());
    }

    [Fact]
    public async Task A_target_in_the_copy_opens_from_its_reference_row()
    {
        var responder = await ResponderAsync();
        var target = (await ListedAsync(responder))[0];
        var documents = new DocumentsClient(ApiCore.FromArchive(responder), () => throw new InvalidOperationException("no reminders offline"));

        var detail = await documents.GetDocumentDetailAsync(target.Links!.Href("self")!);
        var preview = await new VersionsClient(ApiCore.FromArchive(responder)).GetPreviewFromVersionsAsync(detail.Links!.Href("versions")!);
        // Read through the responder itself: the content funnel's static session belongs to whichever test set it last.
        var bytes = await new HttpClient(responder) { BaseAddress = new Uri(OfflineAddress.Root) }.GetByteArrayAsync(preview.DownloadUrl!);

        Assert.Equal("Framework agreement", detail.Name);
        Assert.Equal(Content, bytes);
        Assert.NotNull(target.Links.Href("go-to"));   // its real folder is in the copy too
    }

    [Fact]
    public async Task A_target_not_in_the_copy_is_listed_saying_why_and_offers_nothing_to_follow()
    {
        var listed = await ListedAsync(await ResponderAsync());

        var evicted = listed[1];
        Assert.Equal(("Old offer", "NotInCopy"), (evicted.Name, evicted.OfflineUnavailable));
        Assert.Null(evicted.Links?.Href("self"));

        // A target the user may not see is not named (core ADR 0765).
        var hidden = listed[2];
        Assert.Equal((string.Empty, "NotAccessible"), (hidden.Name, hidden.OfflineUnavailable));
        Assert.Null(hidden.Links?.Href("self"));

        var client = new HttpClient(await ResponderAsync()) { BaseAddress = new Uri(OfflineAddress.Root) };
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"offline/documents/{Evicted}")).StatusCode);
    }
}
