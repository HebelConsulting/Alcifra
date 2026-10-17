using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// A module that needs no licence (ABI 1.23, ADR 0944): an administrator activates it without a file by following its
// `activate` rel, it carries no support end date, and `deactivate` turns it off again. A module that requires a licence
// offers neither and refuses the act. The test module turns licence-free on an environment switch.
[Collection(E2ECollection.Name)]
// e2e-2 — UiLegBalanceTests requires every class in a split suite to name its leg.
[Trait("Area", "e2e-2")]
public class LicenceFreeModuleTests(E2EApiFactory factory)
{
    private const string LicenceFree = "SIMPLARCHIVE_TESTMODULE_LICENCE_FREE";

    private static JsonElement TestModuleRow(JsonElement listing) =>
        listing.GetProperty("items").EnumerateArray().Single(m => m.GetProperty("moduleId").GetString() == "test-module");

    private static Dictionary<string, (string Href, string Method)> Links(JsonElement resource) =>
        resource.GetProperty("links").EnumerateArray().ToDictionary(
            l => l.GetProperty("rel").GetString()!, l => (l.GetProperty("href").GetString()!, l.GetProperty("method").GetString()!));

    [Fact]
    public async Task An_administrator_activates_and_deactivates_a_licence_free_module_without_a_file()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        Environment.SetEnvironmentVariable(LicenceFree, "1");
        try
        {
            var row = TestModuleRow(await TestJson.Get(rig.Admin, "/api/modules"));
            Assert.False(row.GetProperty("requiresLicence").GetBoolean());
            var links = Links(row);
            Assert.DoesNotContain("license", links.Keys);   // nothing to file
            Assert.Equal("PUT", links["activate"].Method);

            var activated = await TestJson.Put(rig.Admin, links["activate"].Href, new { });
            Assert.True(activated.GetProperty("active").GetBoolean());
            Assert.Equal(JsonValueKind.Null, activated.GetProperty("supportContractEndDate").ValueKind);   // nothing lapses
            Assert.Equal(JsonValueKind.Null, activated.GetProperty("deactivatesAt").ValueKind);

            var on = Links(TestModuleRow(await TestJson.Get(rig.Admin, "/api/modules")));
            Assert.DoesNotContain("activate", on.Keys);
            Assert.Equal("DELETE", on["deactivate"].Method);
            using (var off = await rig.Admin.DeleteAsync(on["deactivate"].Href))
            {
                Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
            }

            var afterwards = TestModuleRow(await TestJson.Get(rig.Admin, "/api/modules"));
            Assert.False(afterwards.GetProperty("activated").GetBoolean());
            Assert.Contains("activate", Links(afterwards).Keys);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LicenceFree, null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    [Fact]
    public async Task A_module_that_requires_a_licence_offers_no_activate_and_refuses_the_act()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        try
        {
            var links = Links(TestModuleRow(await TestJson.Get(rig.Admin, "/api/modules")));
            Assert.Contains("license", links.Keys);
            Assert.DoesNotContain("activate", links.Keys);

            using var refused = await rig.Admin.PutAsync("/api/modules/test-module/activation", JsonContent.Create(new { }));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("requires a licence", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }
}
