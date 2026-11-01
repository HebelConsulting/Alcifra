using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit.Cryptography;
using Alcifra.Api.Imap;

namespace Alcifra.EndToEndTests;

// Sealed IMAP WITHOUT the Encryption Module (#1970 slice 4): the self-service certificate column retired, and the core's
// own registry answers where no module speaks. A reader certificate enrolled there seals what a device that asks for it
// fetches, in-process; a device that does not ask gets plaintext; and a device promised sealing with nothing left to seal
// to is refused rather than served plaintext. The per-test tenant has NO encryption mode and NO module, exactly the
// installation this path exists for. The decisive assertion closes the circle: the holder's own key opens the message.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class RegistrySealedImapTests
{
    private readonly E2EApiFactory _factory;

    public RegistrySealedImapTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_registry_certificate_seals_what_a_sealing_device_fetches_and_its_key_opens_it()
    {
        var (tenantId, userId, api, email, repoName) = await SeedUserWithDocumentAsync();
        using var _api = api;
        using var identity = Identity(email);
        await _factory.EnrolReaderCertificateAsync(tenantId, userId, identity.ExportCertificatePem());

        var sealing = await PasswordOfAsync(api, encryptMail: true);
        var plain = await PasswordOfAsync(api, encryptMail: false);
        var port = ((ImapServer)_factory.Services.GetService(typeof(ImapServer))!).BoundPort!.Value;

        // The sealing device is served ciphertext...
        using (var client = new ImapClient())
        {
            using var message = await FirstMessageAsync(client, port, email, sealing, repoName);
            Assert.IsType<ApplicationPkcs7Mime>(message.Body);
            Assert.DoesNotContain("CLEARTEXT-BODY-MARKER", await RawAsync(message), StringComparison.Ordinal);

            // ...and the holder's own key is what opens it.
            using var context = new TemporarySecureMimeContext();
            await context.ImportAsync(new MemoryStream(identity.Export(X509ContentType.Pfx, "circle")), "circle");
            using var decrypted = ((ApplicationPkcs7Mime)message.Body).Decrypt(context);
            using var opened = new MemoryStream();
            await decrypted.WriteToAsync(opened);
            Assert.Contains("CLEARTEXT-BODY-MARKER", Encoding.ASCII.GetString(opened.ToArray()), StringComparison.Ordinal);
            await client.DisconnectAsync(quit: true);
        }

        // A device that does not ask for sealing reads plaintext from the same mailbox.
        using (var client = new ImapClient())
        {
            using var message = await FirstMessageAsync(client, port, email, plain, repoName);
            Assert.Contains("CLEARTEXT-BODY-MARKER", await RawAsync(message), StringComparison.Ordinal);
            await client.DisconnectAsync(quit: true);
        }

        // The certificate withdrawn, the sealing device is refused: promised sealing, it never silently gets plaintext.
        await _factory.WithdrawReaderCertificatesAsync(tenantId, userId);
        using (var client = new ImapClient())
        {
            await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
            await Assert.ThrowsAsync<AuthenticationException>(() => client.AuthenticateAsync(email, sealing));
        }
    }

    private async Task<(Guid TenantId, Guid UserId, HttpClient Api, string Email, string RepoName)> SeedUserWithDocumentAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoName = $"Sealed{Guid.NewGuid():N}"[..12];
        await TestJson.Post(owner, "/api/repositories", new { name = repoName });

        var email = $"sealed-{Guid.NewGuid():N}@e2e.local";
        const string password = "sealed-1234";
        var userId = await _factory.SeedUserAsync(tenantId, email, password, "Sealed User");
        await _factory.GrantTenantAdminAsync(email);
        var api = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        var davPassword = (await TestJson.Post(api, "/api/me/devices", E2EDevices.Any())).GetProperty("password").GetString()!;
        using var dav = _factory.CreateClient();
        using var put = new HttpRequestMessage(HttpMethod.Put, $"/{(global::Alcifra.Presentation.WireNames.WebDavSegment)}/{repoName}/note.eml")
        {
            Content = new ByteArrayContent(Encoding.ASCII.GetBytes(
                "From: Alice <alice@example.test>\r\nSubject: Plain secret\r\n"
                + "Date: Mon, 06 Jul 2026 10:00:00 +0000\r\n\r\nCLEARTEXT-BODY-MARKER\r\n")),
            Headers = { Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}"))) },
        };
        Assert.Equal(HttpStatusCode.Created, (await dav.SendAsync(put)).StatusCode);

        return (tenantId, userId, api, email, repoName);
    }

    private static async Task<string> PasswordOfAsync(HttpClient api, bool encryptMail) =>
        (await TestJson.Post(api, "/api/me/devices", new
        {
            name = $"e2e-{Guid.NewGuid():N}",
            mail = true,
            files = true,
            calendars = true,
            contacts = true,
            encryptMail,
        })).GetProperty("password").GetString()!;

    private static async Task<MimeKit.MimeMessage> FirstMessageAsync(ImapClient client, int port, string email, string password, string repoName)
    {
        await client.ConnectAsync("127.0.0.1", port, SecureSocketOptions.None);
        await client.AuthenticateAsync(email, password);
        var folder = await client.GetFolderAsync(repoName);
        await folder.OpenAsync(FolderAccess.ReadOnly);
        return await folder.GetMessageAsync(0);
    }

    private static async Task<string> RawAsync(MimeKit.MimeMessage message)
    {
        using var raw = new MemoryStream();
        await message.WriteToAsync(raw);
        return Encoding.ASCII.GetString(raw.ToArray());
    }

    // A software identity standing in for a token's key: the registry accepts it because the test tenant allows hardware
    // without attestation (ADR 0963), and it carries what enrolment checks for — key encipherment, e-mail protection and
    // the holder's address.
    private static X509Certificate2 Identity(string email)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN={email}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.KeyEncipherment | System.Security.Cryptography.X509Certificates.X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.4")], critical: false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddEmailAddress(email);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }
}
