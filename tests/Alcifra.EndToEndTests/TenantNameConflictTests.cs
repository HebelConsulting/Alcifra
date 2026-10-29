using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Alcifra.EndToEndTests;

// A second tenant under a name already taken is refused with 409 TENANT_NAME_CONFLICT, and creates no bucket (#1992).
// The bucket is made before the founding transaction on purpose (#1226), so until a name check ran first, every refused
// founding left an empty, object-locked bucket behind for a tenant that never existed.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class TenantNameConflictTests
{
    private readonly E2EApiFactory _factory;

    public TenantNameConflictTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_taken_name_is_refused_before_any_bucket_is_made()
    {
        var (clientId, secret) = await _factory.SeedPlatformAdministratorAsync();
        using var platform = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var suffix = Guid.NewGuid().ToString("N")[..10];
        object Founding(string who) => new
        {
            name = $"Taken {suffix}",
            administratorEmail = $"{who}-{suffix}@taken.e2e.local",
            administratorDisplayName = "Taken Admin",
            repositoryName = $"Archive {suffix}",
        };

        await TestJson.Post(platform, "/api/tenants", Founding("first"));
        using var s3 = _factory.CreateStorageClient();
        var bucketsBefore = (await s3.ListBucketsAsync()).Buckets?.Count ?? 0;

        using var refused = await platform.PostAsJsonAsync("/api/tenants", Founding("second"));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal("TENANT_NAME_CONFLICT", problem.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(bucketsBefore, (await s3.ListBucketsAsync()).Buckets?.Count ?? 0);
    }
}
