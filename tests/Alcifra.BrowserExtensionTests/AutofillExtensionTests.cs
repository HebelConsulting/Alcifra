using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.BrowserExtensionTests;

/// <summary>
/// The extension as a user meets it: focus a password field, pick what it offers, and keep a login typed by hand —
/// with the real host protocol behind it and a fake store behind that (ADR 0980). Each test gets its own browser
/// and profile, so no test sees another's session storage or calls.
/// </summary>
public sealed class AutofillExtensionTests : IDisposable
{
    private const string Sapass = "sapass";
    private const string Desktop = "desktop";

    /// <summary>The keep-alive interval the keep-alive tests patch in (20 s ships), so several ticks take seconds.</summary>
    private static readonly TimeSpan KeepAliveTick = TimeSpan.FromSeconds(1);

    private static readonly FakeEntry Mail = new(
        Guid.Parse("0b6f5c1e-4f1d-4c55-9a39-1f4c2d9e7a10"), "localhost", "Mail login", "My Passwords", "anna@example.test", "correct horse battery");

    private readonly LoginSite _site = new();

    static AutofillExtensionTests() => SetDefaultExpectTimeout(15_000);

    public void Dispose() => _site.Dispose();

    [Fact]
    public async Task A_matching_entry_is_offered_on_focus_and_fills_the_login_when_chosen()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([Mail], Sapass);
        var page = await OpenLoginAsync(browser, "localhost");

        await page.Locator("#pass").ClickAsync();
        var chip = page.Locator(".sa-chip");
        await Expect(chip).ToHaveTextAsync("Alcifra (1)");
        await chip.ClickAsync();
        await page.Locator(".sa-menu button", new() { HasText = Mail.Name }).ClickAsync();

        await Expect(page.Locator("#user")).ToHaveValueAsync(Mail.UserName!);
        await Expect(page.Locator("#pass")).ToHaveValueAsync(Mail.Password);
        await Expect(page.Locator(".sa-menu")).ToHaveCountAsync(0);

