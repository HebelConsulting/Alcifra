using SimplArchive.Domain.Notifications;
using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// A chat notification opens its item's chat, in every client (owner decision 2026-10-13). The rule matches the API's
// type NAMES, so it is checked against the enum the API serialises: a renamed type must not quietly stop opening the chat.
public sealed class NotificationOpeningTests
{
    [Fact]
    public void A_mention_and_a_new_message_open_the_chat_and_nothing_else_does()
    {
        var opening = Enum.GetValues<NotificationType>().Where(t => NotificationOpening.OpensChat(t.ToString())).ToList();

        Assert.Equal([NotificationType.ChatMessagePosted, NotificationType.ChatMentioned], opening);
    }
}
