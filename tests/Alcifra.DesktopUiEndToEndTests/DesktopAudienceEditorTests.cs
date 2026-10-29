using Alcifra.ApiClient;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.DesktopUiEndToEndTests;

// The desktop's audience editor (ADR 0983): it says the one rule the server enforces too — somebody stays in charge —
// before Save, lets a manager add only the readers it was offered, and lets an appointing administrator add a manager
// and nothing else.
public class DesktopAudienceEditorTests
{
    private static readonly Guid Anna = Guid.NewGuid(), Bob = Guid.NewGuid(), Carol = Guid.NewGuid(), Team = Guid.NewGuid();

    private static ContentAudienceInfo Audience(bool manage = true, bool appoint = false, bool personal = false) => new(
        Guid.NewGuid(), IsStore: true, personal, "\"t\"", manage, appoint, IsMember: true,
        [new(Anna, null, "Anna", true, true), new(Bob, null, "Bob", false, true)],
        [new(Anna, "Anna", true, [])], [new(Carol, null, "Carol"), new(null, Team, "Team")], LinkMap.Empty);

    [Fact]
    public void Removing_the_last_manager_is_said_before_saving()
    {
        var editor = new AudienceEditor(Audience());
        Assert.True(editor.CanSave);

        editor.Remove(editor.Rows.Single(r => r.Name == "Anna"));
        Assert.False(editor.CanSave);
        Assert.NotNull(editor.Problem);

        editor.Rows.Single(r => r.Name == "Bob").IsManager = true;
        Assert.True(editor.CanSave);
        Assert.Equal([new AudienceChange(Bob, null, true)], editor.Result());
        Assert.Contains(editor.Candidates, c => c.UserId == Anna);   // removed: offered again
    }

    [Fact]
    public void A_manager_adds_an_offered_reader_or_group_as_a_member()
    {
        var editor = new AudienceEditor(Audience());
        editor.SelectedCandidate = editor.Candidates.Single(c => c.GroupId == Team);
        editor.AddSelected();

        var team = editor.Rows.Single(r => r.Name == "Team");
        Assert.False(team.IsManager);
        Assert.False(team.CanBeManager);   // a manager is always a person
        Assert.DoesNotContain(editor.Candidates, c => c.GroupId == Team);
    }

    [Fact]
    public void An_appointing_administrator_adds_a_manager_and_a_personal_store_edits_nothing()
    {
        var appointing = new AudienceEditor(Audience(manage: false, appoint: true));
        appointing.SelectedCandidate = appointing.Candidates.Single(c => c.UserId == Carol);
        appointing.AddSelected();
        Assert.True(appointing.Rows.Single(r => r.Name == "Carol").IsManager);

        var personal = new AudienceEditor(Audience(personal: true));
        Assert.False(personal.CanEdit);
        Assert.False(personal.CanSave);
    }
}
