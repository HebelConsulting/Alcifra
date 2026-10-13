using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.ApiClient.Offline;
using SimplArchive.Domain.Masks;

namespace SimplArchive.UnitTests.ApiClient;

// The offline responder's own guarantees (#1697), without a server: a blob is served only when it is what its name
// says; it answers reads only; the archive's field types map to the domain's names; a directory archive cannot be led
// outside its directory. Parity with the server is OfflineResponderParityTests (desktop suite).
public sealed class OfflineResponderTests
{
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid Document = Guid.NewGuid();

    private static byte[] Archive(byte[] content, byte[]? storedAs = null)
    {
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string path, string text) { using var w = new StreamWriter(zip.CreateEntry(path).Open()); w.Write(text); }

            Entry("manifest.json", JsonSerializer.Serialize(new { formatVersion = 2, root = new { documentId = Root, name = "Archive" } }));
            Entry("tree/documents.jsonl", string.Join('\n',
                JsonSerializer.Serialize(new { id = Root, parentId = (Guid?)null, name = "Archive" }),
                JsonSerializer.Serialize(new { id = Document, parentId = Root, name = "Report" })));
            Entry("tree/versions.jsonl", JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid(),
                documentId = Document,
                versionNumber = 1,
                documentDate = "2026-10-13",
                filedAt = DateTimeOffset.UtcNow,
                sha256 = sha,
                blobRef = sha,
                fileExtension = ".txt",
            }));
            using var blob = zip.CreateEntry($"blobs/{sha}").Open();
            blob.Write(storedAs ?? content);
        }

        return buffer.ToArray();
    }

    private static HttpClient Client(byte[] zip) =>
        new(new OfflineResponder(OfflineArchive.Open(new ZipArchiveStorage(zip)))) { BaseAddress = new Uri(OfflineAddress.Root) };

    private static string BlobOf(byte[] content) => $"offline/blobs/{Convert.ToHexStringLower(SHA256.HashData(content))}.txt";

    [Fact]
    public async Task A_blob_that_is_what_its_name_says_is_served()
    {
        var content = Encoding.UTF8.GetBytes("the report");

        using var response = await Client(Archive(content)).GetAsync(BlobOf(content));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(content, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_damaged_blob_is_refused_by_name_and_never_served()
    {
        var content = Encoding.UTF8.GetBytes("the report");

        using var response = await Client(Archive(content, storedAs: Encoding.UTF8.GetBytes("tampered"))).GetAsync(BlobOf(content));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("OFFLINE_BLOB_CORRUPT", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task It_answers_reads_only(string method)
    {
        using var response = await Client(Archive([1])).SendAsync(new HttpRequestMessage(new HttpMethod(method), $"offline/documents/{Document}"));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Index_data_and_chat_read_as_the_server_answers_them()
    {
        var maskVersion = Guid.NewGuid();
        var amount = Guid.NewGuid();
        var tags = Guid.NewGuid();
        var anna = Guid.NewGuid();
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Entry(string path, string text) { using var w = new StreamWriter(zip.CreateEntry(path).Open()); w.Write(text); }

            Entry("manifest.json", JsonSerializer.Serialize(new { root = new { documentId = Root, name = "Archive" } }));
            Entry("masks/masks.json", JsonSerializer.Serialize(new[]
            {
                new
                {
                    maskId = Guid.NewGuid(),
                    version = new { maskVersionId = maskVersion, name = "Invoice" },
                    fields = new[] { new { fieldDefinitionId = amount, name = "Amount", dataType = 1 }, new { fieldDefinitionId = tags, name = "Tags", dataType = 6 } },
                },
            }));
            Entry("principals/principals.json", JsonSerializer.Serialize(new { users = new[] { new { id = anna, displayName = "Anna" } } }));
            Entry("tree/documents.jsonl", string.Join('\n',
                JsonSerializer.Serialize(new { id = Root, name = "Archive" }),
                JsonSerializer.Serialize(new { id = Document, parentId = Root, name = "Invoice 7", maskVersionId = maskVersion, createdByUserId = anna })));
            Entry("tree/index-data.jsonl", string.Join('\n',
                JsonSerializer.Serialize(new { documentId = Document, fieldDefinitionId = tags, value = "urgent", ordinal = 1 }),
                JsonSerializer.Serialize(new { documentId = Document, fieldDefinitionId = amount, value = "120.50", ordinal = 0 }),
                JsonSerializer.Serialize(new { documentId = Document, fieldDefinitionId = tags, value = "paid", ordinal = 0 })));
            Entry("tree/chat.jsonl", JsonSerializer.Serialize(new
            {
                id = Guid.NewGuid(),
                documentId = Document,
                body = $"@[{anna}] please check",
                mentions = new[] { anna },
                createdByUserId = anna,
                createdAt = DateTimeOffset.UtcNow,
            }));
        }

        var client = Client(buffer.ToArray());
        var fields = await client.GetFromJsonAsync<JsonElement>($"offline/documents/{Document}/index-data");
        Assert.Equal(
            [("Amount", "Number", "120.50"), ("Tags", "MultiSelect", "paid|urgent")],
            fields.GetProperty("fields").EnumerateArray().Select(f => (
                f.GetProperty("fieldName").GetString(), f.GetProperty("dataType").GetString(),
                string.Join('|', f.GetProperty("values").EnumerateArray().Select(v => v.GetString())))));

        var chat = await client.GetFromJsonAsync<JsonElement>($"offline/documents/{Document}/chat");
        var message = Assert.Single(chat.GetProperty("messages").EnumerateArray().ToList());
        Assert.Equal("Anna", message.GetProperty("authorName").GetString());
        Assert.Equal("Anna", Assert.Single(message.GetProperty("mentions").EnumerateArray().ToList()).GetProperty("displayName").GetString());
    }

    [Fact]
    public void The_archives_field_types_are_the_domains_names_in_its_order() =>
        Assert.Equal(Enum.GetNames<FieldDataType>(), OfflineArchiveFieldTypes.Names);

    [Fact]
    public void A_directory_archive_reads_nothing_outside_its_directory()
    {
        var directory = Directory.CreateTempSubdirectory("offline-archive-").FullName;
        var outside = Path.Combine(Path.GetDirectoryName(directory)!, $"secret-{Guid.NewGuid():N}");
        File.WriteAllText(outside, "not the archive's");
        try
        {
            var storage = new DirectoryArchiveStorage(directory);
            Assert.Null(storage.Read($"../{Path.GetFileName(outside)}"));
        }
        finally
        {
            File.Delete(outside);
            Directory.Delete(directory, recursive: true);
        }
    }
}
