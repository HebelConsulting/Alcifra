using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// A reference field naming the masks it accepts is picked in the web client too (#2214, ADR 1068), as on the desktop: the
// TestModule's Test Poster has a Picture field accepting only a Test Picture. In the detail pane's edit mode the field
// offers the pictures by name, each with its picture, stores the picked one's id on Save, and "From a file" files a new
// Test Picture beside the poster and picks it.
[Collection(ModuleUiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebReferencePickerTests(ModuleAppFixture app)
{
    private static readonly Guid PictureMaskId = Guid.Parse("7E57AB1E-0000-0000-0000-00000000000A");
    private static readonly Guid PosterMaskId = Guid.Parse("7E57AB1E-0000-0000-0000-00000000000B");

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    [Fact]
    public async Task A_picture_field_is_picked_by_name_and_takes_a_file()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var (http, _, repoId) = await app.ActivateTestModuleAsync();
        Guid sunset, poster;
        string repoName;
        using (http)
        {
            repoName = (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{repoId}")).GetProperty("name").GetString()!;
            sunset = await FileAsync(http, repoId, $"Sunset-{run}", ".png", Png);
            (await http.PutAsJsonAsync($"/api/documents/{sunset}/mask", new { maskId = PictureMaskId })).EnsureSuccessStatusCode();
            poster = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = $"Poster-{run}" }))
                .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            (await http.PutAsJsonAsync($"/api/documents/{poster}/mask", new { maskId = PosterMaskId })).EnsureSuccessStatusCode();
        }

        var page = await Ui.LoginAsync(app);
        var list = page.Locator("[data-pane='list']");
        var index = page.Locator("[data-pane='index']");
        await Expect(page.Locator("[data-pane='tree']")).ToBeVisibleAsync(new() { Timeout = 60000 });
        await page.Locator("[data-pane='tree']").GetByText(repoName).First.ClickAsync();
        await list.GetByText($"Poster-{run}").First.ClickAsync();

        // Picked by name, each candidate with its picture.
        await index.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        var picker = index.Locator("[data-reference-picker]");
        await Expect(picker).ToBeVisibleAsync();
        await picker.Locator("input:not([type=file])").FillAsync(run);
        var choice = page.Locator("[data-reference-choice]").Filter(new() { HasText = $"Sunset-{run}" });
        await Expect(choice).ToBeVisibleAsync();
        await Expect(choice.Locator("img")).ToHaveCountAsync(1);
        await choice.ClickAsync();
        await Expect(picker.Locator("[data-reference-chosen-picture]")).ToBeVisibleAsync();
        await index.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        Assert.Equal([sunset.ToString()], await PictureValueAsync(poster));

        // From a file: filed as a Test Picture beside the poster, picked, and stored on Save.
        await index.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        await picker.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = $"Dawn-{run}.png", MimeType = "image/png", Buffer = Png });
        await Expect(picker.Locator("input:not([type=file])")).ToHaveValueAsync($"Dawn-{run}");
        await index.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        var stored = Assert.Single(await PictureValueAsync(poster));
        Assert.NotEqual(sunset.ToString(), stored);

        // Filed as a Test Picture: the field's own candidates, which offer only that kind, name it.
        using var check = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        check.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(app.BaseUrl));
        var field = (await check.GetFromJsonAsync<JsonElement>($"/api/masks/{PosterMaskId}")).GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "Picture");
        var targets = field.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "targets").GetProperty("href").GetString();
        var offered = (await check.GetFromJsonAsync<JsonElement>($"{targets}?q=Dawn-{run}")).GetProperty("targets").EnumerateArray().Select(o => o.GetProperty("id").GetString()).ToList();
        Assert.Equal([stored], offered);
    }

    private async Task<List<string>> PictureValueAsync(Guid documentId)
    {
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(app.BaseUrl));
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var fields = (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{documentId}/index-data")).GetProperty("fields");
            var values = fields.EnumerateArray().FirstOrDefault(f => f.GetProperty("fieldName").GetString() == "Picture") is { ValueKind: JsonValueKind.Object } field
                ? field.GetProperty("values").EnumerateArray().Select(v => v.GetString()!).ToList()
                : [];
            if (values.Count > 0)
            {
                return values;
            }

            await Task.Delay(250);
        }

        return [];
    }

    private static async Task<Guid> FileAsync(HttpClient http, Guid folderId, string name, string extension, byte[] bytes)
    {
        var id = (await (await http.PostAsJsonAsync($"/api/documents/{folderId}/children", new { name }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var version = await (await http.PostAsJsonAsync($"/api/documents/{id}/versions", new { fileExtension = extension }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(bytes))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{id}/versions/{version.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        return id;
    }
}
