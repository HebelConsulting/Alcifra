using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using MimeKit;
using MimeKit.Cryptography;

namespace SimplArchive.EndToEndTests;

// The offline export on a tenant that refuses plaintext doors (ADR 0928; #1759 slice 4): only where the Encryption
// Module allows it (the TestModule's hook, set per test), content as the envelope the reader's own key opens, and,
// when the module says so, index data and chat sealed to the reader as well.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class StrictOfflineExportTests
{
    private const string Marker = "STRICT-OFFLINE-MARKER";
    private readonly E2EApiFactory _factory;

    public StrictOfflineExportTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Reader(HttpClient Api, Guid DocumentId, byte[] Pkcs12);

    private async Task<Reader> ReaderWithADocumentAsync()
    {
        var tenantId = await _factory.SeedTenantNamedAsync(E2EApiFactory.StrictTenantName);
        var email = $"offline-{Guid.NewGuid():N}@e2e.local";
        var userId = await _factory.SeedUserAsync(tenantId, email, "of-1234", "Offline", canManageRepositories: true, isTenantAdmin: true);
        var (pem, pkcs12) = Certificate(email);
        await _factory.EnrolReaderCertificateAsync(tenantId, userId, pem);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "of-1234"));

        var repository = (await TestJson.Post(api, "/api/repositories", new { name = $"repo-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var documentId = (await TestJson.Post(api, $"/api/documents/{repository}/children", new { name = $"doc-{Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        await UploadAsync(api, documentId, Encoding.ASCII.GetBytes($"{Marker} on the road\n"));
        await TestJson.Post(api, $"/api/documents/{documentId}/chat", new { body = $"{Marker} in the chat" });
        return new Reader(api, documentId, pkcs12);
    }

    private static (string Pem, byte[] Pkcs12) Certificate(string email)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={email}, E={email}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.KeyEncipherment | System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return (certificate.ExportCertificatePem(), certificate.Export(X509ContentType.Pkcs12));
    }

    private static async Task UploadAsync(HttpClient api, Guid documentId, byte[] plaintext)
    {
        var version = await TestJson.Post(api, $"/api/documents/{documentId}/versions", new { fileExtension = ".txt" });
        var body = plaintext;
        object finalize = new { };
        if (version.TryGetProperty("encryption", out var encryption) && encryption.ValueKind == JsonValueKind.Object)
        {
            var dek = RandomNumberGenerator.GetBytes(32);
            var blob = new byte[12 + plaintext.Length + 16];
            RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
            using (var aes = new AesGcm(dek, 16))
            {
                aes.Encrypt(blob.AsSpan(0, 12), plaintext, blob.AsSpan(12, plaintext.Length), blob.AsSpan(^16..));
            }

            using var kek = RSA.Create();
            kek.ImportFromPem(encryption.GetProperty("publicKeyPem").GetString()!);
            body = blob;
            finalize = new
            {
                wrappedDek = Convert.ToBase64String(kek.Encrypt(dek, RSAEncryptionPadding.OaepSHA256)),
                kekGeneration = encryption.GetProperty("kekGeneration").GetString()!,
            };
        }

        using var anonymous = new HttpClient();
        Assert.True((await anonymous.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(body))).IsSuccessStatusCode);
        var self = version.GetProperty("links").EnumerateArray().First(l => l.GetProperty("rel").GetString() == "self").GetProperty("href").GetString()!;
        await TestJson.Put(api, self, finalize);
    }

    private static async Task<byte[]> OpenAsync(byte[] smime, byte[] pkcs12)
    {
        var message = await MimeMessage.LoadAsync(new MemoryStream(smime));
        using var context = new TemporarySecureMimeContext();
        await context.ImportAsync(new MemoryStream(pkcs12), string.Empty);
        var part = Assert.IsAssignableFrom<MimePart>(Assert.IsAssignableFrom<ApplicationPkcs7Mime>(message.Body).Decrypt(context));
        using var opened = new MemoryStream();
        await part.Content!.DecodeToAsync(opened);
        return opened.ToArray();
    }

    private async Task<HttpResponseMessage> ExportAsync(Reader reader, string? policy)
    {
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_OFFLINE", policy);
        try
        {
            var href = (await TestJson.Get(reader.Api, $"/api/documents/{reader.DocumentId}")).GetProperty("links").EnumerateArray()
                .Single(l => l.GetProperty("rel").GetString() == "offlineExport").GetProperty("href").GetString()!;
            var response = await reader.Api.GetAsync(href);
            await response.Content.LoadIntoBufferAsync();
            return response;
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_OFFLINE", null);
        }
    }

    private static string Text(ZipArchive zip, string path)
    {
        using var reader = new StreamReader(zip.GetEntry(path)!.Open());
        return reader.ReadToEnd();
    }

    private static byte[] Bytes(ZipArchive zip, string path)
    {
        using var stream = new MemoryStream();
        zip.GetEntry(path)!.Open().CopyTo(stream);
        return stream.ToArray();
    }

    [Fact]
    public async Task Without_the_modules_permission_a_strict_tenant_refuses_the_offline_export()
    {
        var reader = await ReaderWithADocumentAsync();

        var refused = await ExportAsync(reader, policy: null);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("OFFLINE_EXPORT_NOT_ALLOWED_ON_THIS_TENANT", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Where_the_module_allows_it_content_travels_as_the_envelope_the_readers_key_opens()
    {
        var reader = await ReaderWithADocumentAsync();

        using var response = await ExportAsync(reader, "allow");
        response.EnsureSuccessStatusCode();
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);

        var version = JsonDocument.Parse(Text(zip, "tree/versions.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries).Single()).RootElement;
        Assert.Equal("application/pkcs7-mime", version.GetProperty("contentType").GetString());
        var envelope = Bytes(zip, $"blobs/{version.GetProperty("blobRef").GetString()}");
        Assert.DoesNotContain(Marker, Encoding.ASCII.GetString(envelope), StringComparison.Ordinal);   // nothing readable on the way out
        Assert.Equal($"{Marker} on the road\n", Encoding.ASCII.GetString(await OpenAsync(envelope, reader.Pkcs12)));
        Assert.Contains(Marker, Text(zip, "tree/chat.jsonl"), StringComparison.Ordinal);   // not sealed unless the module says so
    }

    [Fact]
    public async Task Where_the_module_seals_them_index_data_and_chat_travel_sealed_to_the_reader()
    {
        var reader = await ReaderWithADocumentAsync();

        using var response = await ExportAsync(reader, "seal");
        response.EnsureSuccessStatusCode();
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);

        Assert.Null(zip.GetEntry("tree/chat.jsonl"));
        Assert.Null(zip.GetEntry("tree/index-data.jsonl"));
        var sealedPart = Bytes(zip, "tree/sealed.p7m");
        Assert.DoesNotContain(Marker, Encoding.ASCII.GetString(sealedPart), StringComparison.Ordinal);
        var files = JsonSerializer.Deserialize<Dictionary<string, string>>(await OpenAsync(sealedPart, reader.Pkcs12))!;
        Assert.Contains(Marker, files["tree/chat.jsonl"], StringComparison.Ordinal);
        Assert.True(files.ContainsKey("tree/index-data.jsonl"));
        Assert.True(JsonDocument.Parse(Text(zip, "manifest.json")).RootElement.GetProperty("sealedIndexDataAndChat").GetBoolean());
    }
}
