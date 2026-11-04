using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// A version over 10 MB waits for Open (ADR 1048): the message and the button first, the preview only after the click, and
// the click is not remembered. Through a real upload, so the size has to arrive on the version resource.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebLargePreviewTests(SelfHostedAppFixture app)
{
    [Fact]
    public async Task A_video_fetches_nothing_until_play_and_a_format_browsers_cannot_play_says_so()
    {
        var page = await Ui.LoginAsync(app);
        var video = $"clip-{Guid.NewGuid():N}"[..13];
        var other = $"oldc-{Guid.NewGuid():N}"[..13];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        foreach (var name in new[] { $"{video}.mp4", $"{other}.avi" })
        {
            var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
                await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync());
            await chooser.SetFilesAsync(new FilePayload { Name = name, MimeType = "application/octet-stream", Buffer = System.Security.Cryptography.RandomNumberGenerator.GetBytes(4096) });
            await Expect(list.GetByText(name[..13]).First).ToBeVisibleAsync();
        }

        var fetched = new List<string>();
        page.Request += (_, request) =>
        {
            if (request.Method == "GET" && request.Url.Contains("/content", StringComparison.Ordinal))
            {
                fetched.Add(request.Url);
            }
        };

        var preview = page.Locator(".wb-preview");
        await list.GetByText(video).First.ClickAsync();
        await Expect(preview.Locator("video[preload='none']")).ToHaveCountAsync(1);
        Assert.Empty(fetched);

        await list.GetByText(other).First.ClickAsync();
        await Expect(preview.Locator(".wb-pv-media-pane")).ToContainTextAsync("open it in an application");
        await Expect(preview.Locator("video")).ToHaveCountAsync(0);
        Assert.Empty(fetched);
    }

    [Fact]
    public async Task A_large_version_shows_the_message_and_loads_only_after_Open()
    {
        var page = await Ui.LoginAsync(app);
        var large = $"large-{Guid.NewGuid():N}"[..14];
        var small = $"small-{Guid.NewGuid():N}"[..14];
        var list = page.Locator("[data-pane='list']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        foreach (var (name, content) in new[]
        {
            (large, $"first line of the large file\n{new string('x', 11 * 1024 * 1024)}"),
            (small, "first line of the small file"),
        })
        {
            var chooser = await page.RunAndWaitForFileChooserAsync(async () =>
                await page.Locator(".wb-ribbon [aria-label=\"Upload\"]").First.ClickAsync());
            await chooser.SetFilesAsync(new FilePayload { Name = $"{name}.txt", MimeType = "text/plain", Buffer = Encoding.UTF8.GetBytes(content) });
            await Expect(list.GetByText(name).First).ToBeVisibleAsync();
        }

        var preview = page.Locator(".wb-preview");
        await list.GetByText(large).First.ClickAsync();
        await Expect(preview.Locator(".wb-pv-large")).ToContainTextAsync("File size more than 10 MB");
        await Expect(preview).Not.ToContainTextAsync("first line of the large file");

        await preview.Locator(".wb-pv-large-open").ClickAsync();
        await Expect(preview).ToContainTextAsync("first line of the large file");
        await Expect(preview.Locator(".wb-pv-large")).ToHaveCountAsync(0);

        await list.GetByText(small).First.ClickAsync();
        await Expect(preview).ToContainTextAsync("first line of the small file");
        await Expect(preview.Locator(".wb-pv-large")).ToHaveCountAsync(0);

        await list.GetByText(large).First.ClickAsync();
        await Expect(preview.Locator(".wb-pv-large")).ToHaveCountAsync(1);
    }
}
