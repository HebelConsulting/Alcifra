using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// The web half of protocol devices (#1970 slice 3): the account menu's "Devices…" dialog, the web's mirror of the
// desktop's Devices window (ADR 0511), which replaced the separate WebDAV and IMAP access dialogs. Enrolling hands a
// device its password once, with every server address beside it; the Intray and Check-out tabs copy their own
// folder's WebDAV address once some device reaches files. The protocols themselves are covered by the E2E suites.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebDevicesTests
{
    private readonly SelfHostedAppFixture _app;

    public WebDevicesTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Enrolling_a_device_shows_its_password_once_and_lists_it()
    {
        var page = await Ui.LoginAsync(_app);
        var name = $"ui-{Guid.NewGuid():N}"[..12];

        var devices = await OpenDevicesAsync(page);
        await Expect(devices.GetByText("Mail shows all documents, not only emails")).ToBeVisibleAsync();

        // Every value a device is set up with has its own Copy (#461); WebDAV, CalDAV and CardDAV at least.
        await Expect(devices.GetByRole(AriaRole.Textbox, new() { Name = "User name" })).ToBeVisibleAsync();
        Assert.True(await devices.Locator(".wb-device-server-line").CountAsync() >= 4);

        // A browser cannot mount (ADR 0560), so it says how; asserted as the heading plus some OS's guidance, since
        // the steps are chosen from the visitor's platform (macOS locally, Linux in CI).
        await Expect(devices.GetByText("How to mount it")).ToBeVisibleAsync();
        await Expect(devices.Locator(".wb-device-mount-steps")).Not.ToBeEmptyAsync();

        await EnrolAsync(page, name);

        // The hand-over closed, the list shows the new device, and its password is nowhere on the page any more.
        await Expect(devices.Locator(".wb-device-row", new() { HasText = name })).ToBeVisibleAsync();
        await Expect(page.GetByText("This password is shown once")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task A_device_is_revoked_after_confirming()
    {
        var page = await Ui.LoginAsync(_app);
        var name = $"ui-{Guid.NewGuid():N}"[..12];

        var devices = await OpenDevicesAsync(page);
        await EnrolAsync(page, name);

        var row = devices.Locator(".wb-device-row", new() { HasText = name });
        await row.GetByRole(AriaRole.Button, new() { Name = "Revoke" }).ClickAsync();
        await page.Locator(".mud-message-box").GetByRole(AriaRole.Button, new() { Name = "Revoke" }).ClickAsync();

        await Expect(row).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task The_ribbon_button_opens_the_devices_dialog()
    {
        var page = await Ui.LoginAsync(_app);

        // The ribbon is the discoverable entry point for files (#461): it opens the same dialog the account menu does.
        await page.Locator("[data-tour='action-webdav']").ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Enrol a device…" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Intray_tab_copies_the_personal_webdav_url()
    {
        var page = await Ui.LoginAsync(_app, new[] { "clipboard-read", "clipboard-write" });
        await EnsureADeviceReachesFilesAsync(page);

        // Intray tab → Copy WebDAV URL → the address of THIS TAB'S folder inside the single mount (ADR 0509).
        // Deep-linked rather than the bare mount root: the desktop's button opens the tab's own folder, and
        // handing over its address is the nearest thing a browser is allowed to do (ADR 0560).
        await page.Locator(".wb-tab[aria-label=\"Intray\"]").First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy WebDAV URL" }).ClickAsync();
        await Expect(page.GetByText("Copied WebDAV URL")).ToBeVisibleAsync();
        // The space is addressed by its OWNER's name, not the literal "Personal" — that is what the mount
        // actually serves, and a client that composed the placeholder produced a path nothing answers.
        var personal = Uri.EscapeDataString(SelfHostedAppFixture.AdminDisplayName);
        Assert.EndsWith($"/{(global::Alcifra.Presentation.WireNames.WebDavSegment)}/{personal}/Intray", await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"));
    }

    // The two tabs must copy DIFFERENT addresses — that is the whole point of the deep link, and a shared
    // handler that ignored its argument would pass the Intray test above while silently doing nothing here.
    [Fact]
    public async Task The_checkout_tab_copies_its_own_folder_not_the_intray_one()
    {
        var page = await Ui.LoginAsync(_app, new[] { "clipboard-read", "clipboard-write" });
        await EnsureADeviceReachesFilesAsync(page);

        await page.Locator(".wb-tab[aria-label=\"Check-out\"]").First.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Copy WebDAV URL" }).ClickAsync();
        await Expect(page.GetByText("Copied WebDAV URL")).ToBeVisibleAsync();
        var personal = Uri.EscapeDataString(SelfHostedAppFixture.AdminDisplayName);
        Assert.EndsWith($"/{(global::Alcifra.Presentation.WireNames.WebDavSegment)}/{personal}/Check-out", await page.EvaluateAsync<string>("() => navigator.clipboard.readText()"));
    }

    private static async Task<ILocator> OpenDevicesAsync(IPage page)
    {
        await page.Locator(".wb-userbox").ClickAsync();
        await page.GetByText("Devices…").ClickAsync();
        var dialog = page.Locator(".mud-dialog").First;
        await Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Enrol a device…" })).ToBeVisibleAsync();
        return dialog;
    }

    // Enrols a device with every switch on (the form's default) from the open Devices dialog, then closes the hand-over.
    private static async Task EnrolAsync(IPage page, string name)
    {
        await page.GetByRole(AriaRole.Button, new() { Name = "Enrol a device…" }).ClickAsync();
        var form = page.Locator(".mud-dialog").Last;
        await form.GetByRole(AriaRole.Textbox, new() { Name = "Name", Exact = true }).FillAsync(name);
        await form.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();

        var handOver = page.Locator(".mud-dialog").Last;
        await Expect(handOver.GetByText("This password is shown once")).ToBeVisibleAsync();
        await Expect(handOver.Locator(".wb-device-password-qr")).ToBeVisibleAsync();
        await handOver.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        await Expect(page.GetByText("This password is shown once")).ToHaveCountAsync(0);
    }

    private static async Task EnsureADeviceReachesFilesAsync(IPage page)
    {
        var devices = await OpenDevicesAsync(page);
        await EnrolAsync(page, $"ui-{Guid.NewGuid():N}"[..12]);
        await devices.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        await Expect(page.Locator(".mud-dialog")).ToHaveCountAsync(0);
    }
}
