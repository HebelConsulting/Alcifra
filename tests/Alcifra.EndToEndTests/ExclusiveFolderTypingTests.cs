using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alcifra.TestModule;

namespace Alcifra.EndToEndTests;

/// <summary>
/// A module's exclusive folder types what is filed into it (ADR 1065): an upload created without a mask inside a Test Reel,
/// which admits only Test Clips, is a Test Clip after finalize — the Tube Channel's case, where it used to be given Basic
/// Entry and refused by the folder's own admission.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-3")]
public class ExclusiveFolderTypingTests(E2EApiFactory factory)
{
    private static async Task<(Guid Id, HttpResponseMessage Finalize)> UploadAsync(HttpClient api, Guid folderId, string name, string extension)
    {
        var doc = await TestJson.Post(api, $"/api/documents/{folderId}/children", new { name });
        var docId = doc.GetProperty("id").GetGuid();

        var version = await TestJson.Post(api, $"/api/documents/{docId}/versions", new { fileExtension = extension });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes($"not really a {extension}")))).EnsureSuccessStatusCode();
        }

        var finalize = await api.PutAsJsonAsync($"/api/documents/{docId}/versions/{version.GetProperty("id").GetGuid()}", new { });
        return (docId, finalize);
    }

    private static async Task<string?> MaskNameAsync(HttpClient api, Guid docId) =>
        (await TestJson.Get(api, $"/api/documents/{docId}/mask")).GetProperty("name").GetString();

    private async Task RunAsync(Func<TestModuleRig.Rig, Guid, Task> test)
    {
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        var rig = await TestModuleRig.CreateAsync(factory);
        try
        {
            await TestModuleRig.ActivateAsync(rig, vendorKey);
            var reel = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
                new { name = $"Reel {Guid.NewGuid():N}", maskId = TestModule.TestModule.ReelMaskId })).GetProperty("id").GetGuid();
            await test(rig, reel);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", null);
            rig.Admin.Dispose();
            rig.Owner.Dispose();
        }
    }

    [Fact]
    public Task A_clip_uploaded_into_a_reel_without_a_mask_is_a_test_clip() => RunAsync(async (rig, reel) =>
    {
        var (id, finalize) = await UploadAsync(rig.Owner, reel, $"clip-{Guid.NewGuid():N}", ".mp4");
        finalize.EnsureSuccessStatusCode();

        Assert.Equal("Test Clip", await MaskNameAsync(rig.Owner, id));
    });

    // The same bytes, named as what the reel holds no kind for: nothing says what it is, so it heads for Basic Entry and
    // the reel's admission refuses it — as a client error that names the cause, never a 500 and never a mistyped clip.
    [Fact]
    public Task A_file_the_reel_holds_no_kind_for_is_refused_by_the_reel() => RunAsync(async (rig, reel) =>
    {
        var (_, finalize) = await UploadAsync(rig.Owner, reel, $"notes-{Guid.NewGuid():N}", ".pdf");

        Assert.True((int)finalize.StatusCode is >= 400 and < 500, $"expected a client error, got {(int)finalize.StatusCode}");
        var problem = await finalize.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.TryGetProperty("errorCode", out var code) && code.GetString() is { Length: > 0 }, "the refusal names no cause");
    });

    // Outside an exclusive folder nothing types the clip: a video in an ordinary folder is an ordinary document, exactly
    // as a contact card outside an addressbook is (#665).
    [Fact]
    public Task A_clip_uploaded_into_an_ordinary_folder_stays_an_ordinary_document() => RunAsync(async (rig, _) =>
    {
        var (id, finalize) = await UploadAsync(rig.Owner, rig.RepoId, $"clip-{Guid.NewGuid():N}", ".mp4");
        finalize.EnsureSuccessStatusCode();

        Assert.NotEqual("Test Clip", await MaskNameAsync(rig.Owner, id));
    });
}
