using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// Inspect seal on the web (#2291): a Sealed document's row menu opens what the server reads from its stored envelope — the
// format, the recipient named as the administrator's own key, the envelope matching the audience — and the ASN.1 structure
// a click away. The browser decrypts nothing and reads no CMS itself.
// A WART, said so: enrolling switches the shared demo tenant's certificate settings on for the test (TestKeyEnrolment).
[Collection(UiCollection.Name)]
[Trait("Area", "ui-2")]
public class WebSealInspectionTests
{
    private readonly SelfHostedAppFixture _app;

    public WebSealInspectionTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_sealed_documents_row_menu_shows_its_seal()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = $"Sealed {suffix}";
        var label = $"Seal key {suffix}";

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));
        await using var enrolment = await Alcifra.TestSupport.TestKeyEnrolment.EnrolAsync(http, SelfHostedAppFixture.AdminEmail, label);
        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();
        var repository = await http.GetFromJsonAsync<JsonElement>($"/api/documents/{repoId}");
        var children = repository.GetProperty("links").EnumerateArray().Single(l => l.GetProperty("rel").GetString() == "children").GetProperty("href").GetString()!;
        var documents = new Alcifra.ApiClient.DocumentsClient(Alcifra.ApiClient.ApiCore.Over(http), () => throw new InvalidOperationException("no reminders here"));
        await documents.CreateSealedDocumentAsync(children, name, "minutes"u8.ToArray(), "text/plain", "minutes.txt", []);

        var page = await Ui.LoginAsync(_app);
        await page.GetByText("Demo Repository").First.ClickAsync();
        await Expect(page.Locator("[data-pane='list'] .wb-list-row").Filter(new() { HasText = name })).ToBeVisibleAsync();
        await (await DocumentActionsUi.OpenRowMenuAsync(page, name)).Filter(new() { HasText = "Inspect seal" }).First.ClickAsync();

        var dialog = page.Locator(".mud-dialog [data-dialog='seal-inspection']");
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.GetByText("CMS EnvelopedData")).ToBeVisibleAsync();
        await Expect(dialog.Locator("[data-seal-audience='matches']")).ToBeVisibleAsync();
        var recipient = dialog.Locator("[data-seal-recipient]");
        await Expect(recipient).ToHaveCountAsync(1);
        await Expect(recipient).ToContainTextAsync(label);

        // The third level: the structure, opened on demand.
        await dialog.Locator("[data-seal-asn1]").ClickAsync();
        await Expect(dialog.Locator("pre")).ToContainTextAsync("SEQUENCE");
    }
}
