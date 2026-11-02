using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Alcifra.EndToEndTests;

// An anonymous module root prefix (ABI 1.28, ADR 1038): the tenant is the one whose module setting names the request's
// Host; an unknown host is 404; the module acts as its principal with nobody signed in; the limiter answers 429; the
// player's link carries the content type and an inline disposition.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class AnonymousRootPrefixTests
{
    private const string VerifyKeyVariable = "SIMPLARCHIVE_TESTMODULE_VERIFY_KEY";
    private readonly E2EApiFactory _factory;

    public AnonymousRootPrefixTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_host_names_the_tenant_the_module_answers_anyone_there_and_a_flood_is_refused()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            var rig = await TestModuleRig.CreateAsync(_factory);
            var host = $"video-{Guid.NewGuid():N}.e2e.test";
            using var anyone = _factory.CreateClient();

            // Before activation and before the setting: nobody serves that host.
            Assert.Equal(HttpStatusCode.NotFound, (await AsHostAsync(anyone, host, "/peertube/test-tube")).StatusCode);

            await TestModuleRig.ActivateAsync(rig, vendorKey, grantPrincipal: true);
            await TestJson.Put(rig.Admin, "/api/modules/test-module/settings", new { values = new Dictionary<string, string?> { ["publicHost"] = host } });

            var answer = await AsHostAsync(anyone, host, "/peertube/test-tube");
            Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
            var who = JsonDocument.Parse(await answer.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(rig.TenantId, who.GetProperty("tenantId").GetGuid());
            Assert.Equal(JsonValueKind.Null, who.GetProperty("userId").ValueKind);
            Assert.NotEqual(JsonValueKind.Null, who.GetProperty("serviceAccountId").ValueKind);   // the module's own principal
            Assert.False(who.GetProperty("encryptsAtRest").GetBoolean());

            Assert.Equal(HttpStatusCode.NotFound, (await AsHostAsync(anyone, $"other-{host}", "/peertube/test-tube")).StatusCode);

            // A player's link: the content type and an inline disposition ride on the presigned link.
            var docId = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children", new { name = $"Clip {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
            var created = await TestJson.Post(rig.Owner, $"/api/documents/{docId}/versions", new { fileExtension = ".mp4" });
            using (var storage = new HttpClient())
            {
                (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.ASCII.GetBytes("not really a video")))).EnsureSuccessStatusCode();
            }

            await TestJson.Put(rig.Owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
            var link = await AsHostAsync(anyone, host, $"/peertube/test-tube/{docId:D}/link");
            Assert.Equal(HttpStatusCode.OK, link.StatusCode);
            var url = JsonDocument.Parse(await link.Content.ReadAsStringAsync()).RootElement.GetProperty("url").GetString()!;
            Assert.Contains("video%2Fmp4", url);
            Assert.Contains("inline", url);

            // Last, since the limiter is per client address for the whole process: a flood from one address is refused
            // with 429, and the limit is counted before the host is resolved (an unknown host floods too).
            var statuses = new List<HttpStatusCode>();
            for (var i = 0; i < 130; i++)
            {
                statuses.Add((await AsHostAsync(anyone, $"flood-{host}", "/peertube/test-tube")).StatusCode);
            }

            Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
            Assert.All(statuses.TakeWhile(s => s != HttpStatusCode.TooManyRequests), s => Assert.Equal(HttpStatusCode.NotFound, s));
        }
        finally
        {
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
        }
    }

    private static async Task<HttpResponseMessage> AsHostAsync(HttpClient client, string host, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Host = host;
        return await client.SendAsync(request);
    }
}
