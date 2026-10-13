using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.ApiClient.Offline;

namespace SimplArchive.UnitTests.ApiClient;

// The cache writer (SimplArchiveMobile #5, ADR 0924): what a client saw online, kept in the export layout, sealed file by
// file, read back by the SAME responder an export goes through. A stand-in seal (AES-GCM with a fixed test key) shows
// that nothing lands on disk in the clear and that every read goes through the open hook.
public sealed class ArchiveWriterTests : IDisposable
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
    private readonly string _directory = Directory.CreateTempSubdirectory("archive-writer-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static byte[] Seal(string path, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(Key, 16))
        {
            aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(path));
        }

        return [.. nonce, .. tag, .. cipher];
    }

    private static byte[] Open(string path, byte[] sealedBytes)
    {
        var plain = new byte[sealedBytes.Length - 28];
        using var aes = new AesGcm(Key, 16);
        aes.Decrypt(sealedBytes.AsSpan(0, 12), sealedBytes.AsSpan(28), sealedBytes.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(path));
        return plain;
    }

    private Task<ArchiveWriter> WriterAsync() =>
        ArchiveWriter.OpenAsync(_directory, "Offline copies", Seal, (path, bytes, _) => Task.FromResult(Open(path, bytes)));

    private async Task<HttpClient> ReaderAsync() =>
        new(new OfflineResponder(await OfflineArchive.OpenAsync(new DirectoryArchiveStorage(_directory, Open)))) { BaseAddress = new Uri(OfflineAddress.Root) };

    private static readonly Guid Contracts = Guid.NewGuid();
    private static readonly Guid Invoices = Guid.NewGuid();
    private static readonly Guid Deal = Guid.NewGuid();
    private static readonly Guid Anna = Guid.NewGuid();
    private static readonly byte[] DealContent = Encoding.UTF8.GetBytes("the deal, plainly");

    private static void KeepTheDeal(ArchiveWriter writer)
    {
        writer.PutFolder(new CachedDocument(Contracts, null, "Contracts", string.Empty, "Admin", DateTimeOffset.UtcNow));
        writer.Put(
            new CachedDocument(Deal, Contracts, "Deal", "Contract", "Admin", DateTimeOffset.UtcNow),
            [new CachedVersion(Guid.NewGuid(), 1, "2026-10-13", DateTimeOffset.UtcNow, "Admin", ".txt", DealContent)],
            [new CachedField("Party", "Text", ["ACME"]), new CachedField("Tags", "MultiSelect", ["signed", "2026"])],
            [new CachedMessage(Guid.NewGuid(), null, $"@[{Anna}] please countersign", "Admin", DateTimeOffset.UtcNow, [(Anna, "Anna")])]);
    }

    [Fact]
    public async Task What_was_kept_reads_back_through_the_responder_as_it_was_seen()
    {
        var writer = await WriterAsync();
        KeepTheDeal(writer);
        writer.PutFolder(new CachedDocument(Invoices, null, "Invoices", string.Empty, "Admin", DateTimeOffset.UtcNow));
        writer.Flush();

        var reader = await ReaderAsync();
        var repositories = await reader.GetFromJsonAsync<JsonElement>("offline/repositories");
        Assert.Equal(["Contracts", "Invoices"], repositories.GetProperty("repositories").EnumerateArray().Select(r => r.GetProperty("name").GetString()));

        var contents = await reader.GetFromJsonAsync<JsonElement>($"offline/documents/{Contracts}/children");
        var deal = Assert.Single(contents.GetProperty("children").EnumerateArray().ToList());
        Assert.Equal(("Deal", false, "Contract"), (deal.GetProperty("name").GetString(), deal.GetProperty("isFolder").GetBoolean(), deal.GetProperty("documentType").GetString()));

        var fields = await reader.GetFromJsonAsync<JsonElement>($"offline/documents/{Deal}/index-data");
        Assert.Equal([("Party", "Text", "ACME"), ("Tags", "MultiSelect", "signed|2026")], fields.GetProperty("fields").EnumerateArray()
            .Select(f => (f.GetProperty("fieldName").GetString(), f.GetProperty("dataType").GetString(), string.Join('|', f.GetProperty("values").EnumerateArray().Select(v => v.GetString())))));

        var chat = await reader.GetFromJsonAsync<JsonElement>($"offline/documents/{Deal}/chat");
        var mention = chat.GetProperty("messages")[0].GetProperty("mentions")[0];
        Assert.Equal((Anna, "Anna"), (mention.GetProperty("userId").GetGuid(), mention.GetProperty("displayName").GetString()));

        var versions = await reader.GetFromJsonAsync<JsonElement>($"offline/documents/{Deal}/versions");
        var download = versions.GetProperty("versions")[0].GetProperty("links").EnumerateArray().First(l => l.GetProperty("rel").GetString() == "download").GetProperty("href").GetString()!;
        Assert.Equal(DealContent, await reader.GetByteArrayAsync(download.TrimStart('/')));
    }

    // Owner's test 2026-10-13: the cache did not show the user's own folder first. The server tells the personal
    // space apart (its own rel, left out of the repositories); the cache now records which root it is and answers alike.
    [Fact]
    public async Task The_personal_space_is_answered_as_such_and_not_among_the_repositories()
    {
        var writer = await WriterAsync();
        KeepTheDeal(writer);
        var mine = Guid.NewGuid();
        writer.PutFolder(new CachedDocument(mine, null, "Anna Meyer", string.Empty, "Anna", DateTimeOffset.UtcNow));
        writer.PersonalRoot = mine;
        writer.Flush();

        var reader = await ReaderAsync();
        var repositories = await reader.GetFromJsonAsync<JsonElement>("offline/repositories");
        Assert.DoesNotContain("Anna Meyer", repositories.GetProperty("repositories").EnumerateArray().Select(r => r.GetProperty("name").GetString()));

        // POST, as the server's link says (get-or-create there; only get here).
        using var personal = await reader.PostAsync("offline/personal", null);
        Assert.Equal(mine, (await personal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, (await reader.PostAsync("offline/repositories", null)).StatusCode);

        // Its type survives the folder chain of a document kept later, which carries none.
        writer.PutFolder(new CachedDocument(mine, null, "Anna Meyer", "User folder", "Anna", DateTimeOffset.UtcNow));
        writer.PutFolder(new CachedDocument(mine, null, "Anna Meyer", string.Empty, string.Empty, default));
        writer.Flush();
        using var again = await (await ReaderAsync()).PostAsync("offline/personal", null);
        Assert.Equal("User folder", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("documentType").GetString());

        var reopened = await WriterAsync();   // a later run keeps knowing it
        reopened.Flush();
        Assert.Equal(mine, (await OfflineArchive.OpenAsync(new DirectoryArchiveStorage(_directory, Open))).PersonalRoot);
    }

    [Fact]
    public async Task Kept_notifications_read_through_the_shared_client_and_point_at_a_kept_document()
    {
        var writer = await WriterAsync();
        KeepTheDeal(writer);
        writer.PutNotifications(
        [
            new CachedNotification(Guid.NewGuid(), "ChatMentioned", "Anna mentioned you", "in Deal", Deal, DateTimeOffset.UtcNow, IsRead: false),
            new CachedNotification(Guid.NewGuid(), "DocumentReminder", "Reminder", "elsewhere", Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1), IsRead: true),
        ]);
        writer.Flush();

        var responder = new OfflineResponder(await OfflineArchive.OpenAsync(new DirectoryArchiveStorage(_directory, Open)));
        var list = await new SimplArchive.ApiClient.NotificationsClient(SimplArchive.ApiClient.ApiCore.FromArchive(responder)).GetNotificationsAsync();

        Assert.Equal(1, list.UnreadCount);
        Assert.Null(list.ReadAllHref);   // nothing is marked read offline
        Assert.Equal(["Anna mentioned you", "Reminder"], list.Items.Select(n => n.Title));
        Assert.NotNull(list.Items[0].Href("document"));   // the deal was kept
        Assert.Null(list.Items[1].Href("document"));      // its document was not
    }

    [Fact]
    public async Task A_kept_envelope_is_served_as_an_envelope()
    {
        var writer = await WriterAsync();
        writer.PutFolder(new CachedDocument(Contracts, null, "Contracts", string.Empty, "Admin", DateTimeOffset.UtcNow));
        var envelope = Encoding.UTF8.GetBytes("pretend CMS");
        writer.Put(new CachedDocument(Deal, Contracts, "Sealed", string.Empty, "Admin", DateTimeOffset.UtcNow),
            [new CachedVersion(Guid.NewGuid(), 1, null, DateTimeOffset.UtcNow, "Admin", ".pdf", envelope, "application/pkcs7-mime")], [], []);
        writer.Flush();

        var reader = await ReaderAsync();
        var versions = await reader.GetFromJsonAsync<JsonElement>($"offline/documents/{Deal}/versions");
        var download = versions.GetProperty("versions")[0].GetProperty("links").EnumerateArray().First(l => l.GetProperty("rel").GetString() == "download").GetProperty("href").GetString()!;
        using var response = await reader.GetAsync(download.TrimStart('/'));
        Assert.Equal("application/pkcs7-mime", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Nothing_lands_on_the_device_in_the_clear()
    {
        var writer = await WriterAsync();
        KeepTheDeal(writer);
        writer.Flush();

        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain("the deal, plainly", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ACME", text, StringComparison.Ordinal);
            Assert.DoesNotContain("countersign", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_later_run_adds_to_the_cache_and_a_removed_document_frees_its_content()
    {
        var first = await WriterAsync();
        KeepTheDeal(first);
        first.Flush();

        // Another run of the app: the cache is read back, and grows.
        var later = await WriterAsync();
        Assert.True(later.Contains(Deal));
        later.PutFolder(new CachedDocument(Invoices, null, "Invoices", string.Empty, "Admin", DateTimeOffset.UtcNow));
        later.Flush();
        var repositories = await (await ReaderAsync()).GetFromJsonAsync<JsonElement>("offline/repositories");
        Assert.Equal(2, repositories.GetProperty("repositories").GetArrayLength());
        var versions = await (await ReaderAsync()).GetFromJsonAsync<JsonElement>($"offline/documents/{Deal}/versions");
        Assert.Equal(1, versions.GetProperty("versions").GetArrayLength());   // its content was not lost on reload

        later.Remove(Deal);
        later.Flush();
        Assert.True(later.Prune() > 0);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_directory, "blobs")));
    }
}
