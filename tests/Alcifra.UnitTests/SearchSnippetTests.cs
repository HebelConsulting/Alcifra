using Alcifra.Presentation;

namespace Alcifra.UnitTests;

// A hit's fragment as both clients render it: the matched terms marked, everything HTML-decoded (the phone once
// printed the raw <em> tags and &#x27; entities, SimplArchiveMobile #24).
public class SearchSnippetTests
{
    [Fact]
    public void The_marked_terms_become_hits_and_everything_is_decoded()
    {
        var parts = SearchSnippet.Parts("<em>Invoice</em> for customer&#x27;s <em>chocolate</em> gift");

        Assert.Equal([("Invoice", true), (" for customer's ", false), ("chocolate", true), (" gift", false)], parts);
    }

    [Fact]
    public void Plain_text_is_one_run_and_no_fragment_is_none()
    {
        Assert.Equal([("Rechnungsdatum 03.03.2026", false)], SearchSnippet.Parts("Rechnungsdatum 03.03.2026"));
        Assert.Empty(SearchSnippet.Parts(null));
    }
}
