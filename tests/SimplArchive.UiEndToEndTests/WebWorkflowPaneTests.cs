using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace SimplArchive.UiEndToEndTests;

// The workflow steps the SERVER advertises, or nothing (#691) — offered since ADR 0970 by the ribbon's "Workflow
// step" menu and the row's ⋮ menu, while the details pane keeps only the status line.
//
// What it replaced was a permanent button labelled from the status string — Start / Manage / View — sitting in
// the row people use constantly for an action most documents never take. The slot now earns its place: empty
// when there is no workflow, and holding the actual next steps when there is one.
//
// ONE test walking the states in order, on a document it seeds ITSELF. That is not tidiness: the demo seed's
// invoice carries the only ready-made review, and WebWorkflowTests already approves and releases it, so a
// second test reading "In Review" from the same document passes or fails on which ran first. Written that way
// once, and it failed exactly so.
[Collection(UiCollection.Name)]
[Trait("Area", "ui-3")]
public class WebWorkflowPaneTests
{
    private readonly SelfHostedAppFixture _app;

    public WebWorkflowPaneTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task The_slot_is_empty_without_a_workflow_and_offers_the_state_transitions_with_one()
    {
        var name = $"wf-{Guid.NewGuid():N}"[..11];

        using var http = new HttpClient { BaseAddress = new Uri(_app.BaseUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repos = (await http.GetFromJsonAsync<JsonElement>("/api/repositories")).GetProperty("repositories");
        var repoId = repos.EnumerateArray().First(r => r.GetProperty("name").GetString() == "Demo Repository").GetProperty("id").GetGuid();

        var docId = (await (await http.PostAsJsonAsync($"/api/documents/{repoId}/children", new { name }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var versionId = await AddVersionAsync(http, docId);

        var page = await Ui.LoginAsync(_app);
        var list = page.Locator("[data-pane='list']");
        var pane = page.Locator("[data-pane='index']");

        await page.GetByText("Demo Repository").First.ClickAsync();
        var row = list.Locator(".wb-list-row").Filter(new() { HasText = name }).First;
        await row.ClickAsync();

        // ── No workflow: the state is stated in the pane, and NO step is offered anywhere. The absence is the
        //    load-bearing half — a client that offered every transition unconditionally would put Approve on
        //    every document in the archive and still satisfy a test that only looked at the In Review case. ──
        await Expect(pane.GetByText("not started")).ToBeVisibleAsync();
        await Expect(DocumentActionsUi.Ribbon(page, "follow")).ToBeEnabledAsync(); // the detail has landed
        await Expect(DocumentActionsUi.Ribbon(page, "workflow-steps")).ToHaveCountAsync(0);

        // ── Submitted to ourselves, so the signed-in admin is the assigned reviewer and the choice is real.
        //    Through the API on purpose: STARTING a workflow is the Workflow… dialog's (#691). ─────────────
        var me = (await http.GetFromJsonAsync<JsonElement>("/api/diagnostics/whoami")).GetProperty("userId").GetGuid();
        (await http.PostAsJsonAsync($"/api/documents/{docId}/versions/{versionId}/workflow/submit", new { reviewerId = me }))
            .EnsureSuccessStatusCode();

        await row.ClickAsync(); // reselect so the transitions reload
        await Expect(pane.GetByText("In Review")).ToBeVisibleAsync();

        // The pane states the state and offers no step (ADR 0970): those are actions on the document.
        foreach (var action in new[] { "Approve", "Reject" })
        {
            await Expect(pane.GetByRole(AriaRole.Button, new() { Name = action, Exact = true })).ToHaveCountAsync(0);
        }

        // ── Approving from the ribbon ACTS. No dialog offering the same button again (#691). ────────────
        var steps = await DocumentActionsUi.OpenRibbonMenuAsync(page, "workflow-steps");
        await steps.Filter(new() { HasText = "Approve" }).First.ClickAsync();

        await Expect(pane.GetByText("Approved")).ToBeVisibleAsync();
        await Expect(page.Locator(".mud-dialog")).ToHaveCountAsync(0);

        // ...and the ribbon now offers what the NEW state affords — re-read from the server after acting.
        steps = await DocumentActionsUi.OpenRibbonMenuAsync(page, "workflow-steps");
        await Expect(steps.Filter(new() { HasText = "Release" }).First).ToBeVisibleAsync();
        await Expect(steps.Filter(new() { HasText = "Approve" })).ToHaveCountAsync(0);
        await page.Keyboard.PressAsync("Escape");

        // The row's ⋮ menu offers the same list under "Workflow step".
        await DocumentActionsUi.OpenRowMenuAsync(page, name);
        await Expect(await DocumentActionsUi.OpenSubmenuAsync(page, "Workflow step", "Release")).ToBeVisibleAsync();
    }

    private static async Task<Guid> AddVersionAsync(HttpClient http, Guid docId)
    {
        var created = await (await http.PostAsJsonAsync($"/api/documents/{docId}/versions", new { fileExtension = ".txt" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var versionId = created.GetProperty("id").GetGuid();

        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes("workflow subject\n")))).EnsureSuccessStatusCode();
        }

        (await http.PutAsJsonAsync($"/api/documents/{docId}/versions/{versionId}", new { })).EnsureSuccessStatusCode();
        return versionId;
    }
}
