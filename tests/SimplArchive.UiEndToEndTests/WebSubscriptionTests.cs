using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of document subscriptions (ADR "Document subscriptions"): the Follow toggle subscribes/unsubscribes
// the current user, cross-checked against the backend subscription state. Since ADR 0970 it lives in the ribbon's
// Documents group and the row's ⋮ menu, and no longer in the details pane.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebSubscriptionTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSubscriptionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Follow_toggle_subscribes_and_unsubscribes()
    {
        var name = $"sub-{Guid.NewGuid().ToString("N")[..8]}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var v = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" })).Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(v.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes("content")))).EnsureSuccessStatusCode();
        }
        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{v.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();

        async Task<bool> SubscribedAsync() =>
            (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}/subscription")).GetProperty("subscribed").GetBoolean();

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        await page.Locator("[data-pane='list'] .wb-list-row").Filter(new() { HasText = name }).ClickAsync();

        var follow = DocumentActionsUi.Ribbon(page, "follow");
        await Expect(follow).ToBeEnabledAsync();
        // The details pane keeps only the details' own actions (ADR 0970).
        await Expect(DocumentActionsUi.DetailPane(page).GetByRole(AriaRole.Button, new() { Name = "Follow" })).ToHaveCountAsync(0);

        // Follow → the backend records the subscription.
        //
        // WAITING ON THE SUBSCRIPTION, NOT ON THE TOAST. The snackbar was never the subject here; it was a
        // synchronisation point that happened to appear when the POST finished. It is also transient — rendered,
        // then removed on a timer — so a slow run missed the window and failed a test whose actual subject had
        // succeeded, naming the toast rather than anything a reader could act on. This waits for the fact.
        await follow.ClickAsync();
        await Ui.EventuallyAsync(SubscribedAsync, "the Follow click should have recorded a subscription");

        // Unfollow → the subscription is removed.
        await follow.ClickAsync();
        await Ui.EventuallyAsync(async () => !await SubscribedAsync(), "the second click should have removed it");

        // The same toggle from the row's ⋮ menu, labelled by what it will do.
        var items = await DocumentActionsUi.OpenRowMenuAsync(page, name);
        await items.Filter(new() { HasText = "Follow" }).First.ClickAsync();
        await Ui.EventuallyAsync(SubscribedAsync, "the menu's Follow should have recorded a subscription");
        items = await DocumentActionsUi.OpenRowMenuAsync(page, name);
        await items.Filter(new() { HasText = "Unfollow" }).First.ClickAsync();
        await Ui.EventuallyAsync(async () => !await SubscribedAsync(), "the menu's Unfollow should have removed it");
    }
}
