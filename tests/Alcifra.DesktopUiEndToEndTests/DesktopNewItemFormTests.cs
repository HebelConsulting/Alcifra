using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Alcifra.ApiClient;
using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.UiEndToEndTests;

// New ▸ a kind with REQUIRED fields (#2217, ADR 1067): the desktop asks for the mask's whole form, read from the address
// the admits entry advertises, and offers Create only once the name and every required field have a value. A kind
// without required fields keeps the name prompt, so the form is null for it. The dialog window itself is not driven
// here (no display); its model is, which is where every rule lives.
[Collection(UiCollection.Name)]
public class DesktopNewItemFormTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopNewItemFormTests(SelfHostedAppFixture app) => _app = app;

    private async Task<(MainWindowViewModel Vm, AlcifraApiClient Api, string Token)> OpenAsync()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var token = await Ui.GetUserTokenAsync(_app.BaseUrl);
        var api = new AlcifraApiClient(token);
        var vm = new MainWindowViewModel();
        await vm.InitializeSessionAsync(api, SelfHostedAppFixture.AdminEmail);
        return (vm, api, token);
    }

    [Fact]
    public async Task A_kind_without_required_fields_keeps_the_name_prompt()
    {
        var (vm, _, _) = await OpenAsync();
        var personal = vm.Tree.First(n => n.IsPersonal);
        await personal.ReloadChildrenAsync();
        var documents = personal.Children.Single(c => c.Name == "My Documents");
        var folder = documents.Admits.Single(a => a.Name == "Folder");

        Assert.False(string.IsNullOrEmpty(folder.MaskHref), "the entry carries no maskHref");
        Assert.Null(await vm.NewItemFormAsync(folder));
    }

    [Fact]
    public async Task A_kind_with_a_required_field_gets_the_form_and_create_waits_for_the_value()
    {
        var (vm, _, token) = await OpenAsync();

        // A mask of this run's own with one required field, as a module's would be; the entry is shaped as the server
        // shapes one (the end-to-end suite proves the server's entry carries the same address).
        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = (await http.PostAsJsonAsync("/api/masks", new
        {
            name = $"Case {Guid.NewGuid():N}"[..20],
            fields = new object[]
            {
                new { name = "Case number", dataType = 0, isRequired = true, isList = false },
                new { name = "Note", dataType = 0, isRequired = false, isList = false },
            },
        })).EnsureSuccessStatusCode();
        var maskId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        // The address the server answered the create with: followed, as a client follows an entry's maskHref.
        var maskHref = response.Headers.Location!.ToString();
        var entry = new CreatableChild(maskId, "Case", true, "/unused", null, "name", MaskHref: maskHref);

        var form = await vm.NewItemFormAsync(entry);
        Assert.NotNull(form);
        Assert.Equal("Case", form!.Title);
        // As a set: two fields made in one request share their sort order and creation time, so the definition's order
        // between them is the server's, not this test's.
        Assert.Equal(["Case number *", "Note"], form.Fields.Select(f => f.DisplayLabel).Order().ToList());
        var caseNumber = form.Fields.Single(f => f.Name == "Case number");

        Assert.False(form.CanCreate, "nothing is filled in yet");
        form.Name = "Case 1";
        Assert.False(form.CanCreate, "the required field is still empty");
        caseNumber.TextValue = "C-42";
        Assert.True(form.CanCreate);

        var values = Assert.Single(form.FieldValues());
        Assert.Equal(caseNumber.FieldDefinitionId, values.FieldDefinitionId);
        Assert.Equal(["C-42"], values.Values);
    }

    [Fact]
    public void A_required_field_refusal_names_the_field_in_the_readers_words()
    {
        Assert.Equal("Case number is required.", Alcifra.Localization.ApiErrorText.For("REQUIRED_FIELD_MISSING", "Case number"));
        Assert.Equal("A required field is empty.", Alcifra.Localization.ApiErrorText.For("REQUIRED_FIELD_MISSING", field: null));
    }
}
