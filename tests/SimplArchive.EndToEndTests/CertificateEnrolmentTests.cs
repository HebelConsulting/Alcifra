using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SimplArchive.ModuleAbi;

namespace SimplArchive.EndToEndTests;

// Certificate enrolment owned by the core (ADR 0936), end to end: offered only while an active module licenses it,
// configured by the tenant's own settings, filed into the "Reader certificates" folder, registered in the same
// transaction (ADR 0935), and answered as the reader's certificates. The policy is replaced only under If-Match.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class CertificateEnrolmentTests
{
    private const string LicensesVariable = "SIMPLARCHIVE_TESTMODULE_LICENSES_ENROLMENT";
    private const string VerifyKeyVariable = "SIMPLARCHIVE_TESTMODULE_VERIFY_KEY";

    private readonly E2EApiFactory _factory;

    public CertificateEnrolmentTests(E2EApiFactory factory) => _factory = factory;

    private sealed record Rig(HttpClient Owner, HttpClient Admin, HttpClient Member, Guid TenantId, Guid RepoId)
    {
        public string MemberEmail { get; init; } = string.Empty;
    }

    [Fact]
    public async Task A_member_enrols_their_own_certificate_and_is_addressed_by_it()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            var root = Links(await TestJson.Get(rig.Member, "/api"));
            Assert.Equal("/api/certificates", root["certificates"]);
            Assert.Equal("/api/certificate-policy", root["certificatePolicy"]);
            Assert.Equal("/api/certificates", root["encryption:certificates"]);   // the alias, for one release

            // Self-service is off until the tenant switches it on, and the self-enrolment list says so.
            var pem = SelfSigned("member");
            using (var refused = await rig.Member.PostAsJsonAsync(root["certificates"], new { certificatePem = pem, label = "Card" }))
            {
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
                Assert.Contains("CERTIFICATE_SELF_SERVICE_DISABLED", await refused.Content.ReadAsStringAsync());
            }

            await SetCertificateSettingsAsync(rig, selfService: true, parent: rig.RepoId);

            var enrolled = await TestJson.Post(rig.Member, root["certificates"], new { certificatePem = pem, label = "Card" });
            Assert.Equal("Card", enrolled.GetProperty("label").GetString());

            var listed = Assert.Single((await TestJson.Get(rig.Member, root["certificates"])).EnumerateArray().ToList());
            Assert.True(listed.GetProperty("usable").GetBoolean());

            // The core's registry answers who the member is addressed to.
            var me = Links(await TestJson.Get(rig.Member, "/api/me"));
            var readers = await TestJson.Get(rig.Member, me["readerCertificates"]);
            var entry = Assert.Single(readers.GetProperty("certificates").EnumerateArray().ToList());
            Assert.Equal(pem.Trim(), entry.GetProperty("pem").GetString()!.Trim());

            using (var again = await rig.Member.PostAsJsonAsync(root["certificates"], new { certificatePem = pem, label = "Card" }))
            {
                Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    [Fact]
    public async Task The_policy_is_replaced_only_by_the_tag_the_administrator_saw()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            await SetCertificateSettingsAsync(rig, selfService: false, parent: rig.RepoId);
            var href = Links(await TestJson.Get(rig.Admin, "/api"))["certificatePolicy"];

            using var read = await rig.Admin.GetAsync(href);
            read.EnsureSuccessStatusCode();
            var seen = read.Headers.ETag!.Tag;
            var policy = new { minimumKeySizeBits = 3072 };

            using (var missing = await rig.Admin.PutAsJsonAsync(href, policy))
            {
                Assert.Equal((HttpStatusCode)428, missing.StatusCode);
            }

            using (var first = await PutWithTagAsync(rig.Admin, href, policy, seen))
            {
                first.EnsureSuccessStatusCode();
                Assert.NotEqual(seen, first.Headers.ETag!.Tag);
            }

            using (var stale = await PutWithTagAsync(rig.Admin, href, new { minimumKeySizeBits = 4096 }, seen))
            {
                Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
            }

            Assert.Equal(3072, (await TestJson.Get(rig.Member, href)).GetProperty("minimumKeySizeBits").GetInt32());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    [Fact]
    public async Task An_unattended_SETUP_ACCOUNT_sets_the_enrolment_settings_and_a_member_may_not()
    {
        // The kiosk's nightly seed signs in as a service account granted CanManageModules (ADR 0870) and sets where the
        // reader-certificate folder goes. These were module settings, which that right sets; the tenant settings refuse
        // every service account, which is why they are a resource of their own (ADR 0936).
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            var created = await TestJson.Post(rig.Admin, "/api/service-accounts", new { name = $"setup-{Guid.NewGuid():N}", canManageModules = true });
            using var setup = _factory.CreateAuthedClient(await _factory.GetTokenAsync(
                created.GetProperty("clientId").GetString()!, created.GetProperty("clientSecret").GetString()!));

            await SetCertificateSettingsAsync(setup, selfService: true, parent: rig.RepoId);

            var read = await TestJson.Get(rig.Admin, "/api/certificate-settings");
            Assert.True(read.GetProperty("selfServiceEnabled").GetBoolean());
            Assert.Equal(rig.RepoId, read.GetProperty("parentFolderId").GetGuid());
            Assert.StartsWith("Certificates ", read.GetProperty("parentFolderName").GetString());   // what a form shows

            using var refused = await rig.Member.GetAsync("/api/certificate-settings");
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    [Fact]
    public async Task An_unattended_SETUP_ACCOUNT_enrols_a_certificate_for_a_named_member()
    {
        // The first enrolment creates the "Reader certificates" folder, and the setup account is its creator: a service
        // account, not a user. Found live (Messenger demo, 2026-10-16): the folder was written with NO creator, the
        // CK_Documents_ExactlyOneCreator check refused it, and every unattended enrolment answered 500.
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        Environment.SetEnvironmentVariable(LicensesVariable, "1");
        try
        {
            var rig = await RigAsync(vendorKey);
            var created = await TestJson.Post(rig.Admin, "/api/service-accounts", new { name = $"setup-{Guid.NewGuid():N}", canManageModules = true });
            using var setup = _factory.CreateAuthedClient(await _factory.GetTokenAsync(
                created.GetProperty("clientId").GetString()!, created.GetProperty("clientSecret").GetString()!));
            await SetCertificateSettingsAsync(setup, selfService: false, parent: rig.RepoId);

            var enrolled = await TestJson.Post(setup, "/api/certificates", new { certificatePem = SelfSigned("member"), label = "Card", holder = rig.MemberEmail });
            Assert.Equal("Card", enrolled.GetProperty("label").GetString());

            // The member is addressed by it, and the listing for the holder shows it.
            Assert.Single((await TestJson.Get(rig.Member, "/api/certificates")).EnumerateArray().ToList());
            Assert.Single((await TestJson.Get(setup, $"/api/certificates?holder={Uri.EscapeDataString(rig.MemberEmail)}")).EnumerateArray().ToList());
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicensesVariable, null);
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    [Fact]
    public async Task Without_a_licensing_module_enrolment_does_not_exist()
    {
        var email = $"noenrol-core-{Guid.NewGuid():N}@e2e.local";
        const string password = "noenrol12345";
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        await _factory.SeedUserAsync(tenantId, email, password, "No Enrolment");
        using var member = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

        Assert.DoesNotContain("certificates", Links(await TestJson.Get(member, "/api")).Keys);
        using var response = await member.GetAsync("/api/certificates");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PutWithTagAsync(HttpClient client, string href, object body, string tag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, href) { Content = JsonContent.Create(body) };
        request.Headers.IfMatch.Add(new EntityTagHeaderValue(tag));
        return await client.SendAsync(request);
    }

    private static Task SetCertificateSettingsAsync(Rig rig, bool selfService, Guid parent) =>
        SetCertificateSettingsAsync(rig.Admin, selfService, parent);

    private static async Task SetCertificateSettingsAsync(HttpClient client, bool selfService, Guid parent)
    {
        var href = Links(await TestJson.Get(client, "/api"))["certificateSettings"];
        using var read = await client.GetAsync(href);
        await TestJson.Read(read);
        using var write = await PutWithTagAsync(client, href,
            new { selfServiceEnabled = selfService, parentFolderId = parent }, read.Headers.ETag!.Tag);
        await TestJson.Read(write);
    }

    private static string SelfSigned(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.ExportCertificatePem();
    }

    private static IReadOnlyDictionary<string, string> Links(JsonElement resource) =>
        resource.TryGetProperty("links", out var links)
            ? links.EnumerateArray()
                .GroupBy(l => l.GetProperty("rel").GetString()!)
                .ToDictionary(g => g.Key, g => g.First().GetProperty("href").GetString()!)
            : new Dictionary<string, string>();

    private async Task<Rig> RigAsync(ECDsa vendorKey)
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Certificates {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var adminEmail = $"enrol-admin-{Guid.NewGuid():N}@e2e.local";
        var memberEmail = $"enrol-member-{Guid.NewGuid():N}@e2e.local";
        const string password = "enrolment1234";
        await _factory.SeedUserAsync(tenantId, adminEmail, password, "Enrolment Admin", isTenantAdmin: true, canManageServiceAccounts: true);
        await _factory.SeedUserAsync(tenantId, memberEmail, password, "Enrolment Member");
        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, password));
        var member = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(memberEmail, password));

        var license = new TenantLicense(["test-module"], tenantId,
                DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), ModuleAbiVersion.Major, string.Empty)
            .Sign(vendorKey);
        var json = JsonSerializer.Serialize(license, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children",
            new { name = $"License {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".json" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(json)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
        await TestJson.Put(admin, "/api/modules/test-module/license", new { licenseDocumentId = docId });
        return new Rig(owner, admin, member, tenantId, repoId) { MemberEmail = memberEmail };
    }
}
