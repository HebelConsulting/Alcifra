using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The check-in comment dialog (ADR 0952), opened for real: the ribbon's Check in asks for the optional comment
// first, Confirm files it on the new version, Cancel leaves the document checked out. Each test creates, checks
// out and stashes its own run-unique document through the API (ADR 0884), so the row reads Modified when the
// tab is opened.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebCheckoutCommentTests
{
    private readonly SelfHostedAppFixture _app;

    public WebCheckoutCommentTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Check_in_asks_for_a_comment_and_the_new_version_carries_it()
    {
        var page = await Ui.LoginAsync(_app);
        using var http = await ApiAsync();
        var name = $"cicom-{Guid.NewGuid():N}"[..14];
        var documentId = await CheckedOutWithStashAsync(http, name);

        var dialog = await OpenCommentDialogAsync(page, name);
        await FillMudAsync(dialog.Locator("input"), "  Fixed the totals  ");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Check in", Exact = true }).ClickAsync();

        // The row leaves the tab once the check-in released the lock…
        await Expect(page.Locator(".wb-checkout .wb-list-row").Filter(new() { HasText = name })).Not.ToBeVisibleAsync();

        // …and the new version carries the comment, trimmed.
        var versions = await http.GetFromJsonAsync<JsonElement>($"/api/documents/{documentId}/versions");
        var latest = versions.GetProperty("versions").EnumerateArray().OrderByDescending(v => v.GetProperty("versionNumber").GetInt32()).First();
        Assert.Equal(2, latest.GetProperty("versionNumber").GetInt32());
        Assert.Equal("Fixed the totals", latest.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task Cancelling_the_comment_dialog_leaves_the_document_checked_out()
    {
        var page = await Ui.LoginAsync(_app);
        using var http = await ApiAsync();
        var name = $"cican-{Guid.NewGuid():N}"[..14];
        var documentId = await CheckedOutWithStashAsync(http, name);

        var dialog = await OpenCommentDialogAsync(page, name);
        await FillMudAsync(dialog.Locator("input"), "never sent");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Expect(dialog).Not.ToBeVisibleAsync();

        // Still listed, still checked out, and no second version was filed.
        await Expect(page.Locator(".wb-checkout .wb-list-row").Filter(new() { HasText = name })).ToBeVisibleAsync();
        var document = await http.GetFromJsonAsync<JsonElement>($"/api/documents/{documentId}");
        Assert.Equal(JsonValueKind.Object, document.GetProperty("checkedOut").ValueKind);
        var versions = await http.GetFromJsonAsync<JsonElement>($"/api/documents/{documentId}/versions");
        Assert.Single(versions.GetProperty("versions").EnumerateArray());
    }

    // The Check-out tab, the row selected, the ribbon's Check in clicked: answers the dialog that opens.
    private static async Task<ILocator> OpenCommentDialogAsync(IPage page, string name)
    {
        await page.Locator(".wb-tab[aria-label=\"Check-out\"]").First.ClickAsync();
        var checkout = page.Locator(".wb-checkout");
        var row = checkout.Locator(".wb-list-row").Filter(new() { HasText = name });
        await Expect(row).ToBeVisibleAsync();
        await Expect(row.GetByText("Modified")).ToBeVisibleAsync();
        await row.ClickAsync();

        await checkout.Locator("[aria-label=\"Check in\"]").First.ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog.GetByLabel("Comment (optional)")).ToBeVisibleAsync();
        return dialog;
    }

    private async Task<HttpClient> ApiAsync()
    {
        var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        return http;
    }

    // A document of its own with one confirmed version, checked out, with an edited working copy in the stash.
    private static async Task<Guid> CheckedOutWithStashAsync(HttpClient http, string name)
    {
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var created = await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>();
        var documentId = created.GetProperty("id").GetGuid();

        var version = await (await http.PostAsJsonAsync($"/api/documents/{documentId}/versions", new { fileExtension = ".txt" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        using var storage = new HttpClient();
        (await storage.PutAsync(version.GetProperty("uploadUrl").GetString(), new ByteArrayContent(Encoding.UTF8.GetBytes("v1")))).EnsureSuccessStatusCode();
        (await http.PutAsJsonAsync($"/api/documents/{documentId}/versions/{version.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
        (await http.PutAsync($"/api/documents/{documentId}/checkout", null)).EnsureSuccessStatusCode();

        var workingCopy = await (await http.PutAsJsonAsync($"/api/documents/{documentId}/checkout/working-copy", new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        (await storage.PutAsync(workingCopy.GetProperty("uploadUrl").GetString(), new ByteArrayContent(Encoding.UTF8.GetBytes("v2 edited")))).EnsureSuccessStatusCode();
        return documentId;
    }

    // MudTextField commits its bound value on blur — fill then blur so the value reaches the model.
    private static async Task FillMudAsync(ILocator field, string value)
    {
        await field.FillAsync(value);
        await field.EvaluateAsync("el => el.blur()");
    }
}
