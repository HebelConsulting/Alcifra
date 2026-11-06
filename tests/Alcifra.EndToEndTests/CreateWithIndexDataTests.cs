using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Alcifra.Infrastructure.Persistence;
using Alcifra.TestModule;

namespace Alcifra.EndToEndTests;

/// <summary>
/// A document can be created WITH its index data in one request, and a refused create says why (#1634).
/// </summary>
/// <remarks>
/// A mask with a REQUIRED field inside an exclusive module folder could not be created through the API at all: the
/// child can only be created with its mask, and a create carried no values. The refusal also arrived as
/// <c>409 DOCUMENT_NAME_CONFLICT</c> for a name that existed nowhere, because the create mapped every SaveChanges
/// invariant to a name clash.
/// </remarks>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class CreateWithIndexDataTests(E2EApiFactory factory)
{
    [Fact]
    public async Task A_case_with_a_required_field_is_created_with_its_value_and_refused_for_the_right_reason_without()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        var rig = await TestModuleRig.CreateAsync(factory);
        try
        {
            await TestModuleRig.ActivateAsync(rig, vendorKey);
            var register = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Register {Guid.NewGuid():N}", maskId = TestModule.TestModule.RegisterMaskId })).GetProperty("id").GetGuid();
            var caseNumber = await FieldAsync(rig.TenantId, TestModule.TestModule.CaseMaskId, "Case number");

            // Without the required value: refused AS a missing required field, not as a name clash.
            var refused = await rig.Owner.PostAsJsonAsync($"/api/documents/{register}/children",
                new { name = "Case 1", maskId = TestModule.TestModule.CaseMaskId });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal("REQUIRED_FIELD_MISSING", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());

            // With it, in the same request: created, and the value is stored.
            var created = await TestJson.Post(rig.Owner, $"/api/documents/{register}/children", new
            {
                name = "Case 1",
                maskId = TestModule.TestModule.CaseMaskId,
                fields = new[] { new { fieldDefinitionId = caseNumber, values = new[] { "C-42" } } },
            });
            var caseId = created.GetProperty("id").GetGuid();
            var stored = (await TestJson.Get(rig.Owner, $"/api/documents/{caseId}/index-data")).GetProperty("fields")
                .EnumerateArray().Single(f => f.GetProperty("fieldDefinitionId").GetGuid() == caseNumber);
            Assert.Equal(["C-42"], stored.GetProperty("values").EnumerateArray().Select(v => v.GetString()));

            // A real duplicate is still the one name conflict.
            var duplicate = await rig.Owner.PostAsJsonAsync($"/api/documents/{register}/children", new
            {
                name = "Case 1",
                maskId = TestModule.TestModule.CaseMaskId,
                fields = new[] { new { fieldDefinitionId = caseNumber, values = new[] { "C-43" } } },
            });
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            Assert.Equal("DOCUMENT_NAME_CONFLICT", (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errorCode").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    [Fact]
    public async Task The_admits_entry_names_where_the_masks_definition_is_and_a_refusal_names_the_field()
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        var rig = await TestModuleRig.CreateAsync(factory);
        try
        {
            await TestModuleRig.ActivateAsync(rig, vendorKey);
            var register = await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Register {Guid.NewGuid():N}", maskId = TestModule.TestModule.RegisterMaskId });
            var registerId = register.GetProperty("id").GetGuid();

            // The New dialog reads the definition from the address the entry advertises (#2217, ADR 1067): the case's
            // entry says where, and the definition there marks the case number as required.
            var registerRow = (await TestJson.Get(rig.Owner, $"/api/documents/{rig.RepoId}/children")).GetProperty("children").EnumerateArray()
                .Single(c => c.GetProperty("id").GetGuid() == registerId);
            var entry = registerRow.GetProperty("admits").EnumerateArray()
                .Single(a => a.GetProperty("maskId").GetGuid() == TestModule.TestModule.CaseMaskId);
            Assert.Equal("name", entry.GetProperty("prompt").GetString());
            var maskHref = entry.GetProperty("maskHref").GetString();
            Assert.False(string.IsNullOrEmpty(maskHref), "the entry advertises no maskHref");

            var definition = await TestJson.Get(rig.Owner, maskHref!);
            var caseNumber = definition.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "Case number");
            Assert.True(caseNumber.GetProperty("isRequired").GetBoolean());

            // A create without it names the field as DATA, so a client says "Case number is required" in its own words.
            var refused = await rig.Owner.PostAsJsonAsync($"/api/documents/{registerId}/children",
                new { name = "Case 2", maskId = TestModule.TestModule.CaseMaskId });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("REQUIRED_FIELD_MISSING", problem.GetProperty("errorCode").GetString());
            Assert.Equal("Case number", problem.GetProperty("field").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    private async Task<Guid> FieldAsync(Guid tenantId, Guid maskId, string name)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<AlcifraDbContext>();
        return await db.MaskVersions.Where(v => v.MaskId == maskId && v.IsCurrent)
            .Join(db.FieldDefinitions, v => v.Id, f => f.MaskVersionId, (v, f) => f)
            .Where(f => f.Name == name).Select(f => f.Id).SingleAsync();
    }
}
