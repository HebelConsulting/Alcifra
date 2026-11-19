using System.Text;
using Alcifra.ApiClient;
using Alcifra.DesktopClient;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;
using Avalonia.Controls;

namespace Alcifra.UiEndToEndTests;

// The desktop half of the notification viewer (ADR "Notification viewer + click-through"): the real
// AlcifraApiClient reads a user's notifications (with the document's parent folder for click-through) and
// marks one read. Set up by the demo admin submitting a document to a fresh reviewer, who then reads their intray.
[Collection(UiCollection.Name)]
public class DesktopNotificationsTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopNotificationsTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Reviewer_reads_notifications_with_parent_and_marks_read()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var admin = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // A repository + a confirmed-version document.
        await admin.Documents.CreateRepositoryAsync($"dt-notif-{suffix}");
        var repo = (await admin.Documents.GetRepositoriesAsync()).First(r => r.Name == $"dt-notif-{suffix}");
        await admin.Documents.UploadFileAsync(repo.Href("children"), $"notif-{suffix}.txt", Encoding.UTF8.GetBytes("hi"));
        var doc = (await admin.Documents.GetChildrenAsync(repo.Href("children"))).First(c => c.HasVersions);

        // A fresh reviewer (tenant admin so they can read the content), with a password to log in.
        var email = $"dt-reviewer-{suffix}@example.test";
        var reviewerId = await admin.Admin.CreateUserAsync(email, $"DT Reviewer {suffix}");
        await admin.Admin.SetRightsAsync(reviewerId, new AdminClient.SystemRightsData(true, false, false, false, false, false, false, false, false, false, false, false, false));
        var password = await admin.Admin.ResetUserPasswordAsync(reviewerId);

        // Submit the document to the reviewer → a ReviewAssigned notification for them.
        var wf = await admin.Documents.GetWorkflowAsync(doc.Href("versions"));
        await admin.Workflow.PostWorkflowActionAsync(wf!.Links.Href("submit")!, new { reviewerId = reviewerId.Id });

        // The reviewer reads their intray: the notification carries the document + its parent folder.
        var reviewer = new AlcifraApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl, email, password));
        var list = await reviewer.Notifications.GetNotificationsAsync();
        Assert.True(list.UnreadCount >= 1);
        var n = list.Items.First(x => x.DocumentId == doc.Id);
        Assert.Equal(repo.Id, n.DocumentParentId);
        Assert.False(n.IsRead);

        // Marking it read drops the unread count.
        await reviewer.Notifications.MarkNotificationReadAsync(n);
        var after = await reviewer.Notifications.GetNotificationsAsync();
        Assert.True(after.Items.First(x => x.Id == n.Id).IsRead);
        Assert.Equal(list.UnreadCount - 1, after.UnreadCount);
    }

    // #1739 (owner, 2026-10-13): a chat notification opens its item with the conversation in front. On the
    // desktop the chat is a pane, so it expands — at its saved width, not the zero width a collapse leaves —
    // and a notification about anything else leaves the layout as the person set it.
    [Fact]
    public async Task A_chat_notification_expands_the_chat_pane_and_another_kind_does_not()
    {
        static NotificationRowViewModel Row(string type) => new(new NotificationsClient.NotificationInfo(
            Guid.NewGuid(), type, "Title", "Body", Guid.NewGuid(), null, DateTimeOffset.UtcNow, IsRead: false));

        var chat = new MainWindowViewModel { ChatCollapsed = true, ChatWidth = new GridLength(0) };
        await chat.OpenNotificationCommand.ExecuteAsync(Row("ChatMessagePosted"));
        Assert.False(chat.ChatCollapsed);
        Assert.NotEqual(0d, chat.ChatWidth.Value);

        var review = new MainWindowViewModel { ChatCollapsed = true, ChatWidth = new GridLength(0) };
        await review.OpenNotificationCommand.ExecuteAsync(Row("ReviewAssigned"));
        Assert.True(review.ChatCollapsed);
    }
}
