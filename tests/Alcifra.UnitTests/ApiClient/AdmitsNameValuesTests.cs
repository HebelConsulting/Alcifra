using System.Text.Json;
using Alcifra.ApiClient;

namespace Alcifra.UnitTests.ApiClient;

// #1710: a creatable child that names its name vocabulary (a module's, for New ▸) carries it into the client, so the
// desktop's and the phone's new-item dialog completes the name; a child without one stays a plain name box.
public sealed class AdmitsNameValuesTests
{
    private static Node Parse(string admits) => DocumentsClient.ParseNode(JsonDocument.Parse($$"""
        {"id":"{{Guid.NewGuid()}}","name":"Aerodromes","hasChildren":true,"hasVersions":false,"admits":{{admits}}}
        """).RootElement);

    [Fact]
    public void A_row_carrying_a_name_vocabulary_yields_it_and_whether_it_takes_several()
    {
        var node = Parse("""
            [{"maskId":"11111111-1111-1111-1111-111111111111","name":"Aerodrome","folder":true,"href":"/api/x/children","prompt":"name",
              "nameValuesHref":"/api/modules/flight-school/aerodromes","nameValuesAreMultiple":true},
             {"maskId":"22222222-2222-2222-2222-222222222222","name":"Note","folder":false,"href":"/api/x/children","prompt":"name"}]
            """);

        var admits = node.Admits!;
        var vocabulary = admits[0];
        Assert.Equal("/api/modules/flight-school/aerodromes", vocabulary.NameValuesHref);
        Assert.True(vocabulary.NameValuesAreMultiple);
        Assert.Null(admits[1].NameValuesHref);
        Assert.False(admits[1].NameValuesAreMultiple);
    }
}
