using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SimplArchive.ApiClient;
using SimplArchive.Domain.Masks;

namespace SimplArchive.EndToEndTests;

// The Sealed Document mask (owner decision 2026-10-24): the ACL says who may SEE it, the envelope says who may READ
// it. It gets the filing rule every sealed core mask has (the same guard as a password entry's): only bytes shaped as
// a CMS envelope are accepted, a change carries the precondition of what its author saw, and the server serves the
// content as opaque bytes — it never decrypts it.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class SealedDocumentTests
{
    private readonly E2EApiFactory _factory;

    public SealedDocumentTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_sealed_document_takes_only_a_cms_envelope_and_gates_every_change()
    {
        var (clientId, secret, _) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"SD {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children",
            new { name = "Board pack", maskId = WellKnownMaskIds.SealedDocument })).GetProperty("id").GetGuid();

        // PLAINTEXT as the first content is refused, and the refused version is removed rather than left behind.
        var refused = await FileAsync(api, docId, "plaintext, not an envelope"u8.ToArray(), ifMatch: null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Contains("CONTENT_NOT_SEALED", refused.Body);
        Assert.DoesNotContain((await TestJson.Get(api, $"/api/documents/{docId}/versions")).GetProperty("versions")
            .EnumerateArray(), v => v.GetProperty("status").GetString() == "Confirmed");

        // A REAL envelope to a test certificate is accepted as the first version.
        using var certificate = NewCertificate();
        var first = await FileAsync(api, docId, Envelope("board pack v1", certificate), ifMatch: null);
        Assert.True(first.Status is >= HttpStatusCode.OK and < HttpStatusCode.Ambiguous, $"first envelope: {first.Status} {first.Body}");
        Assert.Single((await TestJson.Get(api, $"/api/documents/{docId}/versions")).GetProperty("versions").EnumerateArray().ToList());

        // A change without the precondition is refused (428), as every change of a sealed document is.
        var tag = await DocumentTagAsync(api, docId);
        var ungated = await FileAsync(api, docId, Envelope("board pack v2", certificate), ifMatch: null);
        Assert.Equal(HttpStatusCode.PreconditionRequired, ungated.Status);

        // A plaintext change under the precondition is refused too; with the envelope it is filed.
        var plaintext = await FileAsync(api, docId, "plaintext"u8.ToArray(), tag);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, plaintext.Status);
        var second = await FileAsync(api, docId, Envelope("board pack v2", certificate), tag);
        Assert.True(second.Status is >= HttpStatusCode.OK and < HttpStatusCode.Ambiguous, $"second envelope: {second.Status} {second.Body}");
        Assert.Equal(2, (await TestJson.Get(api, $"/api/documents/{docId}/versions")).GetProperty("versions")
            .EnumerateArray().Count());
    }

    // The SERVING half (ADR 0975): the server holds no key for a sealed document, so it can neither render it nor
    // envelope it again — it serves the stored bytes as the S/MIME message they are, and says so on the resource.
    // That type is what makes the desktop's and the phone's ONE content funnel open it on the device; the web,
    // which holds no key, is told by `contentIsEnveloped` and offers "open it in the desktop or phone app".
    [Fact]
    public async Task A_sealed_document_is_served_as_the_envelope_it_is_and_offers_no_text_door()
    {
        // A PERSON files it, and so is its first audience manager and member (ADR 0983): the content goes only to them.
        var tenantId = await _factory.SeedTenantNamedAsync($"SD{Guid.NewGuid():N}"[..20]);
        var filerEmail = $"filer-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, filerEmail, "sd-1234", "Filer Person", canManageRepositories: true);
        using var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(filerEmail, "sd-1234"));

        var repoId = (await TestJson.Post(api, "/api/repositories", new { name = $"SD {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(api, $"/api/documents/{repoId}/children",
            new { name = "Sealed pack", maskId = WellKnownMaskIds.SealedDocument })).GetProperty("id").GetGuid();

        using var certificate = NewCertificate();
        var envelope = Envelope("the audience-only content", certificate);
        var filed = await FileAsync(api, docId, envelope, ifMatch: null);
        Assert.True(filed.Status is >= HttpStatusCode.OK and < HttpStatusCode.Ambiguous, $"filing: {filed.Status} {filed.Body}");

        var current = (await TestJson.Get(api, $"/api/documents/{docId}/versions"))
            .GetProperty("versions").EnumerateArray().Single();
        Assert.True(current.GetProperty("contentIsEnveloped").GetBoolean());
        Assert.False(current.GetProperty("previewConverted").GetBoolean());

        var links = current.GetProperty("links").EnumerateArray()
            .ToDictionary(l => l.GetProperty("rel").GetString()!, l => l.GetProperty("href").GetString()!);

        // One door, named as the only pair there is; and the words are inside the envelope, so the text door — and
        // the comparison that is both versions' text — are not advertised at all (ADR 0543: absent, not failing).
        Assert.DoesNotContain("plain-download", links.Keys);
        Assert.DoesNotContain("plain-preview", links.Keys);
        Assert.DoesNotContain("text-layout", links.Keys);

        // The download and the preview serve the STORED ENVELOPE, typed as what it is. The type rides on the
        // presigned URL's response override, because the stored object carries whatever the uploader sent.
        Assert.Contains("response-content-type=application/pkcs7-mime",
            Uri.UnescapeDataString(links["download"]), StringComparison.Ordinal);
        Assert.Contains("response-content-type=application/pkcs7-mime",
            Uri.UnescapeDataString(links["preview"]), StringComparison.Ordinal);

        using var plain = new HttpClient();
        using (var download = await plain.GetAsync(links["download"]))
        {
            download.EnsureSuccessStatusCode();
            Assert.Equal("application/pkcs7-mime", download.Content.Headers.ContentType?.MediaType);
            Assert.Equal(envelope, await download.Content.ReadAsByteArrayAsync());
        }

        using (var preview = await plain.GetAsync(links["preview"]))
        {
            preview.EnsureSuccessStatusCode();
            Assert.Equal("application/pkcs7-mime", preview.Content.Headers.ContentType?.MediaType);
            Assert.Equal(envelope, await preview.Content.ReadAsByteArrayAsync());
        }

        // A reader OUTSIDE the audience sees the version — the ACL decides who sees the history — but no content door.
        var readerEmail = $"outside-{Guid.NewGuid():N}@e2e.local";
        var readerId = await _factory.SeedUserAsync(tenantId, readerEmail, "sd-1234", "Outside Reader");
        (await api.PutAsJsonAsync($"/api/documents/{repoId}/acl-entries/users/{readerId}", new { canSee = true, canReadContent = true }))
            .EnsureSuccessStatusCode();
        using var reader = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(readerEmail, "sd-1234"));
        var seen = (await TestJson.Get(reader, $"/api/documents/{docId}/versions")).GetProperty("versions").EnumerateArray().Single();
        var readerRels = seen.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("rel").GetString()).ToList();
        Assert.DoesNotContain("download", readerRels);
        Assert.DoesNotContain("preview", readerRels);
        var row = (await TestJson.Get(reader, $"/api/documents/{repoId}/children")).GetProperty("children").EnumerateArray()
            .Single(c => c.GetProperty("id").GetGuid() == docId);
        Assert.Equal("not-you", row.GetProperty("encryptedForCaller").GetString());
    }

    // A re-seal is an AUDIENCE MANAGER's act (ADR 0983, replacing ADR 0975's author rule), decided by the server: the
    // document resource says whether this caller may, and the finalize refuses the re-seal kind from anybody else even
    // when the caller composes the request. The filer is the first manager; a manager makes another.
    [Fact]
    public async Task Only_an_audience_manager_may_re_seal_and_the_filer_is_the_first()
    {
        var tenantId = await _factory.SeedTenantNamedAsync($"SD{Guid.NewGuid():N}"[..20]);

        var authorEmail = $"author-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, authorEmail, "sd-1234", "Author Person", canManageRepositories: true);
        using var author = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(authorEmail, "sd-1234"));

        var readerEmail = $"reader-{Guid.NewGuid():N}@e2e.local";
        var readerId = await _factory.SeedUserAsync(tenantId, readerEmail, "sd-1234", "Reader Person");
        using var reader = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(readerEmail, "sd-1234"));

        var repoId = (await TestJson.Post(author, "/api/repositories", new { name = $"SD {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();
        var docId = (await TestJson.Post(author, $"/api/documents/{repoId}/children",
            new { name = "Board minutes", maskId = WellKnownMaskIds.SealedDocument })).GetProperty("id").GetGuid();

        // The reader may see and edit the document's content — so the only thing standing between them and a re-seal
        // is being a MANAGER, which is exactly what this test is about. Granted on the REPOSITORY, the document's
        // governing ACL scope (ADR 0183).
        (await author.PutAsJsonAsync($"/api/documents/{repoId}/acl-entries/users/{readerId}",
            new { canSee = true, canReadContent = true, canEditContent = true })).EnsureSuccessStatusCode();

        using var certificate = NewCertificate();
        Assert.True((await FileAsync(author, docId, Envelope("first audience", certificate), ifMatch: null)).Status
            is >= HttpStatusCode.OK and < HttpStatusCode.Ambiguous);

        var (authorBody, authorTag) = await ReadAsync(author, docId);
        Assert.True(authorBody.GetProperty("canChangeAudience").GetBoolean());
        var (readerBody, _) = await ReadAsync(reader, docId);
        Assert.False(readerBody.GetProperty("canChangeAudience").GetBoolean());

        // A composed request gets the server's answer, not the toolbar's: 403, by name.
        var refused = await ResealAsync(reader, docId, Envelope("a new audience", certificate), authorTag);
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Contains("AUDIENCE_CHANGE_REQUIRES_MANAGER", refused.Body);

        // The filer manages; making the reader a manager too is one PUT under the document's tag.
        var audience = await TestJson.Get(author, $"/api/documents/{docId}/audience");
        Assert.Equal(authorTag, audience.GetProperty("eTag").GetString());
        Assert.Contains(audience.GetProperty("candidates").EnumerateArray(), c => c.GetProperty("userId").GetString() == readerId.ToString());
        var authorId = audience.GetProperty("members").EnumerateArray().Single().GetProperty("userId").GetGuid();
        using (var put = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{docId}/audience")
        {
            Content = JsonContent.Create(new { members = new object[] { new { userId = authorId, isManager = true }, new { userId = readerId, isManager = true } } }),
        })
        {
            put.Headers.TryAddWithoutValidation("If-Match", authorTag);
            using var answered = await author.SendAsync(put);
            Assert.True(answered.IsSuccessStatusCode, $"audience PUT: {(int)answered.StatusCode} {await answered.Content.ReadAsStringAsync()}");
        }

        var (nowReader, readerTag) = await ReadAsync(reader, docId);
        Assert.True(nowReader.GetProperty("canChangeAudience").GetBoolean());
        var filed = await ResealAsync(reader, docId, Envelope("a new audience", certificate), readerTag);
        Assert.True(filed.Status is >= HttpStatusCode.OK and < HttpStatusCode.Ambiguous, $"re-seal: {filed.Status} {filed.Body}");
        Assert.Equal(2, (await TestJson.Get(author, $"/api/documents/{docId}/versions")).GetProperty("versions")
            .EnumerateArray().Count());
    }

    private static async Task<(JsonElement Body, string ETag)> ReadAsync(HttpClient api, Guid documentId)
    {
        using var read = await api.GetAsync($"/api/documents/{documentId}");
        var body = await read.Content.ReadAsStringAsync();
        Assert.True(read.IsSuccessStatusCode, $"document read: {(int)read.StatusCode} {body}");
        return (JsonDocument.Parse(body).RootElement.Clone(), read.Headers.ETag!.Tag);
    }

    // What a client files: the CLIENT's own sealer, so the bytes under test are the ones the desktop and phone
    // produce — a hand-built CMS in the test could pass while the real producer's shape is refused at the door.
    private static byte[] Envelope(string text, X509Certificate2 certificate) =>
        SealedDocument.Seal(System.Text.Encoding.UTF8.GetBytes(text), "text/plain", [certificate]);

    // A re-seal: a version, its envelope, and a finalize that SAYS it is a re-seal and carries the document's ETag.
    private static async Task<(HttpStatusCode Status, string Body)> ResealAsync(
        HttpClient api, Guid documentId, byte[] bytes, string ifMatch)
    {
        var created = await TestJson.Post(api, $"/api/documents/{documentId}/versions", new { fileExtension = ".p7m" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
        }

        using var finalize = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/versions/{created.GetProperty("id").GetGuid()}")
        {
            Content = JsonContent.Create(new { commentKind = 2 }),   // ResealedAfterAccessChange (ADR 0964)
        };
        finalize.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var response = await api.SendAsync(finalize);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static X509Certificate2 NewCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=sealed-{Guid.NewGuid():N}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }

    private static async Task<string> DocumentTagAsync(HttpClient api, Guid documentId)
    {
        using var read = await api.GetAsync($"/api/documents/{documentId}");
        read.EnsureSuccessStatusCode();
        return read.Headers.ETag!.Tag;
    }

    private static async Task<(HttpStatusCode Status, string Body)> FileAsync(
        HttpClient api, Guid documentId, byte[] bytes, string? ifMatch)
    {
        var created = await TestJson.Post(api, $"/api/documents/{documentId}/versions", new { fileExtension = ".p7m" });
        var versionId = created.GetProperty("id").GetGuid();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
        }

        using var finalize = new HttpRequestMessage(HttpMethod.Put, $"/api/documents/{documentId}/versions/{versionId}")
        {
            Content = JsonContent.Create(new { }),
        };
        if (ifMatch is not null)
        {
            finalize.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        using var response = await api.SendAsync(finalize);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
