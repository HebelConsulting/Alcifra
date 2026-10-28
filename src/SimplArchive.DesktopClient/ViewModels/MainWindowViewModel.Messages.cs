namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The Messages tab's wiring (core #1813), in its own partial: MainWindowViewModel.cs is over the 1000-line limit
/// and must not grow (OverLimitFileCeilingTests), so a tab added after it registers here — built on first use, told
/// of sign-in and sign-out by the generated <see cref="OnIsLoggedInChanged"/>, and loaded through
/// <see cref="ActivateLaterTabAsync"/>, which the tab switch's default arm calls.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The Messages tab's index: appended after Calendar, so no other tab is re-indexed.</summary>
    public const int MessagesTabIndex = 15;

    private MessagesTabViewModel? _messagesTab;

    public MessagesTabViewModel MessagesTab => _messagesTab ??=
        new MessagesTabViewModel(this, () => _api, new Services.MessengerLiveEvents(ModuleLiveEvents), () => UserEmail);

    /// <summary>
    /// Modules' ephemeral events off the window's live connection (core ABI 1.25, ADR 0957), subscribed here rather than
    /// on the connection, so a subscription outlives a reconnect: the Messages tab hears "is typing" on it
    /// (SimplArchiveMessenger #26).
    /// </summary>
    public SimplArchive.Presentation.ModuleLiveEventRouter ModuleLiveEvents { get; } = new();

    // Signed in: ask whether the Messenger is offered here (the tab shows only if it is). Signed out: forget its keys.
    partial void OnIsLoggedInChanged(bool value)
    {
        if (value)
        {
            Services.Safe.Fire(MessagesTab.DiscoverAsync);
            Services.Safe.Fire(PasswordsTab.DiscoverAsync);   // the Passwords tab likewise (ADR 0968)
            Services.Safe.Fire(ApplyProductNameAsync);   // the installation's product name (core #1117)
        }
        else
        {
            ForgetProductName();
            MessagesTab.Reset();
            PasswordsTab.Reset();
        }
    }

    /// <summary>The wire name of a module's "something new" notification (core ABI 1.22): the Messenger's names its conversation.</summary>
    public const string ModuleSignalType = "ModuleSignal";

    /// <summary>
    /// The conversations the unread signals in <paramref name="items"/> are about, once each — for the Messenger's
    /// "new messages", the conversation is the signal's document (core #1811), as the phone reads it.
    /// </summary>
    public static IReadOnlyList<Guid> SignalledConversations(IEnumerable<NotificationRowViewModel> items) =>
        [.. items.Where(n => !n.IsRead && n.Notification.Type == ModuleSignalType && n.DocumentId is not null)
            .Select(n => n.DocumentId!.Value).Distinct()];

    // After the bell was read again for a live notification: each conversation with an unread signal reads again in the
    // Messages tab, if it is on show (its list always). The hub's payload says no conversation, the bell does.
    private void MessagesArrived()
    {
        foreach (var conversation in SignalledConversations(Notifications))
        {
            MessagesTab.Arrived(conversation);
        }
    }

    // A clicked "new messages" signal: its conversation, selected in the Messages tab brought to the front.
    private async Task<bool> OpenSignalledConversationAsync(NotificationRowViewModel n)
    {
        if (n.Notification.Type != ModuleSignalType || n.DocumentId is not { } conversation || !await MessagesTab.OpenConversationAsync(conversation))
        {
            return false;
        }

        SelectedTab = MessagesTabIndex;
        return true;
    }

    // Tabs appended after the switch was closed to growth load here, by their index.
    private Task ActivateLaterTabAsync(int tab) => tab switch
    {
        MessagesTabIndex => MessagesTab.LoadAsync(),
        PasswordsTabIndex => PasswordsTab.LoadAsync(),
        _ => Task.CompletedTask,
    };
}