        // The desktop's name was asked first and is not registered here, so apass's host answered both requests.
        Assert.Equal(
            [("match", "localhost", (Guid?)null), ("open", null, Mail.Id)],
            browser.Calls.Select(c => (c.Command, c.Site, c.EntryId)));
        Assert.All(browser.Calls, c => Assert.Equal(Sapass, c.Host));
    }

    [Fact]
    public async Task Generate_puts_a_fresh_password_into_the_field_and_leaves_the_user_name_alone()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([Mail], Sapass);
        var page = await OpenLoginAsync(browser, "localhost");

        await page.Locator("#pass").ClickAsync();
        await page.Locator(".sa-chip").ClickAsync();
        await page.Locator(".sa-menu button", new() { HasText = "Generate a strong password" }).ClickAsync();

        await Expect(page.Locator("#pass")).Not.ToHaveValueAsync(string.Empty);
        var generated = Assert.Single(browser.Calls, c => c.Command == "generate");
        Assert.Equal(24, generated.Password!.Length);
        await Expect(page.Locator("#pass")).ToHaveValueAsync(generated.Password);
        await Expect(page.Locator("#user")).ToHaveValueAsync(string.Empty);
    }

    [Fact]
    public async Task A_page_with_no_matching_entry_offers_no_entry()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([Mail], Sapass);
        var page = await OpenLoginAsync(browser, "127.0.0.1");

        await page.Locator("#pass").ClickAsync();
        var chip = page.Locator(".sa-chip");
        await Expect(chip).ToHaveTextAsync("Alcifra: no match");
        await chip.ClickAsync();

        // Only the generator is on offer; nothing was opened, and the fields stay empty.
        await Expect(page.Locator(".sa-menu button")).ToHaveTextAsync(["Generate a strong password"]);
        Assert.Equal([("match", "127.0.0.1")], browser.Calls.Select(c => (c.Command, c.Site)));
        await Expect(page.Locator("#user")).ToHaveValueAsync(string.Empty);
        await Expect(page.Locator("#pass")).ToHaveValueAsync(string.Empty);
    }

    [Fact]
    public async Task A_submitted_login_is_offered_on_the_next_page_and_saving_reaches_the_store()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([], Sapass);
        var page = await OpenLoginAsync(browser, "127.0.0.1");

        await SubmitAsync(page, "tom@example.test", "typed by hand 42");

        var prompt = page.Locator(".sa-menu", new() { HasText = "Keep this login in Alcifra?" });
        await Expect(prompt).ToBeVisibleAsync();
        await prompt.Locator("button", new() { HasText = "Save to My Passwords" }).ClickAsync();
        await Expect(prompt).ToHaveCountAsync(0);

        var save = Assert.Single(browser.Calls, c => c.Command == "save");
        Assert.Equal(("127.0.0.1", "tom@example.test", "typed by hand 42"), (save.Site, save.UserName, save.Password));

        // Kept once: the pending login is gone, so the next visit asks nothing.
        await page.ReloadAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Welcome");
        await Expect(page.Locator(".sa-menu")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Not_now_drops_the_submitted_login_without_saving_it()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([], Sapass);
        var page = await OpenLoginAsync(browser, "127.0.0.1");

        await SubmitAsync(page, "tom@example.test", "typed by hand 42");
        var prompt = page.Locator(".sa-menu", new() { HasText = "Keep this login in Alcifra?" });
        await prompt.Locator("button", new() { HasText = "Not now" }).ClickAsync();
        await Expect(prompt).ToHaveCountAsync(0);

        await page.GotoAsync(_site.Welcome("127.0.0.1"));
        await Expect(page.Locator("h1")).ToHaveTextAsync("Welcome");
        await Expect(page.Locator(".sa-menu")).ToHaveCountAsync(0);
        Assert.DoesNotContain(browser.Calls, c => c.Command == "save");
    }

    /// <summary>
    /// The defect ADR 0980 records, and the keep-alive that fixes it. The browser stops an idle extension worker after
    /// about 30 seconds, and a submit's message to a stopped worker is dropped when the page navigates before the
    /// worker has started — the login was never offered for saving. Here the worker is stopped AFTER the field holds
    /// a value and BEFORE the submit; nothing but the page's keep-alive talks to the worker in between (the next test
    /// is the control: without a value, it stays stopped), so the keep-alive is what has it running when the submit
    /// comes. Without the keep-alive this test fails at the wait for the worker, and fails at the prompt if the wait
    /// is removed.
    /// </summary>
    [Fact]
    public async Task A_login_submitted_while_the_service_worker_is_stopped_is_still_offered()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([], KeepAliveTick, Sapass);
        var page = await OpenLoginAsync(browser, "127.0.0.1");
        await page.Locator("#user").FillAsync("tom@example.test");
        await page.Locator("#pass").FillAsync("typed by hand 42");
        await Expect(page.Locator(".sa-chip")).ToBeVisibleAsync();

        await using var worker = await browser.ProbeServiceWorkerAsync(page);
        await worker.StopAsync();
        await worker.WaitUntilRunningAsync(KeepAliveTick * 5);

        await page.Locator("#go").ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Welcome");
        await Expect(page.Locator(".sa-menu", new() { HasText = "Keep this login in Alcifra?" })).ToBeVisibleAsync();
    }

    /// <summary>
    /// The keep-alive ends with the value: once the password field is cleared, a stopped worker stays stopped through
    /// several keep-alive intervals, so a form nobody is filling does not hold the worker up.
    /// </summary>
    [Fact]
    public async Task The_keep_alive_stops_when_the_password_field_is_cleared()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([], KeepAliveTick, Sapass);
        var page = await OpenLoginAsync(browser, "127.0.0.1");
        await page.Locator("#user").FillAsync("tom@example.test");
        await page.Locator("#pass").FillAsync("typed by hand 42");
        await Expect(page.Locator(".sa-chip")).ToBeVisibleAsync();
        await page.Locator("#pass").FillAsync(string.Empty);

        await using var worker = await browser.ProbeServiceWorkerAsync(page);
        await worker.StopAsync();
        await Task.Delay(KeepAliveTick * 4);

        Assert.False(worker.IsRunning, "Something restarted the worker after the password field was cleared.");
    }

    [Fact]
    public async Task With_both_clients_registered_the_desktop_answers()
    {
        await using var browser = await ExtensionBrowser.LaunchAsync([Mail], Sapass, Desktop);
        var page = await OpenLoginAsync(browser, "localhost");

        await page.Locator("#pass").ClickAsync();
        await Expect(page.Locator(".sa-chip")).ToHaveTextAsync("Alcifra (1)");

        var match = Assert.Single(browser.Calls);
        Assert.Equal(("match", Desktop), (match.Command, match.Host));
    }

    private Task<IPage> OpenLoginAsync(ExtensionBrowser browser, string host) => browser.OpenAsync(_site.Login(host));

    /// <summary>Types a login and submits it, landing on the page that follows.</summary>
    /// <remarks>
    /// Waits for the offer on the password field before submitting, as a person would see it. A freshly started
    /// browser's worker is not always running yet, and the keep-alive's first ping races the submit at test speed;
    /// the offer's round trip proves the worker is up. The stopped-worker case is its own test.
    /// </remarks>
    private static async Task SubmitAsync(IPage page, string userName, string password)
    {
        await page.Locator("#user").FillAsync(userName);
        await page.Locator("#pass").FillAsync(password);
        await Expect(page.Locator(".sa-chip")).ToBeVisibleAsync();
        await page.Locator("#go").ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync("Welcome");
    }
}
