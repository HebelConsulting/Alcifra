using System.Net;
using Alcifra.Cli.Commands;
using Alcifra.Cli.Infrastructure;

namespace Alcifra.EndToEndTests;

/// <summary>
/// <c>alcifra acl remove</c>: a principal's whole entry is deleted at its own address, which <c>revoke</c> of every right
/// cannot do (the API refuses an entry granting nothing). The kiosk's Tube seed runs it every night, so a second run
/// must find nothing to remove rather than fail.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class AclRemoveCommandTests(E2EApiFactory factory)
{
    [Fact]
    public async Task A_principals_entry_is_removed_and_a_second_run_finds_nothing_to_remove()
    {
        var (_, _, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"acl-{Guid.NewGuid():N}@e2e.local";
        const string password = "acl12345";
        await factory.SeedUserAsync(tenantId, email, password, "Acl Admin", canManageRepositories: true);
        await factory.GrantTenantAdminAsync(email);
        var http = factory.CreateAuthedClient(await factory.GetUserTokenAsync(email, password));
        var api = new AlcifraApi(http);

        var reader = $"Reader {Guid.NewGuid():N}"[..15];
        var readerId = await factory.SeedUserAsync(tenantId, $"reader-{Guid.NewGuid():N}@e2e.local", "reader1234", reader);
        var repository = await TestJson.Post(http, "/api/repositories", new { name = $"acl-{Guid.NewGuid():N}" });
        var folder = (await TestJson.Post(http, $"/api/documents/{repository.GetProperty("id").GetGuid()}/children", new { name = "Workshop" }))
            .GetProperty("id").GetGuid();
        var entry = $"/api/documents/{folder}/acl-entries/users/{readerId}";
        await TestJson.Put(http, entry, new { canSee = true, canReadContent = true });

        var settings = new AclSettings { Document = folder.ToString(), User = reader };
        var removed = await AclRemoval.RemoveAsync(api, settings, CancellationToken.None);

        Assert.NotNull(removed);
        Assert.Equal(["see", "read-content"], removed.Granted);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(entry)).StatusCode);
        Assert.Null(await AclRemoval.RemoveAsync(api, settings, CancellationToken.None));
    }

    // grant and revoke now send the entry's tag, read from its body (the GET emits no ETag header): the API must accept
    // that tag, quoted, as If-Match, and refuse a stale one. The kiosk's seed runs grant every night.
    [Fact]
    public async Task The_tag_in_an_entrys_body_is_the_If_Match_its_write_accepts()
    {
        var (_, _, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"acl-{Guid.NewGuid():N}@e2e.local";
        const string password = "acl12345";
        await factory.SeedUserAsync(tenantId, email, password, "Acl Admin", canManageRepositories: true);
        await factory.GrantTenantAdminAsync(email);
        var http = factory.CreateAuthedClient(await factory.GetUserTokenAsync(email, password));
        var readerId = await factory.SeedUserAsync(tenantId, $"reader-{Guid.NewGuid():N}@e2e.local", "reader1234", "Reader");
        var repository = await TestJson.Post(http, "/api/repositories", new { name = $"acl-{Guid.NewGuid():N}" });
        var entry = $"/api/documents/{repository.GetProperty("id").GetGuid()}/acl-entries/users/{readerId}";
        await TestJson.Put(http, entry, new { canSee = true });
        var tag = $"\"{(await TestJson.Get(http, entry)).GetProperty("etag").GetString()}\"";

        Assert.Equal(HttpStatusCode.OK, (await PutAsync(http, entry, tag, new { canSee = true, canReadContent = true })).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await PutAsync(http, entry, tag, new { canSee = true })).StatusCode);
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient http, string url, string ifMatch, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = System.Net.Http.Json.JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return http.SendAsync(request);
    }

    [Fact]
    public void A_revoke_of_every_right_is_refused_naming_the_remove_to_run()
    {
        var settings = new AclSettings { Document = "4d3f065e-0544-408c-8e56-3aa20deacb04", ServiceAccount = "Module: Tube" };

        var refusal = AclRemoval.RevokeAllRefused(settings);

        Assert.Contains("refuses an entry that grants nothing", refusal);
        Assert.EndsWith("alcifra acl remove 4d3f065e-0544-408c-8e56-3aa20deacb04 --service-account \"Module: Tube\"", refusal);
    }
}
