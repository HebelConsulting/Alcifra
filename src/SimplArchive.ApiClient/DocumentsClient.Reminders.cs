using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.ApiClient;

/// <summary>
/// The documents client's reminders side: the reminder collection's read, the target picker it advertises, and the
/// two ways to set one (the zone-aware overload is the one a client that knows its zone should use, ADR 0972).
/// </summary>
/// <remarks>
/// Split out of <c>DocumentsClient.cs</c> when the second <c>CreateReminderAsync</c> overload took that file over the
/// 1000-line ceiling (ADR 0572/0732): the reminders are one responsibility, and the file that names them is where a
/// caller looks.
/// </remarks>
public sealed partial class DocumentsClient
{
    public async Task<bool> GetSubscriptionAsync(string subscriptionHref, CancellationToken cancellationToken = default) =>
        await _reminders().GetSubscriptionAsync(subscriptionHref, cancellationToken);

    public async Task SetSubscriptionAsync(string subscriptionHref, bool subscribe, CancellationToken cancellationToken = default) =>
        await _reminders().SetSubscriptionAsync(subscriptionHref, subscribe, cancellationToken);

    public async Task<IReadOnlyList<RemindersClient.ReminderInfo>> GetRemindersAsync(string remindersHref, CancellationToken cancellationToken = default) =>
        (await GetRemindersViewAsync(remindersHref, cancellationToken)).Reminders;

    /// <summary>
    /// The document's reminders AND the address of its target picker, from ONE read of the collection that
    /// advertises both. The Remind… dialog wants the two together; asking for them separately would mean
    /// fetching the document twice and the collection twice, which is how following rels turns into four
    /// requests where there used to be two (ADR 0543, issue #416).
    /// </summary>
    public async Task<(IReadOnlyList<RemindersClient.ReminderInfo> Reminders, string TargetsHref)> GetRemindersViewAsync(string remindersHref, CancellationToken cancellationToken = default)
    {
        var collection = await _core.Http.GetFromJsonAsync<JsonElement>(remindersHref, cancellationToken);
        return (RemindersClient.ParseReminders(collection), ApiCore.RequireRel(collection, "targets", "The reminders collection"));
    }

    public async Task CreateReminderAsync(string remindersHref, DateTimeOffset remindAt, string? note, int recurrence, Guid? targetUserId, CancellationToken cancellationToken = default) =>
        await _reminders().CreateReminderAsync(remindersHref, remindAt, note, recurrence, targetUserId, cancellationToken);

    /// <inheritdoc cref="RemindersClient.CreateReminderAsync(string, DateTimeOffset, string?, int, Guid?, string?, CancellationToken)"/>
    public async Task CreateReminderAsync(string remindersHref, DateTimeOffset remindAt, string? note, int recurrence, Guid? targetUserId, string? timeZone, CancellationToken cancellationToken = default) =>
        await _reminders().CreateReminderAsync(remindersHref, remindAt, note, recurrence, targetUserId, timeZone, cancellationToken);
}
