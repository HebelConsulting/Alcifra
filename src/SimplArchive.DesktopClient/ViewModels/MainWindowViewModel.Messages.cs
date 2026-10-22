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
        new MessagesTabViewModel(this, () => _api, new Services.MessengerLiveEvents(ModuleLiveEvents));

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
        }
        else
        {
            MessagesTab.Reset();
        }
    }

    // Tabs appended after the switch was closed to growth load here, by their index.
    private Task ActivateLaterTabAsync(int tab) => tab switch
    {
        MessagesTabIndex => MessagesTab.LoadAsync(),
        _ => Task.CompletedTask,
    };
}
