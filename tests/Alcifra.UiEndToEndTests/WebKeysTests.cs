using System.Net.Http.Headers;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// "My keys" (#2278, ADR 1083): the account menu lists the caller's enrolled keys, and one is revoked with a reason the
// dialog asks for. The server's rules (who may, the reason, the notification) are the E2E suite's.
//
// A WART, said so: enrolling switches the shared demo tenant's certificate settings on for the test (TestKeyEnrolment).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-1")]
public class WebKeysTests
{
    private readonly SelfHostedAppFixture _app;

    public WebKeysTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task My_keys_revokes_a_key_with_the_reason_given()
    {
        var label = $"Card {Guid.NewGuid():N}"[..13];
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        await using var enrolment = await Alcifra.TestSupport.TestKeyEnrolment.EnrolAsync(http, SelfHostedAppFixture.AdminEmail, label);

        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("My keys…").ClickAsync();
        var row = page.Locator(".wb-enrolled-key", new() { HasText = label });
        await row.GetByRole(AriaRole.Button, new() { Name = "Revoke…" }).ClickAsync();

        // The reason is asked, never assumed: Revoke stays off until one is chosen, and "Other" needs words.
        var dialog = page.Locator(".mud-dialog").Last;
        await Expect(dialog.GetByText("Enrol the new key first", new() { Exact = false })).ToBeVisibleAsync();
        var revoke = dialog.GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true });
        await Expect(revoke).ToBeDisabledAsync();
        await dialog.GetByText("Other", new() { Exact = true }).ClickAsync();
        await Expect(revoke).ToBeDisabledAsync();
        await dialog.GetByText("Lost", new() { Exact = true }).ClickAsync();
        await revoke.ClickAsync();

        await Expect(row).ToContainTextAsync("Lost");
        await Expect(row.GetByRole(AriaRole.Button, new() { Name = "Revoke…" })).ToHaveCountAsync(0);
    }

    // The administrator's path: the Users & groups tab's Keys… on the selected user, from that user's `certificates` rel.
    [Fact]
    public async Task A_users_keys_are_listed_and_revoked_from_the_users_tab()
    {
        var label = $"Card {Guid.NewGuid():N}"[..13];
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        await using var enrolment = await Alcifra.TestSupport.TestKeyEnrolment.EnrolAsync(http, SelfHostedAppFixture.AdminEmail, label);

        var page = await Ui.LoginAsync(_app);
        await page.Locator(".wb-tab[aria-label=\"Users & groups\"]").First.ClickAsync();
        await page.Locator(".wb-ug-rows").GetByText("Demo Admin", new() { Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Keys…" }).ClickAsync();

        var row = page.Locator(".wb-enrolled-key", new() { HasText = label });
        await row.GetByRole(AriaRole.Button, new() { Name = "Revoke…" }).ClickAsync();
        var dialog = page.Locator(".mud-dialog").Last;
        await dialog.GetByText("Replaced by a new key", new() { Exact = true }).ClickAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Revoke", Exact = true }).ClickAsync();

        await Expect(row).ToContainTextAsync("Replaced by a new key");
    }
}
