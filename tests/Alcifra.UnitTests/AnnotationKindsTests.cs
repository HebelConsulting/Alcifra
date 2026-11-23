using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.UnitTests;

// ADR 1106: the stored kinds are append-only ints, and every one of them has a name a list can show — a kind whose key
// is missing renders as a raw key, which is what the duplicate-key trap once did to a caption.
public sealed class AnnotationKindsTests
{
    [Fact]
    public void Every_stored_kind_from_note_to_signature_has_a_defined_name_key()
    {
        var defined = Strings.AllKeys().ToHashSet(StringComparer.Ordinal);

        for (var kind = 0; kind <= 8; kind++)
        {
            Assert.Contains(AnnotationKinds.NameKey(kind), defined);
        }

        Assert.Equal("AnnotationKindSignature", AnnotationKinds.NameKey(8));
        Assert.Equal("AnnotationKindFreehand", AnnotationKinds.NameKey(7));
    }

    [Fact]
    public void The_signatures_row_label_exists_in_every_language()
    {
        foreach (var culture in new[] { "en", "de", "it", "es" })
        {
            var text = Strings.Get("AnnotationSignatureBy", System.Globalization.CultureInfo.GetCultureInfo(culture));
            Assert.Contains("{0}", text);
            Assert.NotEqual("AnnotationSignatureBy", text);
        }
    }
}
