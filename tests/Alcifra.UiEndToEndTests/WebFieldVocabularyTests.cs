using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// A field's value completes from a module's vocabulary in the web client too (ABI 2.2, core #2288, ADR 1085), as on the
// desktop: the TestModule's Test Certificate has an Issuer field completing from the module's issuer list. In the detail
// pane's edit mode the stored code is named beneath the box, each offered code carries its name, and the picked code is
// what Save stores.
[Collection(ModuleUiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebFieldVocabularyTests(ModuleAppFixture app)
{
    private static readonly Guid CertificateMaskId = Guid.Parse("7E57AB1E-0000-0000-0000-000000000001");

    [Fact]
    public async Task An_issuer_is_named_beneath_its_code_and_completes_by_name()
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var (http, _, repoId) = await app.ActivateTestModuleAsync();
        Guid certificate, issuerField;
        string repoName;
        using (http)
        {
            repoName = (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{repoId}")).GetProperty("name").GetString()!;
            certificate = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = $"Medical-{run}" }))
                .EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            (await http.PutAsJsonAsync($"/api/documents/{certificate}/mask", new { maskId = CertificateMaskId })).EnsureSuccessStatusCode();
            issuerField = (await http.GetFromJsonAsync<JsonElement>($"/api/masks/{CertificateMaskId}")).GetProperty("fields").EnumerateArray()
                .Single(f => f.GetProperty("name").GetString() == "Issuer").GetProperty("id").GetGuid();
            (await http.PutAsJsonAsync($"/api/documents/{certificate}/index-data",
                new { fields = new[] { new { fieldDefinitionId = issuerField, values = new[] { "AMC-GVA" } } } })).EnsureSuccessStatusCode();
        }

        var page = await Ui.LoginAsync(app);
        var list = page.Locator("[data-pane='list']");
        var index = page.Locator("[data-pane='index']");
        await Expect(page.Locator("[data-pane='tree']")).ToBeVisibleAsync(new() { Timeout = 60000 });
        await page.Locator("[data-pane='tree']").GetByText(repoName).First.ClickAsync();
        await list.GetByText($"Medical-{run}").First.ClickAsync();

        // The stored code, named.
        await index.GetByRole(AriaRole.Button, new() { Name = "Edit" }).ClickAsync();
        var issuer = index.Locator("[data-field-vocabulary]");
        await Expect(issuer).ToBeVisibleAsync();
        await Expect(index.GetByText("Aeromedical Center Geneva")).ToBeVisibleAsync();

        // Offered by name, the code being what is picked and stored.
        await issuer.Locator("input").FillAsync("zurich");
        var zurich = page.Locator(".mud-popover-open .mud-list-item").Filter(new() { HasText = "AMC-ZRH" });
        await Expect(zurich).ToContainTextAsync("Aeromedical Center Zurich");
        await zurich.First.ClickAsync();
        await Expect(issuer.Locator("input")).ToHaveValueAsync("AMC-ZRH");
        await Expect(index.GetByText("Aeromedical Center Zurich")).ToBeVisibleAsync();
        await index.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        Assert.Equal(["AMC-ZRH"], await IssuerValueAsync(certificate, "AMC-ZRH"));
    }

    private async Task<List<string>> IssuerValueAsync(Guid documentId, string expected)
    {
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(app.BaseUrl));
        List<string> values = [];
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var fields = (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{documentId}/index-data")).GetProperty("fields");
            values = fields.EnumerateArray().FirstOrDefault(f => f.GetProperty("fieldName").GetString() == "Issuer") is { ValueKind: JsonValueKind.Object } field
                ? [.. field.GetProperty("values").EnumerateArray().Select(v => v.GetString()!)]
                : [];
            if (values.SequenceEqual([expected]))
            {
                return values;
            }

            await Task.Delay(250);
        }

        return values;
    }
}
