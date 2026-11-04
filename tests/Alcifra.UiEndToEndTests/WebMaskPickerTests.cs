using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// A folder's picker offers folder masks, and none for documents (#2169, ADR 1049).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebMaskPickerTests(SelfHostedAppFixture app)
{
    [Fact]
    public async Task A_folders_picker_offers_folder_masks_and_no_document_masks()
    {
        var name = $"picker-{Guid.NewGuid():N}"[..15];
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(app.BaseUrl));
        var repoId = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories")
            .EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name, folderMask = "folder" })).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(app);
        var index = page.Locator("[data-pane='index']");
        await page.GetByText("Demo Repository").First.ClickAsync();
        await page.Locator("[data-pane='list'] .wb-list-row").Filter(new() { HasText = name }).First.ClickAsync();
        await Expect(index.GetByText("Mask: Folder")).ToBeVisibleAsync();
        await index.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();

        await index.Locator(".wb-mask-row").First.Locator(".mud-input-control").ClickAsync();
        var options = page.Locator(".mud-popover-open .mud-list-item");
        await Expect(options.Filter(new() { HasText = "Folder" }).First).ToBeVisibleAsync();
        await Expect(options.Filter(new() { HasText = "Basic Entry" })).ToHaveCountAsync(0);
        await Expect(options.Filter(new() { HasText = "Certificate" })).ToHaveCountAsync(0);
    }
}
