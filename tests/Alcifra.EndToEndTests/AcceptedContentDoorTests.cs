using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace Alcifra.EndToEndTests;

/// <summary>
/// A mask naming the file types it holds, through the real version endpoint (ABI 1.29, ADR 1046): the refusal arrives as
/// its own code with the sentence a client can show, and an accepted type goes on as usual.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class AcceptedContentDoorTests(E2EApiFactory factory)
{
    [Fact]
    public async Task The_version_endpoint_refuses_another_type_with_its_own_code_and_takes_an_accepted_one()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await TestModuleRig.ActivateAsync(rig, vendorKey);
            var clipMask = Alcifra.TestModule.TestModule.ClipMaskId;
            var clip = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children", new { name = $"Clip {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
            await TestJson.Put(rig.Owner, $"/api/documents/{clip}/mask", new { maskId = clipMask });

            var refused = await rig.Owner.PostAsJsonAsync($"/api/documents/{clip}/versions", new { fileExtension = ".exe" });
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, refused.StatusCode);
            var problem = await refused.Content.ReadAsStringAsync();
            Assert.Contains("CONTENT_EXTENSION_NOT_ACCEPTED", problem, StringComparison.Ordinal);
            Assert.Contains("A Test Clip holds .mov, .mp4", problem, StringComparison.Ordinal);

            var accepted = await TestJson.Post(rig.Owner, $"/api/documents/{clip}/versions", new { fileExtension = ".MOV" });
            Assert.False(string.IsNullOrEmpty(accepted.GetProperty("uploadUrl").GetString()));
            using (var storage = new HttpClient())
            {
                (await storage.PutAsync(accepted.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.ASCII.GetBytes("a clip")))).EnsureSuccessStatusCode();
            }

            await TestJson.Put(rig.Owner, $"/api/documents/{clip}/versions/{accepted.GetProperty("id").GetGuid()}", new { });

            // The mask arriving over content it does not hold is the same refusal.
            var pdf = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children", new { name = $"Paper {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
            await TestJson.Post(rig.Owner, $"/api/documents/{pdf}/versions", new { fileExtension = ".pdf" });
            var remasked = await rig.Owner.PutAsJsonAsync($"/api/documents/{pdf}/mask", new { maskId = clipMask });
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, remasked.StatusCode);

            // Over WebDAV another extension is another name beside the document, never a new version of it: refused, content kept.
            var email = $"clip-{Guid.NewGuid():N}@e2e.local";
            const string password = "dav-1234";
            await factory.SeedUserAsync(rig.TenantId, email, password, "Clip User");
            await factory.GrantTenantAdminAsync(email);
            using var api = factory.CreateAuthedClient(await factory.GetUserTokenAsync(email, password));
            var davPassword = (await TestJson.Post(api, "/api/me/devices", E2EDevices.Any())).GetProperty("password").GetString()!;
            var repoName = (await TestJson.Get(api, $"/api/documents/{rig.RepoId}")).GetProperty("name").GetString()!;
            var clipName = (await TestJson.Get(api, $"/api/documents/{clip}")).GetProperty("name").GetString()!;
            using var dav = factory.CreateClient();
            using var put = new HttpRequestMessage(HttpMethod.Put, $"/webdav/{Uri.EscapeDataString(repoName)}/{Uri.EscapeDataString(clipName)}.exe")
            {
                Content = new ByteArrayContent(Encoding.ASCII.GetBytes("not a clip")),
            };
            put.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{davPassword}")));
            Assert.False((await dav.SendAsync(put)).IsSuccessStatusCode);
            var versions = (await TestJson.Get(api, $"/api/documents/{clip}/versions")).GetProperty("versions");
            Assert.Equal(1, versions.GetArrayLength());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }
}
