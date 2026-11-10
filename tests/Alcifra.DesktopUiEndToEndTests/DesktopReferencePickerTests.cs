using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Alcifra.ApiClient;
using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.UiEndToEndTests;

// A reference field naming the masks it accepts is picked, not typed (#2214, ADR 1068): the TestModule's Test Poster has a
// Picture field accepting only a Test Picture. The picker offers the pictures by name, reaches each one's picture through its
// versions, stores the picked id, and files a picked file as a Test Picture beside the item. Driven through the editor's model
// against a real server; the window itself is not opened here (no display). The module is activated in a tenant of this
// test's own: every other test here shares the demo tenant, and its New menus and module list must not change under them.
[Collection(UiCollection.Name)]
public class DesktopReferencePickerTests(SelfHostedAppFixture app)
{
    // The TestModule's own ids (tests/Alcifra.TestModule), which this project does not reference.
    private static readonly Guid PictureMaskId = Guid.Parse("7E57AB1E-0000-0000-0000-00000000000A");
    private static readonly Guid PosterMaskId = Guid.Parse("7E57AB1E-0000-0000-0000-00000000000B");

    // A 1×1 PNG: a picture any decoder reads.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task A_picture_field_offers_pictures_by_name_and_files_one_from_a_file()
    {
        DesktopClientOptions.ApiBaseUrl = app.BaseUrl;
        var (token, tenantId, repoId) = await TestModuleTenant.CreateAsync(app, "pictures");
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await TestModuleTenant.ActivateAsync(app, http, tenantId, repoId);
        var api = new AlcifraApiClient(token);
        var children = $"/api/documents/{repoId}/children";
        var run = Guid.NewGuid().ToString("N")[..8];
        var existing = await api.Documents.UploadFileAsync(children, $"Sunset {run}.png", Png, maskId: PictureMaskId);

        var definition = (await api.Masks.GetMaskFieldsAsync($"/api/masks/{PosterMaskId}")).Single(f => f.Name == "Picture");
        Assert.NotNull(definition.TargetsHref);
        Assert.Equal("Test Picture", Assert.Single(definition.AcceptedMasks!).Name);

        var editor = MaskFieldEditViewModel.Create(definition, []);
        Assert.True(editor.IsReferencePicker);
        Assert.False(editor.IsPlainSingleLine);
        ReferencePickerWiring.Wire(editor, api, _ => Task.FromResult<string?>(children));
        Assert.True(editor.OffersFile);

        // Offered by name, with the picture reached through the row's versions.
        var sunset = Assert.Single((await editor.TargetSource!(run, CancellationToken.None)).Cast<ReferenceChoice>());
        Assert.Equal(existing, sunset.Target.Id);
        Assert.Equal(Png, await api.Masks.TargetPictureAsync(sunset.Target));

        // Picking stores the id, which is what the field holds.
        editor.Chosen = sunset;
        Assert.Equal([existing.ToString()], editor.ToValues());

        // From a file: filed as the one accepted kind, in the folder given, and picked.
        await editor.FileAsync($"Dawn {run}.png", Png);
        Assert.Null(editor.FileProblem);
        var filed = editor.Chosen!.Target.Id;
        Assert.NotEqual(existing, filed);
        Assert.Equal([filed.ToString()], editor.ToValues());
        // The list offers only the accepted kind, so finding it there proves it was filed as a Test Picture.
        Assert.Contains((await editor.TargetSource!($"Dawn {run}", CancellationToken.None)).Cast<ReferenceChoice>(), c => c.Target.Id == filed);

        // The same name again is refused, and said beside the field rather than lost.
        await editor.FileAsync($"Dawn {run}.png", Png);
        Assert.NotNull(editor.FileProblem);
        Assert.Equal(filed, editor.Chosen!.Target.Id);
    }
}
