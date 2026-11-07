using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// A contact shows as a card in the Repositories preview, chosen by its mask (#2215): the editor's own form, read-only, and a
// pencil that opens the editor and shows the card again after Save. A .vcf the archive filed as a Basic Entry gets no card.
// The desktop's DesktopStructuredCardTests is its twin.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebPreviewCardTests(SelfHostedAppFixture app)
{
    [Fact]
    public async Task A_contact_shows_as_a_card_and_its_pencil_edits_it()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var book = await FolderAsync(http, repoId, $"Book-{run}", "addressbook");
        var plain = await FolderAsync(http, repoId, $"Plain-{run}", "folder");
        var vcard = $"BEGIN:VCARD\r\nVERSION:4.0\r\nUID:web-card-{run}\r\nFN:Anna {run}\r\nN:{run};Anna;;;\r\nORG:Example AG\r\nEND:VCARD\r\n";
        await FileAsync(http, book, $"Anna-{run}", ".vcf", vcard);
        await FileAsync(http, plain, $"Loose-{run}", ".vcf", vcard);

        var page = await Ui.LoginAsync(app);
        var list = page.Locator("[data-pane='list']");
        var preview = page.Locator("[data-pane='preview']");
        await Expect(page.Locator("[data-pane='tree']")).ToBeVisibleAsync(new() { Timeout = 60000 });
        await page.Locator("[data-pane='tree']").GetByText("Demo Repository").First.ClickAsync();
        await list.Locator(".wb-list-row").Filter(new() { HasText = $"Book-{run}" }).First.DblClickAsync();
        // An addressbook lists a contact by its own name.
        await list.Locator(".wb-list-row").Filter(new() { HasText = $"Anna {run}" }).First.ClickAsync();

        // The card: the form, read-only.
        var card = preview.Locator("[data-preview-card]");
        await Expect(card).ToBeVisibleAsync();
        await Expect(card.Locator("input").First).ToHaveValueAsync("Anna");
        await Expect(card.Locator("input").First).ToHaveAttributeAsync("readonly", string.Empty);

        // The pencil: the editor dialog, and the card again after Save.
        await card.Locator("[data-preview-card-edit]").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        var organization = dialog.Locator(".mud-input-control").Filter(new() { HasText = "Organization" }).Locator("input");
        await organization.FillAsync($"Renamed {run}");
        await organization.EvaluateAsync("el => el.blur()");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Expect(card.Locator(".mud-input-control").Filter(new() { HasText = "Organization" }).Locator("input")).ToHaveValueAsync($"Renamed {run}");

        // A .vcf in an ordinary folder: no card.
        await page.Locator("[data-pane='tree']").GetByText("Demo Repository").First.ClickAsync();
        await list.Locator(".wb-list-row").Filter(new() { HasText = $"Plain-{run}" }).First.DblClickAsync();
        await list.GetByText($"Loose-{run}").First.ClickAsync();
        await Expect(page.Locator("[data-pane='index']")).ToContainTextAsync($"Loose-{run}");
        await Expect(preview.Locator("[data-preview-card]")).ToHaveCountAsync(0);
    }

    private static async Task<Guid> FolderAsync(HttpClient http, Guid parent, string name, string folderMask) =>
        (await (await http.PostAsJsonAsync($"/api/documents/{parent}/children", new { name, folderMask })).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    private static async Task FileAsync(HttpClient http, Guid folder, string name, string extension, string text)
    {
        var id = (await (await http.PostAsJsonAsync($"/api/documents/{folder}/children", new { name })).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var version = await (await http.PostAsJsonAsync($"/api/documents/{id}/versions", new { fileExtension = extension }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(text)))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{id}/versions/{version.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
    }
}
