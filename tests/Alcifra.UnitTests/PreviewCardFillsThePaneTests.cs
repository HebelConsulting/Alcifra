namespace Alcifra.UnitTests;

// A contact's or an appointment's card fills the preview pane (owner, 2026-11-08, #2274). Both clients had it in a
// 640-pixel column at the left, which leaves most of a wide pane empty. The web's width is measured in
// WebPreviewCardTests; the desktop's card has no rendered-geometry test, so this guards its markup's shape instead, in
// the manner of PreviewToolbarWrapsTests: structure here, geometry there.
public class PreviewCardFillsThePaneTests
{
    [Fact]
    public void The_desktop_card_stretches_and_has_no_width_cap()
    {
        var axaml = File.ReadAllText(Path.Combine(RepoPaths.Root(), "src", "Alcifra.DesktopClient", "Views", "PreviewPane.axaml"));
        var scroll = axaml.IndexOf("x:Name=\"CardScroll\"", StringComparison.Ordinal);
        Assert.True(scroll > 0, "the card's ScrollViewer (CardScroll) is gone from PreviewPane.axaml");

        var panelStart = axaml.IndexOf("<StackPanel", scroll, StringComparison.Ordinal);
        var panel = axaml[panelStart..axaml.IndexOf('>', panelStart)];

        Assert.DoesNotContain("MaxWidth", panel);
        Assert.Contains("HorizontalAlignment=\"Stretch\"", panel);
    }
}
