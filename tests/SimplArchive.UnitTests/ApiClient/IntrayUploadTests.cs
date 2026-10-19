using SimplArchive.ApiClient;

namespace SimplArchive.UnitTests.ApiClient;

// Filing into the personal Inbox (the server intray) as the shared library does it for the desktop and, since the
// phone's share sheet (Mobile slice 4), for the phone: ask for an upload address, PUT the bytes there, then follow
// `processed` so the ingest runs at once. Every address is one the server advertised.
[Collection(ApiClientSettingsCollection.Name)]
public sealed class IntrayUploadTests : ApiClientSettingsTest
{
    [Fact]
    public async Task An_upload_asks_for_an_address_puts_the_bytes_there_and_follows_processed()
    {
        LoopbackServer? server = null;
        server = new LoopbackServer((method, path, _) => (method, path) switch
        {
            ("GET", "/api") => (200, """{"links":[{"rel":"intray","href":"/api/intray"}]}"""),
            ("POST", "/api/intray") => (200, $$"""{"uploadUrl":"{{server!.BaseUrl}}storage/object-1","links":[{"rel":"processed","href":"/api/intray/object-1/processed"}]}"""),
            ("PUT", "/storage/object-1") => (200, string.Empty),
            ("POST", "/api/intray/object-1/processed") => (204, string.Empty),
            _ => (404, "{}"),
        });
        using var _ = server;
        ApiClientSettings.ApiRootUrl = server.BaseUrl.TrimEnd('/');

        await new IntrayApi(new ApiCore("access-1")).UploadAsync("Shared.pdf", "%PDF-1.7"u8.ToArray());

        Assert.Equal(
            ["GET /api", "POST /api/intray", "PUT /storage/object-1", "POST /api/intray/object-1/processed"],
            server.Requests.Select(r => $"{r.Method} {r.Path}"));
        Assert.Contains("Shared.pdf", server.Requests[1].Body, StringComparison.Ordinal);
        Assert.Equal("%PDF-1.7", server.Requests[2].Body);
    }

    [Fact]
    public async Task A_listed_item_without_a_download_rel_lists_with_an_empty_address()
    {
        // A strict tenant offers no plaintext door, so its items carry no `download` (#1376); the listing must still read.
        using var server = new LoopbackServer((method, path, _) => (method, path) switch
        {
            ("GET", "/api") => (200, """{"links":[{"rel":"intray","href":"/api/intray"}]}"""),
            ("GET", "/api/intray") => (200, """{"items":[{"name":"Sealed.pdf","size":12,"links":[{"rel":"preview","href":"/api/intray/Sealed.pdf/preview","method":"GET"},{"rel":"move","href":"/api/intray/Sealed.pdf/parent","method":"PUT"}]}],"links":[]}"""),
            _ => (404, "{}"),
        });
        ApiClientSettings.ApiRootUrl = server.BaseUrl.TrimEnd('/');

        var item = Assert.Single((await new IntrayApi(new ApiCore("access-1")).ListAsync()).Items);

        Assert.Equal("Sealed.pdf", item.Name);
        Assert.Equal(string.Empty, item.DownloadUrl);
        Assert.Equal("/api/intray/Sealed.pdf/parent", item.MoveUrl);
    }
}
