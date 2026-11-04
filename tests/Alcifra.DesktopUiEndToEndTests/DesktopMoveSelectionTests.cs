using Alcifra.DesktopClient.ViewModels;
using Alcifra.Presentation;

namespace Alcifra.UiEndToEndTests;

// Move to… in the desktop's ribbon and list menu (#2146): the offer follows the rows as selected. The bulk list drops
// references, so a test on it alone would pass a mixed selection that should say why not.
public class DesktopMoveSelectionTests
{
    private static NodeViewModel Row(bool canMove = true, bool isReference = false, bool isFolder = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = "row",
        HasChildren = false,
        HasVersions = !isFolder,
        IsReference = isReference,
        CanMove = canMove,
    };

    private static MainWindowViewModel Selecting(params NodeViewModel[] rows)
    {
        var vm = new MainWindowViewModel();
        vm.SetBulkSelection(rows);
        return vm;
    }

    [Fact]
    public void Nothing_selected_offers_nothing()
    {
        var vm = Selecting();
        Assert.Equal(MoveOffer.Nothing, vm.MoveOffer);
        Assert.False(vm.CanMoveSelected);
        Assert.False(vm.ShowMoveInMenu);
    }

    [Fact]
    public void One_row_and_several_rows_may_move_when_each_may()
    {
        Assert.True(Selecting(Row()).CanMoveSelected);
        Assert.True(Selecting(Row(), Row(isFolder: true)).CanMoveSelected);
        Assert.True(Selecting(Row(), Row()).ShowMoveInMenu);
    }

    [Fact]
    public void A_row_the_caller_may_not_move_disables_it_and_says_why()
    {
        var vm = Selecting(Row(), Row(canMove: false));
        Assert.Equal(MoveOffer.NoRight, vm.MoveOffer);
        Assert.False(vm.CanMoveSelected);
        Assert.Equal(Alcifra.Localization.Strings.Get("MoveWhyNoRight"), vm.MoveHint);
    }

    [Fact]
    public void A_reference_among_the_rows_disables_it_and_says_why_and_alone_it_has_no_Move()
    {
        var mixed = Selecting(Row(), Row(isReference: true));
        Assert.Equal(MoveOffer.HasReference, mixed.MoveOffer);
        Assert.True(mixed.ShowMoveInMenu);
        Assert.False(mixed.CanMoveSelected);
        Assert.Equal(Alcifra.Localization.Strings.Get("MoveWhyReference"), mixed.MoveHint);

        var alone = Selecting(Row(isReference: true));
        Assert.False(alone.ShowMoveInMenu);
        Assert.False(alone.CanMoveSelected);
    }

    [Fact]
    public void The_offer_tells_the_view_when_the_selection_changes()
    {
        var vm = Selecting();
        var told = new List<string?>();
        vm.PropertyChanged += (_, e) => told.Add(e.PropertyName);

        vm.SetBulkSelection([Row()]);

        Assert.Contains(nameof(vm.CanMoveSelected), told);
        Assert.Contains(nameof(vm.ShowMoveInMenu), told);
        Assert.Contains(nameof(vm.MoveHint), told);
    }
}
