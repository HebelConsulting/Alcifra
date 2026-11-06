using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Alcifra.UiEndToEndTests;

// New ▸ a kind with REQUIRED fields on the web (#2217, ADR 1067): the dialog shows the mask's form with the required field
// marked, offers Create only once it is filled, and the create then carries the value — against the test module's
// register, whose only admitted kind (a case) requires a case number. The desktop's form is the reference (ADR 0511).
[Collection(ModuleUiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebNewItemDialogTests(ModuleAppFixture app)
{
    private static readonly Guid RegisterMaskId = Guid.Parse("7E57AB1E-0000-0000-0000-000000000007");

    [Fact]
    public async Task New_case_asks_for_the_case_number_and_creates_with_it()
    {
        var registerName = $"Register {Guid.NewGuid():N}"[..20];
        var (http, _, repoId) = await app.ActivateTestModuleAsync();
        string repoName;
        using (http)
        {
            (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name = registerName, maskId = RegisterMaskId })).EnsureSuccessStatusCode();
            repoName = (await http.GetFromJsonAsync<JsonElement>($"/api/documents/{repoId}")).GetProperty("name").GetString()!;
        }

        var page = await Ui.LoginAsync(app);
        var tree = page.Locator("[data-pane='tree']");

        // Opened from the LIST rather than reached in the tree: that is what expands the branch and reveals the node
        // (#692), and it leaves the listing standing inside the register, where the case has to turn up at the end.
        await Expect(tree).ToBeVisibleAsync(new() { Timeout = 60000 });
        await tree.GetByText(repoName).First.ClickAsync();
        await page.Locator("[data-pane='list'] .wb-list-row").Filter(new() { HasText = registerName }).First.DblClickAsync();

        var node = tree.Locator(".mud-treeview-item-content").Filter(new() { HasText = registerName }).First;
        await Expect(node).ToBeVisibleAsync();
        await node.ClickAsync(new() { Button = MouseButton.Right });
        await (await Ui.OpenNewSubmenuAsync(page, "Test Case")).ClickAsync();

        var dialog = page.Locator(".mud-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        await Expect(dialog.Locator(".wb-mask-label").Filter(new() { HasText = "Case number *" })).ToBeVisibleAsync();
        await Expect(dialog.Locator(".wb-mask-label").Filter(new() { HasText = "Note" })).ToBeVisibleAsync();

        var create = dialog.Locator("[data-new-item-create]");
        var caseName = $"Case {Guid.NewGuid():N}"[..12];
        await dialog.Locator(".wb-mask-row").First.Locator("input").FillAsync(caseName);
        await Expect(create).ToBeDisabledAsync();

        var caseNumber = dialog.Locator(".wb-mask-row").Filter(new() { HasText = "Case number *" }).Locator("input");
        await caseNumber.FillAsync("C-42");
        await caseNumber.PressAsync("Tab");
        await Expect(create).ToBeEnabledAsync();
        await create.ClickAsync();

        await Expect(page.Locator(".mud-snackbar")).ToContainTextAsync(caseName);
        await Expect(page.Locator("[data-pane='list']").GetByText(caseName)).ToBeVisibleAsync();
    }
}
