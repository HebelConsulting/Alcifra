using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// A contact shows as a card in the Repositories preview, chosen by its mask (#2215): the editor's own form, read-only, and a
// pencil that opens the editor and shows the card again after Save, filling the pane, with its stored vCard in a collapsed
// section (#2274). A .vcf the archive filed as a Basic Entry gets no card.
// The desktop's DesktopStructuredCardTests is its twin.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebPreviewCardTests(SelfHostedAppFixture app)
{
    [Fact]
    public async Task A_contact_shows_as_a_card_and_its_pencil_edits_it()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var book = await FolderAsync(http, repoId, $"Book-{run}", "addressbook");
        var plain = await FolderAsync(http, repoId, $"Plain-{run}", "folder");
        var vcard = $"BEGIN:VCARD\r\nVERSION:4.0\r\nUID:web-card-{run}\r\nFN:Anna {run}\r\nN:{run};Anna;;;\r\nORG:Example AG\r\nEND:VCARD\r\n";
        await FileAsync(http, book, $"Anna-{run}", ".vcf", vcard);
        await FileAsync(http, plain, $"Loose-{run}", ".vcf", vcard);

        var page = await Ui.LoginAsync(app);
        var list = page.Locator("[data-pane='list']");
        var preview = page.Locator("[data-pane='preview']");
        await Expect(page.Locator("[data-pane='tree']")).ToBeVisibleAsync(new() { Timeout = 60000 });
        await page.Locator("[data-pane='tree']").GetByText("Demo Repository").First.ClickAsync();
        await list.Locator(".wb-list-row").Filter(new() { HasText = $"Book-{run}" }).First.DblClickAsync();
        // An addressbook lists a contact by its own name.
        await list.Locator(".wb-list-row").Filter(new() { HasText = $"Anna {run}" }).First.ClickAsync();

        // The card: the form, read-only.
        var card = preview.Locator("[data-preview-card]");
        await Expect(card).ToBeVisibleAsync();
        await Expect(card.Locator("input").First).ToHaveValueAsync("Anna");
        await Expect(card.Locator("input").First).ToHaveAttributeAsync("readonly", string.Empty);

        // It fills the pane (#2274): its width is the preview pane's, not a 640-pixel column at the left. Measured in a wide
        // window, since in a pane narrower than the old cap a capped card fills it too and the check proves nothing.
        await page.SetViewportSizeAsync(2400, 1000);
        await Expect(card).ToBeVisibleAsync();
        var paneBox = (await preview.BoundingBoxAsync())!;
        Assert.True(paneBox.Width > 800, $"the preview pane is only {paneBox.Width:0} px wide, so the card's width says nothing");
        var cardBox = (await card.BoundingBoxAsync())!;
        Assert.True(cardBox.Width >= paneBox.Width - 24, $"the card is {cardBox.Width:0} px wide in a pane of {paneBox.Width:0} px");

        // The stored vCard (#2274): collapsed, then opened, verbatim.
        var source = card.Locator("[data-preview-card-source]");
        await Expect(source).ToContainTextAsync("vCard source");
        await Expect(source.Locator("[data-preview-card-source-text]")).Not.ToBeVisibleAsync();
        await source.GetByText("vCard source").ClickAsync();
        await Expect(source.Locator("[data-preview-card-source-text]")).ToContainTextAsync($"UID:web-card-{run}");
        await Expect(source.Locator("[data-preview-card-source-text]")).ToContainTextAsync("BEGIN:VCARD");
        await Expect(source.Locator("[data-preview-card-source-copy]")).ToBeEnabledAsync();

        // The pencil: the editor dialog, and the card again after Save.
        await card.Locator("[data-preview-card-edit]").ClickAsync();
        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        var organization = dialog.Locator(".mud-input-control").Filter(new() { HasText = "Organization" }).Locator("input");
        await organization.FillAsync($"Renamed {run}");
        await organization.EvaluateAsync("el => el.blur()");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Expect(card.Locator(".mud-input-control").Filter(new() { HasText = "Organization" }).Locator("input")).ToHaveValueAsync($"Renamed {run}");

        // A .vcf in an ordinary folder: no card.
        await page.Locator("[data-pane='tree']").GetByText("Demo Repository").First.ClickAsync();
        await list.Locator(".wb-list-row").Filter(new() { HasText = $"Plain-{run}" }).First.DblClickAsync();
        await list.GetByText($"Loose-{run}").First.ClickAsync();
        await Expect(page.Locator("[data-pane='index']")).ToContainTextAsync($"Loose-{run}");
        await Expect(preview.Locator("[data-preview-card]")).ToHaveCountAsync(0);
    }

    // A mail previews as its card (ADR 1073): the envelope, the sanitised HTML in a sandboxed frame, and its attachments.
    [Fact]
    public async Task A_mail_shows_as_its_card_with_its_html_in_a_sandboxed_frame()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(app.BaseUrl));
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var folder = await FolderAsync(http, repoId, $"Mails-{run}", "folder");
        var rfc822 = $"From: Anna <anna@example.com>\r\nTo: tom@example.com\r\nSubject: Quarterly {run}\r\nMIME-Version: 1.0\r\n"
            + "Content-Type: multipart/mixed; boundary=b\r\n\r\n--b\r\nContent-Type: text/html; charset=utf-8\r\n\r\n"
            + "<p>Hello <b>there</b></p><script>alert(1)</script>\r\n--b\r\nContent-Type: text/plain\r\n"
            + $"Content-Disposition: attachment; filename=report-{run}.txt\r\n\r\nthe report\r\n--b--\r\n";
        await FileAsync(http, folder, $"mail-{run}", ".eml", rfc822);

        var page = await Ui.LoginAsync(app);
        var list = page.Locator("[data-pane='list']");
        var preview = page.Locator("[data-pane='preview']");
        await Expect(page.Locator("[data-pane='tree']")).ToBeVisibleAsync(new() { Timeout = 60000 });
        await page.Locator("[data-pane='tree']").GetByText("Demo Repository").First.ClickAsync();
        await list.Locator(".wb-list-row").Filter(new() { HasText = $"Mails-{run}" }).First.DblClickAsync();
        await list.Locator(".wb-list-row").Filter(new() { HasText = $"Quarterly {run}" }).First.ClickAsync();

        var mail = preview.Locator("[data-preview-mail]");
        await Expect(mail).ToContainTextAsync($"Quarterly {run}");
        await Expect(mail.Locator("[data-preview-mail-attachment]")).ToContainTextAsync($"report-{run}");
        var frame = mail.Locator("iframe[data-preview-mail-body]");
        await Expect(frame).ToHaveAttributeAsync("sandbox", "allow-popups allow-popups-to-escape-sandbox");
        var body = await frame.GetAttributeAsync("srcdoc");
        Assert.Contains("<b>there</b>", body);
        Assert.DoesNotContain("script", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Guid> FolderAsync(HttpClient http, Guid parent, string name, string folderMask) =>
        (await (await http.PostAsJsonAsync($"/api/documents/{parent}/children", new { name, folderMask })).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

    private static async Task FileAsync(HttpClient http, Guid folder, string name, string extension, string text)
    {
        var id = (await (await http.PostAsJsonAsync($"/api/documents/{folder}/children", new { name })).EnsureSuccessStatusCode()
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var version = await (await http.PostAsJsonAsync($"/api/documents/{id}/versions", new { fileExtension = extension }))
            .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(version.GetProperty("uploadUrl").GetString()!, new ByteArrayContent(Encoding.UTF8.GetBytes(text)))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{id}/versions/{version.GetProperty("id").GetGuid()}", new { })).EnsureSuccessStatusCode();
    }
}
