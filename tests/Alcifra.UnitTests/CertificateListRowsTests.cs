using System.Text.Json;
using Alcifra.Cli.Commands;

namespace Alcifra.UnitTests;

// `alcifra certificates list` reads what the server answers. The core answers a bare array (ADR 0936); found live
// (Messenger demo, 2026-10-16) the command still expected the Encryption Module's wrapped shape and failed with
// "requires an element of type 'Object'". Either shape is read, so an older installation still lists.
public class CertificateListRowsTests
{
    private const string Row = """{"label":"Card","thumbprint":"AB","serial":"01","usable":true}""";

    [Theory]
    [InlineData("[" + Row + "]")]
    [InlineData("""{"certificates":[""" + Row + "]}")]
    [InlineData("""{"enrolled":[""" + Row + "]}")]
    public void The_core_array_and_the_modules_wrapped_answers_both_list(string json)
    {
        using var document = JsonDocument.Parse(json);
        var row = Assert.Single(CertificateListCommand.Rows(document.RootElement).ToList());
        Assert.Equal("Card", row.Label);
    }
}
