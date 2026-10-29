using Alcifra.ApiClient;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.UiEndToEndTests;

// Owner's report: after picking a mention (@Anna) the caret sat INSIDE the inserted token, because replacing the
// box's text keeps the old caret position. The view moves the caret to the end when the view model says a mention
// went in; this pins that the signal comes, and only once the text already holds the token.
[Collection(UiCollection.Name)]
public class DesktopMentionCaretTests
{
    [Fact]
    public void Picking_a_mention_says_so_after_the_token_is_in_the_text()
    {
        var vm = new MainWindowViewModel { NewComment = "Please check, @Ann" };
        var anna = new DocumentsClient.MentionableUser(Guid.NewGuid(), "Anna Muster");
        string? textWhenSignalled = null;
        vm.MentionInserted += () => textWhenSignalled = vm.NewComment;

        vm.PickMentionCommand.Execute(anna);

        Assert.Equal($"Please check, @[{anna.Id}] ", textWhenSignalled);
    }

    [Fact]
    public void No_mention_no_signal()
    {
        var vm = new MainWindowViewModel { NewComment = "no at sign here" };
        var signalled = false;
        vm.MentionInserted += () => signalled = true;

        vm.PickMentionCommand.Execute(new DocumentsClient.MentionableUser(Guid.NewGuid(), "Anna"));

        Assert.False(signalled);
    }
}
