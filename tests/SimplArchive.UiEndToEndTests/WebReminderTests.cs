using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The web half of document reminders (ADR "Document reminders"): the detail-pane Remind button opens the
// dialog; setting a reminder (the date defaults to tomorrow) persists it, cross-checked against the backend.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebReminderTests
{
    private readonly SelfHostedAppFixture _app;

    public WebReminderTests(SelfHostedAppFixture app) => _app = app;

    // The browser runs in Europe/Zurich whatever zone the machine is in, and the stored instant must read 09:00 on
    // TOMORROW's calendar date there. #1909: the day before a clock change (24 October 2026) the dialog built that
    // instant with TODAY's offset and, the default date being DateTimeKind.Local, the constructor threw, so the
    // click stored nothing. The rule itself is pinned on both change days in ReminderScheduleTests. The browser
    // keeps the real clock here on purpose: a pinned one (Playwright's page clock) does not survive this app's
    // sign-in. Pinned months back, the workbench never loads its list; pinned ahead, every call bounces back to
    // the login, because the SPA's token was stamped by the real clock.
    [Fact]
    public async Task Set_a_reminder_from_the_detail_pane()
    {
        var name = $"rem-{Guid.NewGuid().ToString("N")[..8]}";

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

        async Task<JsonElement> RemindersAsync() =>
            (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}/reminders")).GetProperty("reminders");

        Assert.Equal(0, (await RemindersAsync()).GetArrayLength());

        var page = await Ui.LoginAsync(_app, configureContext: o => o.TimezoneId = "Europe/Zurich");
        await page.GetByText("Demo Repository").First.ClickAsync();
        await page.Locator("[data-pane='list'] .wb-list-row").Filter(new() { HasText = name }).ClickAsync();

        // Open the Remind dialog (the date defaults to tomorrow) and set the reminder.
        await page.Locator("[data-pane='index']").GetByRole(AriaRole.Button, new() { Name = "Remind" }).ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Set reminder" }).ClickAsync();

        // The backend now has the reminder — waited for, not sampled once. The "Reminder set." snackbar used to
        // stand in for this and is what made the test unreliable: the dialog opened and the click landed, and
        // only the transient toast went unobserved.
        JsonElement stored = default;
        await Ui.EventuallyAsync(
            async () => (stored = await RemindersAsync()).GetArrayLength() == 1,
            "the Set reminder click should have stored one reminder");

        // The expectation is the CALENDAR: tomorrow's date and the 09:00 the dialog shows, read back in Zurich.
        var zurich = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Zurich");
        var tomorrow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zurich).DateTime).AddDays(1);
        var there = TimeZoneInfo.ConvertTime(stored[0].GetProperty("remindAt").GetDateTimeOffset(), zurich).DateTime;
        Assert.Equal(tomorrow, DateOnly.FromDateTime(there));
        Assert.Equal(new TimeOnly(9, 0), TimeOnly.FromDateTime(there));
    }
}
