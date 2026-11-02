using Avalonia.Controls;
using Alcifra.DesktopClient.Views;

namespace Alcifra.UiEndToEndTests;

// #2073: a context menu shows a separator only between two visible groups: never first, last or two in a row. The
// owner saw two with nothing between them, between Legal hold and Compare, where Rename and Delete were both hidden.
public class MenuSeparatorsTests
{
    private static MenuItem Entry(string name, bool visible = true) => new() { Header = name, IsVisible = visible };

    private static string Shown(IEnumerable<Control> items) =>
        string.Join(' ', items.Where(i => i.IsVisible).Select(i => i is Separator ? "|" : (string)((MenuItem)i).Header!));

    [Fact]
    public void A_group_with_nothing_visible_leaves_one_separator_not_two()
    {
        Control[] menu = [Entry("Open"), Entry("Compare"), new Separator(), Entry("Rename", false), Entry("Delete", false),
            new Separator(), Entry("Legal hold"), new Separator(), Entry("Workflow", false)];

        MenuSeparators.Apply(menu);

        Assert.Equal("Open Compare | Legal hold", Shown(menu));   // no doubled, no trailing separator
    }

    [Fact]
    public void A_separator_is_never_first_or_last_and_comes_back_when_its_group_does()
    {
        var rename = Entry("Rename", false);
        Control[] menu = [new Separator(), Entry("Open"), new Separator(), rename, new Separator()];

        MenuSeparators.Apply(menu);
        Assert.Equal("Open", Shown(menu));

        rename.IsVisible = true;   // the next row allows renaming: the same menu opens again
        MenuSeparators.Apply(menu);
        Assert.Equal("Open | Rename", Shown(menu));
    }
}
