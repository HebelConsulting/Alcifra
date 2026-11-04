using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.Domain.Masks;

namespace Alcifra.EndToEndTests;

// What a document's picker offers, and what the server refuses (#2169, ADR 1049).
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class MaskChoiceTests(E2EApiFactory factory)
{
    private static async Task<IReadOnlyList<string>> ChoicesAsync(HttpClient http, Guid document) =>
        [.. (await TestJson.Get(http, $"/api/documents/{document}/mask")).GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("name").GetString()!)];

    private static async Task<Guid> ChildAsync(HttpClient http, Guid parent, object body) =>
        (await TestJson.Post(http, $"/api/documents/{parent}/children", body)).GetProperty("id").GetGuid();

    [Fact]
    public async Task A_folder_is_offered_folder_masks_and_a_document_item_masks()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        var folder = await ChildAsync(rig.Owner, rig.RepoId, new { name = $"Folder {Guid.NewGuid():N}", folderMask = "folder" });
        var document = await ChildAsync(rig.Owner, rig.RepoId, new { name = $"Paper {Guid.NewGuid():N}" });
        await TestJson.Post(rig.Owner, $"/api/documents/{document}/versions", new { fileExtension = ".pdf" });
        await TestJson.Put(rig.Owner, $"/api/documents/{document}/mask", new { maskId = WellKnownMaskIds.BasicEntry });

        var forFolder = await ChoicesAsync(rig.Owner, folder);
        Assert.Contains("Folder", forFolder);
        Assert.DoesNotContain("Basic Entry", forFolder);
        Assert.DoesNotContain("Repository", forFolder);
        Assert.DoesNotContain("IMAP Special", forFolder);

        var forDocument = await ChoicesAsync(rig.Owner, document);
        Assert.Contains("Basic Entry", forDocument);
        Assert.DoesNotContain("Folder", forDocument);
    }

    [Fact]
    public async Task A_modules_folder_mask_is_offered_where_it_may_live()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        using var vendorKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await TestModuleRig.ActivateAsync(rig, vendorKey);
            var folder = await ChildAsync(rig.Owner, rig.RepoId, new { name = $"Folder {Guid.NewGuid():N}", folderMask = "folder" });

            Assert.Contains("Test Register", await ChoicesAsync(rig.Owner, folder));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
        }
    }

    [Fact]
    public async Task The_server_refuses_a_folder_mask_on_content_and_an_item_mask_on_a_folder_with_children()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        var document = await ChildAsync(rig.Owner, rig.RepoId, new { name = $"Paper {Guid.NewGuid():N}" });
        await TestJson.Post(rig.Owner, $"/api/documents/{document}/versions", new { fileExtension = ".pdf" });
        var onContent = await rig.Owner.PutAsJsonAsync($"/api/documents/{document}/mask", new { maskId = WellKnownMaskIds.Folder });
        Assert.Equal(HttpStatusCode.Conflict, onContent.StatusCode);
        Assert.Contains("MASK_KIND_MISMATCH", await onContent.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var folder = await ChildAsync(rig.Owner, rig.RepoId, new { name = $"Folder {Guid.NewGuid():N}", folderMask = "folder" });
        await ChildAsync(rig.Owner, folder, new { name = $"Inner {Guid.NewGuid():N}", folderMask = "folder" });
        var onFolder = await rig.Owner.PutAsJsonAsync($"/api/documents/{folder}/mask", new { maskId = WellKnownMaskIds.BasicEntry });
        Assert.Equal(HttpStatusCode.Conflict, onFolder.StatusCode);
        Assert.Contains("MASK_KIND_MISMATCH", await onFolder.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_empty_document_still_takes_an_item_mask_before_its_content()
    {
        var rig = await TestModuleRig.CreateAsync(factory);
        var blank = await ChildAsync(rig.Owner, rig.RepoId, new { name = $"Blank {Guid.NewGuid():N}" });

        var answer = await rig.Owner.PutAsJsonAsync($"/api/documents/{blank}/mask", new { maskId = WellKnownMaskIds.BasicEntry });

        Assert.True(answer.IsSuccessStatusCode);
    }
}
