namespace Alcifra.Presentation;

/// <summary>
/// What opening a notification shows (owner decision 2026-10-13, from the phone's push test): a notification about a
/// CHAT opens its item with the chat in front, since the conversation is what it is about; every other one opens the
/// item as before. One rule for every client, so the phone, the desktop and the web cannot disagree.
/// </summary>
public static class NotificationOpening
{
    /// <summary>Whether a notification of <paramref name="type"/> (the API's type name) opens its item's chat.</summary>
    public static bool OpensChat(string type) => type is "ChatMentioned" or "ChatMessagePosted";
}
