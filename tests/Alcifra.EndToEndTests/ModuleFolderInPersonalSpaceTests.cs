using System.Net;
using System.Security.Cryptography;
using Alcifra.Api.Controllers;

namespace Alcifra.EndToEndTests;

/// <summary>
/// A module's folder in a person's My Documents (#2132, ADR 1045): the module reaches the folder a person made with one of
/// its masks, and a populate hook whose module cannot read its subject is not run, answers "current" (no client loops)
/// and says why. Found on the kiosk: an Aerodrome folder in My Documents was refreshed 889 times in three hours.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class ModuleFolderInPersonalSpaceTests(E2EApiFactory factory)
{
    [Fact]
    public async Task A_module_folder_in_my_documents_is_filled_and_one_the_module_cannot_read_is_not_run_and_says_why()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            // Active but UNGRANTED: the module's principal holds nothing anywhere.
            await TestModuleRig.ActivateAsync(rig, vendorKey, grantPrincipal: false);

            // In a shared repository the module was never granted: the hook is not run, "current", and the reason.
            var shared = await DossierAsync(rig.Owner, rig.RepoId);
            var refused = await rig.Admin.PostAsync(Refresh(shared), null);
            Assert.Equal(HttpStatusCode.NoContent, refused.StatusCode);
            Assert.Equal("current", refused.Headers.GetValues(MachineTransitionsController.PopulateOutcomeHeader).Single());
            Assert.Equal("module-has-no-access", refused.Headers.GetValues(MachineTransitionsController.PopulateUnavailableHeader).Single());
            Assert.Equal(0, await ChildCountAsync(rig.Admin, shared));

            // In the person's own My Documents: making the folder is the invitation, so the hook runs and fills it.
            var personal = (await TestJson.Post(rig.Admin, "/api/me/personal-repository", new { })).GetProperty("id").GetGuid();
            var myDocuments = (await TestJson.Get(rig.Admin, $"/api/documents/{personal}/children"))
                .GetProperty("children").EnumerateArray()
                .Single(c => c.GetProperty("name").GetString() == "My Documents")
                .GetProperty("id").GetGuid();
            var mine = await DossierAsync(rig.Admin, myDocuments);
            var ran = await rig.Admin.PostAsync(Refresh(mine), null);
            Assert.Equal(HttpStatusCode.NoContent, ran.StatusCode);
            Assert.Equal("ran", ran.Headers.GetValues(MachineTransitionsController.PopulateOutcomeHeader).Single());
            Assert.False(ran.Headers.Contains(MachineTransitionsController.PopulateUnavailableHeader));
            Assert.Equal(1, await ChildCountAsync(rig.Admin, mine));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    private static string Refresh(Guid dossierId) => $"/api/documents/{dossierId}/machine/test-pilot/transitions/refresh";

    private static async Task<Guid> DossierAsync(HttpClient api, Guid parentId) =>
        (await TestJson.Post(api, $"/api/documents/{parentId}/children",
            new { name = $"Dossier {Guid.NewGuid():N}", maskId = Alcifra.TestModule.TestModule.DossierMaskId }))
        .GetProperty("id").GetGuid();

    private static async Task<int> ChildCountAsync(HttpClient api, Guid folderId) =>
        (await TestJson.Get(api, $"/api/documents/{folderId}/children")).GetProperty("children").GetArrayLength();
}
