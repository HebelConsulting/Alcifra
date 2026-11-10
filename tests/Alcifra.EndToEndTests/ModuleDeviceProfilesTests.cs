using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alcifra.ModuleAbi;

namespace Alcifra.EndToEndTests;

// A module's part in a device's setup profile (ABI 1.27, ADR 1036): the test module adds a VPN payload for every device
// of a tenant it is active in, and records the devices the core tells it were revoked. Read through the real hand-over.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-3")]
public class ModuleDeviceProfilesTests
{
    private const string VerifyKeyVariable = "SIMPLARCHIVE_TESTMODULE_VERIFY_KEY";
    private readonly E2EApiFactory _factory;

    public ModuleDeviceProfilesTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_active_module_adds_its_payload_to_the_profile_and_hears_of_the_revocation()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
            using var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
            var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Profiles {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
            var email = $"profiles-{Guid.NewGuid():N}@e2e.local";
            const string password = "profiles12345678";
            var adminId = await _factory.SeedUserAsync(tenantId, email, password, "Profiles Admin", isTenantAdmin: true);
            await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{adminId}",
                new { canSee = true, canReadContent = true, canCreateSubItems = true, canEditContent = true });
            using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));

            // Before activation: the profile carries the core's payloads only.
            var before = await TestJson.Post(admin, "/api/me/devices", new { name = "Before" });
            var profileBefore = await ProfileAsync(before);
            Assert.DoesNotContain("test-module.vpn", profileBefore);

            await ActivateAsync(owner, admin, tenantId, repoId, vendorKey);

            var enrolled = await TestJson.Post(admin, "/api/me/devices", new { name = "Phone" });
            var deviceId = enrolled.GetProperty("id").GetGuid();
            var profile = await ProfileAsync(enrolled);
            Assert.Contains("com.apple.vpn.managed", profile);
            Assert.Contains($"E2E VPN {email}", profile);
            Assert.Contains($".{deviceId}.test-module.vpn</string>", profile);   // the core's identifier, the module's part
            Assert.Contains(Alcifra.Presentation.ConfigurationProfile.PayloadUuid(deviceId, "test-module.scep"), profile);   // a payload names another's UUID

            // Taking the profile switched the device to chosen collections and moved its token (ADR 1034): revoke what is there now.
            var device = await TestJson.Get(admin, Href(enrolled, "self"));
            using var revoke = new HttpRequestMessage(HttpMethod.Delete, Href(device, "self"));
            revoke.Headers.TryAddWithoutValidation("If-Match", device.GetProperty("eTag").GetString());
            Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(revoke)).StatusCode);
            Assert.Contains(deviceId.ToString("D"), (Environment.GetEnvironmentVariable(TestModule.TestModule.RevokedDevicesVariable) ?? string.Empty).Split(','));
        }
        finally
        {
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    private async Task<string> ProfileAsync(JsonElement enrolled)
    {
        var link = new Uri(enrolled.GetProperty("handOver").GetProperty("profileUrl").GetString()!).PathAndQuery;
        using var camera = _factory.CreateClient();
        var answer = await camera.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return await answer.Content.ReadAsStringAsync();
    }

    private static string Href(JsonElement resource, string rel) => resource.GetProperty("links").EnumerateArray()
        .Single(l => l.GetProperty("rel").GetString() == rel).GetProperty("href").GetString()!;

    // The licence dance an administrator does: a vendor-signed licence filed as a document, named at activation.
    private static async Task ActivateAsync(HttpClient owner, HttpClient admin, Guid tenantId, Guid repoId, ECDsa vendorKey)
    {
        var license = new TenantLicense(["test-module"], tenantId, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), ModuleAbiVersion.Major, string.Empty).Sign(vendorKey);
        var json = JsonSerializer.Serialize(license, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var docId = (await TestJson.Post(owner, $"/api/documents/{repoId}/children", new { name = $"License {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(owner, $"/api/documents/{docId}/versions", new { fileExtension = ".json" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(json)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
        await TestJson.Put(admin, "/api/modules/test-module/license", new { licenseDocumentId = docId });
    }
}
