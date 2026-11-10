using System.Net.Http.Headers;
using Alcifra.ApiClient;
using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.UiEndToEndTests;

// A field's value completes from a module's vocabulary (ABI 2.2, core #2288, ADR 1085): the TestModule's Test Certificate
// has an Issuer field completing from the module's issuer list. The editor offers each code with what it names, and names
// the code in the box beside it; a value the list does not hold is kept, unlabelled. Driven through the editor's model
// against a real server; the window itself is not opened here (no display).
[Collection(UiCollection.Name)]
public class DesktopFieldVocabularyTests(SelfHostedAppFixture app)
{
    // The TestModule's own id (tests/Alcifra.TestModule), which this project does not reference.
    private static readonly Guid CertificateMaskId = Guid.Parse("7E57AB1E-0000-0000-0000-000000000001");

    [Fact]
    public async Task An_issuer_completes_with_its_name_and_the_stored_code_is_named_beside_it()
    {
        DesktopClientOptions.ApiBaseUrl = app.BaseUrl;
        var (token, tenantId, repoId) = await TestModuleTenant.CreateAsync(app, "issuers");
        using var http = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await TestModuleTenant.ActivateAsync(app, http, tenantId, repoId);
        var api = new AlcifraApiClient(token);

        var definition = (await api.Masks.GetMaskFieldsAsync($"/api/masks/{CertificateMaskId}")).Single(f => f.Name == "Issuer");
        Assert.NotNull(definition.VocabularyHref);

        // A stored code is named as soon as the editor is wired.
        var editor = MaskFieldEditViewModel.Create(definition, ["AMC-GVA"]);
        FieldCompletionWiring.Wire(editor, api);
        Assert.True(editor.Completes);
        await editor.RefreshValueLabelAsync();
        Assert.Equal("Aeromedical Center Geneva", editor.ValueLabel);

        // Offered by name, the code being what the box receives.
        var offered = (await editor.SuggestionSource!("zurich", CancellationToken.None)).Cast<FieldSuggestion>().ToList();
        var zurich = Assert.Single(offered, s => s.HasLabel);
        Assert.Equal(("AMC-ZRH", "Aeromedical Center Zurich"), (zurich.Value, zurich.Label));
        Assert.Equal("AMC-ZRH", zurich.ToString());

        // Completion, never validation: free text is kept and simply has no name.
        editor.TextValue = "Dr. Meier";
        await editor.RefreshValueLabelAsync();
        Assert.Null(editor.ValueLabel);
        Assert.Equal(["Dr. Meier"], editor.ToValues());
    }
}
