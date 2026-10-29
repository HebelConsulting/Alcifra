using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// The web half of encrypted content's audience (ADR 0983): a store's manager opens "Encrypted for…" from the row's
// menu, adds a reader the dialog offers, and saves — and the server's audience then holds that reader. A throwaway
// folder (with its own grants, so the administrator is a READER and not just bypassing) keeps the demo tenant clean.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebContentAudienceTests
{
    private readonly SelfHostedAppFixture _app;

    public WebContentAudienceTests(SelfHostedAppFixture app) => _app = app;

    // WellKnownMaskIds.PasswordStore: every tenant reuses the well-known ids (ADR 0198); this project has no Domain reference.
    private static readonly Guid PasswordStoreMask = Guid.Parse("E10E1000-E100-E100-E100-E10E10E10E4D");

    [Fact]
    public async Task A_manager_adds_a_reader_to_a_stores_audience_from_the_row_menu()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var folderName = $"aud-{suffix}";
        var storeName = $"Team {suffix}";
        var readerName = $"Reader {suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var me = (await http.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("userId").GetGuid();
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var folderId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = folderName })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await http.PutAsJsonAsync($"/api/documents/{folderId}/acl-entries/inheritance", new { breaksInheritance = true })).EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync($"/api/documents/{folderId}/acl-entries/users/{me}", new { canSee = true, canReadContent = true, canEditContent = true, canCreateSubItems = true })).EnsureSuccessStatusCode();
        var reader = await (await http.PostAsJsonAsync("/api/users", new { email = $"reader-{suffix}@alcifra.local", displayName = readerName })).Content.ReadFromJsonAsync<JsonElement>();
        (await http.PutAsJsonAsync($"/api/documents/{folderId}/acl-entries/users/{reader.GetProperty("id").GetGuid()}", new { canSee = true, canReadContent = true })).EnsureSuccessStatusCode();
        var storeId = (await (await http.PostAsJsonAsync($"/api/documents/{folderId}/children", new { name = storeName, maskId = PasswordStoreMask })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        var list = page.Locator("[data-pane='list']");
        await list.Locator(".wb-list-row").Filter(new() { HasText = folderName }).First.DblClickAsync();
        await Expect(list.Locator(".wb-list-row").Filter(new() { HasText = storeName })).ToBeVisibleAsync();

        await (await DocumentActionsUi.OpenRowMenuAsync(page, storeName)).Filter(new() { HasText = "Encrypted for" }).First.ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog.Locator("[data-dialog='audience']")).ToBeVisibleAsync();
        await dialog.Locator(".mud-input-control").First.ClickAsync();   // MudSelect opens via its input-control
        await page.Locator(".mud-list-item").Filter(new() { HasText = readerName }).First.ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Add", Exact = true }).ClickAsync();
        await Expect(dialog.Locator(".wb-audience-row").Filter(new() { HasText = readerName })).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Expect(page.Locator(".mud-dialog [data-dialog='audience']")).ToHaveCountAsync(0);

        var audience = await http.GetFromJsonAsync<JsonElement>($"/api/documents/{storeId}/audience");
        Assert.Contains(audience.GetProperty("members").EnumerateArray(), m => m.GetProperty("displayName").GetString() == readerName);
    }
}
