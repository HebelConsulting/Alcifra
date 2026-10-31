using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;

namespace Alcifra.UiEndToEndTests;

// The richer demo seed (issue #354; Kristall Holding AG's archive since #1788): the Demo Repository has the
// Business Years / Contracts / General tree with varied document types, a two-version document, and a
// cross-folder reference. Verified over the real Api against
// the demo-seeded self-hosted app (the same seed the kiosk ships).
[Collection(UiCollection.Name)]
public class DesktopDemoTreeTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopDemoTreeTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Demo_repository_has_the_business_tree_with_versions_and_a_reference()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var repo = (await api.Documents.GetRepositoriesAsync()).Single(r => r.Name == "Demo Repository");
        var topLevel = (await api.Documents.GetChildrenAsync(repo.Href("children"))).Select(n => n.Name).ToList();
        Assert.Contains("Business Years", topLevel);
        Assert.Contains("Contracts", topLevel);
        Assert.Contains("General", topLevel);
        Assert.Contains("Customers", topLevel);

        async Task<Node> ChildAsync(Node parent, string name) =>
            (await api.Documents.GetChildrenAsync(parent.Href("children"))).Single(n => n.Name == name);

        // Business Years / 2026 / 03 March holds the catering invoice with TWO versions (Compare-versions).
        var businessYears = await ChildAsync(repo, "Business Years");
        var year = await ChildAsync(businessYears, "2026");
        var march = await ChildAsync(year, "03 March");
        var catering = await ChildAsync(march, "Catering invoice — staff event");
        Assert.Equal(2, catering.VersionCount);

        // The March Seewelle invoice lives under Contracts/Seewelle Telecom/Invoices and is referenced into a month
        // folder — so it reports HasReferences.
        var contracts = await ChildAsync(repo, "Contracts");
        var seewelle = await ChildAsync(contracts, "Seewelle Telecom");
        var seewelleInvoices = await ChildAsync(seewelle, "Invoices");
        var marchInvoice = await ChildAsync(seewelleInvoices, "Seewelle invoice — March 2026");
        Assert.True(marchInvoice.HasReferences);
    }
}
