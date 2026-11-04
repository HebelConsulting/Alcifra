using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// The Toggle field on the web (ADR 1047): the slider and its word in the read row and in the editor. Unset reads as disabled,
// and a save stores what the slider says.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebToggleFieldTests(SelfHostedAppFixture app)
{
    [Fact]
    public async Task An_unset_toggle_reads_disabled_and_saving_it_on_reads_enabled()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = $"toggle-{suffix}";
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(app.BaseUrl));

        async Task<Guid> PostIdAsync(string url, object body) =>
            (await (await http.PostAsJsonAsync(url, body)).EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var maskId = await PostIdAsync("/api/masks", new { name = $"toggle-mask-{suffix}", fields = new object[] { new { name = "Published", dataType = 10 } } });
        var repoId = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories")
            .EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var docId = await PostIdAsync($"/api/documents/{repoId}/children", new { name });
        (await http.PutAsJsonAsync($"/api/documents/{docId}/mask", new { maskId })).EnsureSuccessStatusCode();

        var page = await Ui.LoginAsync(app);
        var index = page.Locator("[data-pane=\x27index\x27]");
        await page.GetByText("Demo Repository").First.ClickAsync();
        await page.Locator("[data-pane=\x27list\x27] .wb-list-row").Filter(new() { HasText = name }).First.ClickAsync();
        await Expect(index.GetByText($"Mask: toggle-mask-{suffix}")).ToBeVisibleAsync();
        var read = index.Locator(".wb-field-toggle");
        await Expect(read).ToContainTextAsync("Disabled");

        await index.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
        var editor = index.Locator(".wb-mask-row").Filter(new() { HasText = "Published" }).Locator(".wb-field-toggle");
        await Expect(editor).ToContainTextAsync("Disabled");
        await editor.Locator("input").CheckAsync();
        await Expect(editor).ToContainTextAsync("Enabled");
        await index.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

        await Expect(index.Locator(".wb-mask-row")).ToHaveCountAsync(0);
        await Expect(read).ToContainTextAsync("Enabled");
        var stored = await http.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}/index-data");
        Assert.Equal("true", stored.GetProperty("fields")[0].GetProperty("values")[0].GetString());
    }
}
