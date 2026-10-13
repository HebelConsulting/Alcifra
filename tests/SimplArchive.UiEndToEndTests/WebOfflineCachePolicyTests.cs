using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web Tenant tab's "Offline cache (phone app)" group (#1742, ADR 0923), as the desktop has it: the group's pencil,
// a change, Save — and the policy resource says so. It changes the shared demo tenant's policy, so it restores the
// defaults afterwards through the API (ADR 0884's wart, said here).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebOfflineCachePolicyTests
{
    private readonly SelfHostedAppFixture _app;

    public WebOfflineCachePolicyTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_group_saves_the_policy_through_its_pencil()
    {
        using var api = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var href = (await api.GetFromJsonAsync<JsonElement>("api/me")).GetProperty("links").EnumerateArray()
            .First(l => l.GetProperty("rel").GetString() == "offlineCachePolicy").GetProperty("href").GetString()!.TrimStart('/');

        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label=\"Tenant\"]").First.ClickAsync();
        var view = page.Locator(".wb-tenant");
        // A substring filter: no other group's name contains it, and an anchored one stops matching once the header
        // shows Save/Cancel instead of the pencil (the first run waited a minute for exactly that).
        var head = view.Locator(".wb-tenant-group-head").Filter(new() { HasText = "Offline cache (phone app)" });
        await Expect(head).ToBeVisibleAsync();

        try
        {
            await head.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
            await view.GetByText("Folders the user chooses", new() { Exact = true }).ClickAsync();   // on by default: now off
            await head.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
            await Expect(head.GetByRole(AriaRole.Button, new() { Name = "Edit" })).ToBeVisibleAsync();   // back to read-only

            var saved = await api.GetFromJsonAsync<JsonElement>(href);
            Assert.False(saved.GetProperty("chosenFolders").GetBoolean());
            Assert.True(saved.GetProperty("notifications").GetBoolean());   // only what was changed changed
        }
        finally
        {
            using var read = await api.GetAsync(href);
            using var restore = new HttpRequestMessage(HttpMethod.Put, href)
            {
                Content = JsonContent.Create(new
                {
                    allowed = true,
                    notifications = true,
                    openedDocuments = true,
                    chosenFolders = true,
                    unlockMode = "UserChooses",
                    refreshMode = "UserChooses",
                    maxUnlockedMinutes = 15,
                }),
            };
            restore.Headers.TryAddWithoutValidation("If-Match", read.Headers.ETag?.Tag);
            (await api.SendAsync(restore)).EnsureSuccessStatusCode();
        }
    }
}
