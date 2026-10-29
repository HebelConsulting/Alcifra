using Alcifra.ApiClient;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.UnitTests;

// The rules the desktop's dialogs and the phone's forms share (mobile slice 5, second PR): what Delete asks, how a
// reminder repeats, and what an access role grants. Each key exists, so neither client shows a raw key.
public sealed class SharedFormRulesTests
{
    [Theory]
    [InlineData(true, false, "DelConfirmReference", "Remove")]
    [InlineData(true, true, "DelConfirmReference", "Remove")]
    [InlineData(false, true, "DelConfirmFolder", "UgDelete")]
    [InlineData(false, false, "DelConfirmDocument", "UgDelete")]
    public void Delete_asks_by_what_the_row_is(bool isReference, bool isFolder, string question, string button)
    {
        Assert.Equal(question, DeleteConfirmation.QuestionKey(isReference, isFolder));
        Assert.Equal(button, DeleteConfirmation.ButtonKey(isReference));
        Assert.Contains("Contract", string.Format(Strings.Get(question), "Contract"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_reminder_repeat_is_labelled_by_its_stored_value_and_every_label_exists()
    {
        Assert.Equal(4, ReminderRepeat.LabelKeys.Count);
        Assert.All(ReminderRepeat.LabelKeys, key => Assert.NotEqual(key, Strings.Get(key)));
    }

    [Fact]
    public void A_role_is_offered_only_when_the_caller_may_grant_every_right_in_it()
    {
        Assert.True(AclRoles.Covers(AclRoles.Viewer, AclRoles.Editor));
        Assert.False(AclRoles.Covers(AclRoles.Manager, AclRoles.Editor));   // Delete and ManagePermissions are not the editor's to hand out
        Assert.Equal("MaRoleEditor", AclRoles.LabelKeyOf(AclRoles.Editor));
        Assert.Equal("MaRoleCustom", AclRoles.LabelKeyOf(AclRoles.Viewer with { CanAnnotate = true }));
    }
}
