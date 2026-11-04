using Alcifra.Presentation;

namespace Alcifra.UnitTests;

// What Move to… may do with a selection (#2146): one answer for the ribbon and the list's menu, in both clients.
public class MoveSelectionTests
{
    private sealed record Row(bool IsReference = false, bool CanMove = true);

    private static MoveOffer Offer(params Row[] rows) => MoveSelection.Of(rows, r => r.IsReference, r => r.CanMove);

    private static bool Shows(params Row[] rows) => MoveSelection.ShowsInMenu(rows, r => r.IsReference, r => r.CanMove);

    [Fact]
    public void Nothing_selected_offers_nothing_and_says_nothing()
    {
        Assert.Equal(MoveOffer.Nothing, Offer());
        Assert.False(Shows());
        Assert.Null(MoveSelection.WhyNotKey(MoveOffer.Nothing));
    }

    [Fact]
    public void One_row_or_several_may_move_when_each_may()
    {
        Assert.Equal(MoveOffer.Allowed, Offer(new Row()));
        Assert.Equal(MoveOffer.Allowed, Offer(new Row(), new Row(), new Row()));
        Assert.True(Shows(new Row(), new Row()));
        Assert.Null(MoveSelection.WhyNotKey(MoveOffer.Allowed));
    }

    [Fact]
    public void One_row_the_caller_may_not_move_stops_them_all_and_says_why()
    {
        Assert.Equal(MoveOffer.NoRight, Offer(new Row(), new Row(CanMove: false)));
        Assert.False(Shows(new Row(), new Row(CanMove: false)));
        Assert.Equal("MoveWhyNoRight", MoveSelection.WhyNotKey(MoveOffer.NoRight));
    }

    [Fact]
    public void A_reference_in_the_selection_stops_it_and_says_why_even_where_a_right_is_missing_too()
    {
        Assert.Equal(MoveOffer.HasReference, Offer(new Row(), new Row(IsReference: true)));
        Assert.Equal(MoveOffer.HasReference, Offer(new Row(CanMove: false), new Row(IsReference: true)));
        Assert.Equal("MoveWhyReference", MoveSelection.WhyNotKey(MoveOffer.HasReference));
    }

    [Fact]
    public void A_lone_reference_has_no_Move_in_the_menu_and_a_mixed_selection_shows_it_saying_why()
    {
        Assert.False(Shows(new Row(IsReference: true)));
        Assert.True(Shows(new Row(), new Row(IsReference: true)));
    }

    [Theory]
    [InlineData("MoveWhyNoRight")]
    [InlineData("MoveWhyReference")]
    public void The_reasons_exist_in_the_catalogue(string key) =>
        Assert.NotEqual(key, Alcifra.Localization.Strings.Get(key));
}
