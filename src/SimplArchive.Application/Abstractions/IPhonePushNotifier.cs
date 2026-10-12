namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Pushes a just-written notification to the recipient's registered phones (SimplArchiveMobile #20, slice B). Called
/// from the DbContext's save, like <see cref="IRealtimeNotifier"/>, so every write path is covered. Best effort and
/// non-blocking (owner decision 2026-10-12): it hands the items over and returns; a lost push costs nothing that the
/// stored notification does not still show when the app opens.
/// </summary>
public interface IPhonePushNotifier
{
    void Notify(IReadOnlyList<PhonePushItem> items);
}

/// <summary>What one push is about: whose, which notification, and of which type (for the phone switch). No title: a push
/// carries the id alone (owner decision 2026-10-12).</summary>
public sealed record PhonePushItem(Guid TenantId, Guid UserId, Guid NotificationId, int Type);
