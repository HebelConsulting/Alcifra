using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// The details pane holds only the details' own actions (ADR 0970): Edit/Save/Cancel, the tags, its collapse
// toggles, the workflow status line. Everything the user does TO the document — follow, share, remind, compare,
// manage access, the workflow steps, the server-supplied actions — is in the ribbon's Documents group and the
// row's ⋮ menu, where the desktop has it. Make searchable lives ONLY in the Check-out tab's ribbon.
//
// A TIFF is filed because the finalizer stamps it ConvertibleScan by extension (#999), so its version advertises
// `make-searchable` without the OCR worker having to run — the bytes need not be a real image.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebDocumentOwnActionsTests
{
    private readonly SelfHostedAppFixture _app;

    public WebDocumentOwnActionsTests(SelfHostedAppFixture app) => _app = app;

    // Filed through the API (an upload in the browser asks about TIFF conversion first).
    private async Task<string> FileATiffAsync(string prefix)
    {
        var name = $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var v = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".tif" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(v.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("not-a-real-tiff")))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{v.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        return name;
    }

    private static async Task OpenDemoRepositoryAsync(IPage page, params string[] names)
    {
        await page.GetByText("Demo Repository").First.ClickAsync();
        foreach (var name in names)
        {
            await Expect(page.Locator("[data-pane='list']").GetByText(name)).ToBeVisibleAsync();
        }
    }

    [Fact]
    public async Task The_details_pane_holds_only_its_own_actions_and_the_rest_are_in_the_ribbon_and_the_row_menu()
    {
        var name = await FileATiffAsync("ownact");
        var page = await Ui.LoginAsync(_app);
        await OpenDemoRepositoryAsync(page, name);
        await page.Locator("[data-pane='list'] .wb-list-row").Filter(new() { HasText = name }).ClickAsync();
        var pane = DocumentActionsUi.DetailPane(page);
        await Expect(pane.Locator(".wb-detail-head")).ToContainTextAsync(name);

        // The ribbon offers them once the subject's detail has loaded — Follow because the resource advertised
        // `subscription`, which is also what proves the detail landed before the pane is inspected.
        await Expect(DocumentActionsUi.Ribbon(page, "follow")).ToBeEnabledAsync();
        await Expect(DocumentActionsUi.Ribbon(page, "share")).ToBeEnabledAsync();

        // Make searchable is NOT a Repositories action, though this TIFF's version advertises the rel: it lives
        // only in the Check-out tab's ribbon (owner decision, ADR 0970).
        await Expect(DocumentActionsUi.Ribbon(page, "make-searchable")).ToHaveCountAsync(0);

        // The pane: the pencil and nothing else that acts on the document.
        await Expect(pane.Locator("[data-tour='action-edit-index']")).ToBeVisibleAsync();
        foreach (var moved in new[] { "Follow", "Remind", "External links", "Share by link", "Compare versions", "Manage access", "Make searchable", "Bookings", "Approve", "Submit for review" })
        {
            await Expect(pane.GetByRole(AriaRole.Button, new() { Name = moved })).ToHaveCountAsync(0);
        }

        await Expect(pane.Locator("[data-module-action]")).ToHaveCountAsync(0);

        // The row's ⋮ menu carries the same subject's actions.
        var items = await DocumentActionsUi.OpenRowMenuAsync(page, name);
        foreach (var entry in new[] { "Follow", "Share by link", "Remind", "Manage access" })
        {
            await Expect(items.Filter(new() { HasText = entry }).First).ToBeVisibleAsync();
        }

        await Expect(items.Filter(new() { HasText = "Make searchable" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task A_row_that_is_not_the_selection_offers_none_of_the_selections_actions()
    {
        // ADR 0559: the ⋮ menu of an unselected row must not offer what the details pane loaded for ANOTHER row
        // under this row's name.
        var selected = await FileATiffAsync("ownsel");
        var other = await FileATiffAsync("ownoth");
        var page = await Ui.LoginAsync(_app);
        await OpenDemoRepositoryAsync(page, selected, other);
        await page.Locator("[data-pane='list'] .wb-list-row").Filter(new() { HasText = selected }).ClickAsync();
        await Expect(DocumentActionsUi.Ribbon(page, "follow")).ToBeEnabledAsync();

        var items = await DocumentActionsUi.OpenRowMenuAsync(page, other);
        await Expect(items.Filter(new() { HasText = "Manage access" }).First).ToBeVisibleAsync(); // the menu is open
        await Expect(items.Filter(new() { HasText = "Follow" })).ToHaveCountAsync(0);
        await Expect(items.Filter(new() { HasText = "Share by link" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Make_searchable_for_a_held_document_is_in_the_check_out_ribbon_not_its_pane()
    {
        var name = await FileATiffAsync("ownco");
        var page = await Ui.LoginAsync(_app);
        await OpenDemoRepositoryAsync(page, name);
        await (await DocumentActionsUi.OpenRowMenuAsync(page, name)).Filter(new() { HasText = "Check out" }).First.ClickAsync();

        await page.Locator(".wb-tab[aria-label=\"Check-out\"]").First.ClickAsync();
        var checkout = page.Locator(".wb-checkout");
        var row = checkout.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();
        await row.ClickAsync();

        var ribbonButton = checkout.Locator("[data-doc-action='make-searchable']");
        await Expect(ribbonButton).ToBeEnabledAsync();
        await Expect(checkout.Locator("table").GetByRole(AriaRole.Button, new() { Name = "Make searchable" })).ToHaveCountAsync(0);
        await ribbonButton.ClickAsync();
        await Expect(page.GetByText("Conversion queued").First).ToBeVisibleAsync();

        // Clean up so the shared fixture is not left holding a lock.
        await row.Locator("button.mud-icon-button").Last.ClickAsync();
        await page.Locator(".mud-menu-item").Filter(new() { HasText = "Cancel check-out" }).First.ClickAsync();
        await page.Locator(".mud-dialog").GetByRole(AriaRole.Button, new() { Name = "Cancel check-out" }).ClickAsync();
    }
}
