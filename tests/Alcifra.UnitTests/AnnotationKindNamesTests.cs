using System.Globalization;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.UnitTests;

// #318: a list of annotations names each one's kind through the one mapping, and every name it can return must
// exist in the resources. The kinds are core ADR 0525's stored values, append-only.
public sealed class AnnotationKindNamesTests
{
    [Theory]
    [InlineData(0, "Note")]
    [InlineData(1, "Highlight")]
    [InlineData(2, "Rectangle")]
    [InlineData(3, "Arrow")]
    [InlineData(4, "Stamp")]
    [InlineData(5, "Strikethrough")]
    [InlineData(6, "Text box")]
    [InlineData(7, "Freehand")]
    public void Each_stored_kind_is_named(int kind, string english)
    {
        Assert.Equal(english, Strings.Get(AnnotationKinds.NameKey(kind), CultureInfo.GetCultureInfo("en")));
    }

    [Fact]
    public void An_unknown_kind_reads_as_a_note()
    {
        // What the renderers draw it as, so an archive carrying a newer value still lists something true.
        Assert.Equal("AnnotationKindNote", AnnotationKinds.NameKey(99));
    }
}
