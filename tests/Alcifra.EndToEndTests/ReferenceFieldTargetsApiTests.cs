using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.Domain.Masks;
using Alcifra.Infrastructure.Persistence;

namespace Alcifra.EndToEndTests;

// A reference field naming accepted masks advertises a picker's address, which offers only documents of those masks that the
// caller may see, and refuses a target of another kind at the door (#2214, ADR 1068). Rows are made directly here: only a
// module's seed declares them, through an ABI version not yet published.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ReferenceFieldTargetsApiTests(E2EApiFactory factory)
{
    [Fact]
    public async Task The_picker_offers_the_accepted_kind_the_caller_can_see_and_a_wrong_target_is_refused()
    {
        var (clientId, secret, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var service = factory.CreateAuthedClient(await factory.GetTokenAsync(clientId, secret));
        var adminEmail = $"targets-{Guid.NewGuid():N}@e2e.local";
        const string password = "targets1234";
        await factory.SeedUserAsync(tenantId, adminEmail, password, "Targets Admin", isTenantAdmin: true);
        await factory.GrantTenantAdminAsync(adminEmail);   // carries CanManageMasks
        var otherEmail = $"targets-{Guid.NewGuid():N}@e2e.local";
        await factory.SeedUserAsync(tenantId, otherEmail, password, "Targets Other");
        using var admin = factory.CreateAuthedClient(await factory.GetUserTokenAsync(adminEmail, password));
        using var other = factory.CreateAuthedClient(await factory.GetUserTokenAsync(otherEmail, password));

        var run = Guid.NewGuid().ToString("N")[..8];
        var stillMask = (await TestJson.Post(admin, "/api/masks", new { name = $"Still {run}", fields = Array.Empty<object>() })).GetProperty("id").GetGuid();
        var clip = await TestJson.Post(admin, "/api/masks", new
        {
            name = $"Clip {run}",
            fields = new object[] { new { name = "Poster", dataType = (int)FieldDataType.DocumentReference, isRequired = false, isList = false } },
        });
        var clipMask = clip.GetProperty("id").GetGuid();
        var poster = clip.GetProperty("fields")[0].GetProperty("id").GetGuid();

        // Before a field names masks it advertises no picker, and the address answers nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/masks/{clipMask}/fields/{poster}/targets")).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
            db.FieldDefinitionAcceptedMasks.Add(new FieldDefinitionAcceptedMask { Id = Guid.NewGuid(), TenantId = tenantId, FieldDefinitionId = poster, MaskId = stillMask });
            await db.SaveChangesAsync();
        }

        var repo = (await TestJson.Post(service, "/api/repositories", new { name = $"targets-{run}" })).GetProperty("id").GetGuid();
        async Task<Guid> Made(string name, Guid mask)
        {
            var id = (await TestJson.Post(admin, $"/api/documents/{repo}/children", new { name })).GetProperty("id").GetGuid();
            (await admin.PutAsJsonAsync($"/api/documents/{id}/mask", new { maskId = mask })).EnsureSuccessStatusCode();
            return id;
        }

        var still = await Made($"Still-{run}", stillMask);
        var video = await Made($"Video-{run}", clipMask);

        var field = (await TestJson.Get(admin, $"/api/masks/{clipMask}")).GetProperty("fields")[0];
        var href = field.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "targets").GetProperty("href").GetString()!;

        var rows = (await TestJson.Get(admin, $"{href}?q={run}")).GetProperty("targets").EnumerateArray().ToList();
        Assert.Equal([still], rows.Select(t => t.GetProperty("id").GetGuid()));

        // The field names what it accepts, so a picker knows when to offer filing a file (#2214), and each row links its
        // versions, which is how a picker reaches its picture.
        var accepted = Assert.Single(field.GetProperty("acceptedMasks").EnumerateArray());
        Assert.Equal(stillMask, accepted.GetProperty("id").GetGuid());
        Assert.Equal($"Still {run}", accepted.GetProperty("name").GetString());
        Assert.Contains(rows[0].GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "versions" && l.GetProperty("href").GetString() == $"/api/documents/{still}/versions");
        Assert.Empty((await TestJson.Get(other, href)).GetProperty("targets").EnumerateArray());

        var wrong = await admin.PutAsJsonAsync($"/api/documents/{video}/index-data", new { fields = new[] { new { fieldDefinitionId = poster, values = new[] { video.ToString() } } } });
        Assert.True((int)wrong.StatusCode is >= 400 and < 500, $"expected a refusal, got {(int)wrong.StatusCode}");
        (await admin.PutAsJsonAsync($"/api/documents/{video}/index-data", new { fields = new[] { new { fieldDefinitionId = poster, values = new[] { still.ToString() } } } })).EnsureSuccessStatusCode();
    }
}
