using Alcifra.Presentation;

namespace Alcifra.UnitTests;

/// <summary>
/// The two annotation palettes (owner decision 2026-11-25, #2469): pastel for the two kinds that cover text, strong for
/// the drawing kinds — both reachable for every kind, only the default follows the kind.
/// </summary>
public sealed class AnnotationPaletteTests
{
    [Fact]
    public void Highlight_and_note_default_to_the_soft_set_without_black_or_navy()
    {
        Assert.Same(AnnotationPalette.Pastel, AnnotationPalette.ForKind(1));   // Highlight
        Assert.Same(AnnotationPalette.Pastel, AnnotationPalette.ForKind(0));   // Note (and the unknown-kind fallback)
        Assert.DoesNotContain("#000000", AnnotationPalette.Pastel);
        Assert.DoesNotContain("#1A237E", AnnotationPalette.Pastel);
    }

    [Fact]
    public void Every_drawing_kind_defaults_to_the_ink_set_begun_by_black_and_navy()
    {
        foreach (var kind in new[] { 2, 3, 4, 5, 6, 7, 8 })   // rectangle, arrow, stamp, strikethrough, text box, freehand, signature
        {
            Assert.Same(AnnotationPalette.Strong, AnnotationPalette.ForKind(kind));
        }

        Assert.Equal("#000000", AnnotationPalette.Strong[0]);
        Assert.Equal("#1A237E", AnnotationPalette.Strong[1]);
    }

    [Fact]
    public void A_new_tool_starts_in_its_default_sets_first_colour()
    {
        Assert.Equal(AnnotationPalette.Pastel[0], AnnotationPalette.DefaultFor(1));
        Assert.Equal("#000000", AnnotationPalette.DefaultFor(8));
    }

    [Fact]
    public void The_two_sets_share_no_colour()
    {
        Assert.Empty(AnnotationPalette.Pastel.Intersect(AnnotationPalette.Strong));
    }
}
