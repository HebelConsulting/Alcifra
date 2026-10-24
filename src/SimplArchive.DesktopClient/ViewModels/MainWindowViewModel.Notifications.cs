using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// The in-app notifications bell (ADR "Notification viewer + click-through"): the list, the unread badge,
// marking everything read, opening one -- and the REALTIME connection that keeps the badge current, whose
// start and stop belong here because nothing else observes it.
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<NotificationRowViewModel> Notifications { get; } = [];
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasUnreadNotifications))] private int _unreadNotificationCount;
    public bool HasUnreadNotifications => UnreadNotificationCount > 0;

    public async Task LoadNotificationsAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var list = await _api.Notifications.GetNotificationsAsync();
            Notifications.Clear();
            foreach (var n in list.Items)
            {
                Notifications.Add(new NotificationRowViewModel(n));
            }

            UnreadNotificationCount = list.UnreadCount;
            _notificationsReadAllHref = list.ReadAllHref;
        }
        catch (Exception)
        {
            // best-effort — the bell just shows nothing
        }
    }

    // Live bell updates (ADR "Real-time notifications (SignalR)"): connect to the hub and reload the bell +
    // surface a status line whenever the server pushes a notification. Best-effort — the bell still loads on
    // login if the hub can't connect.
    private RealtimeNotificationClient? _realtime;

    private async Task StartRealtimeNotificationsAsync()
    {
        if (_api is null || _realtime is not null)
        {
            return;
        }

        try
        {
            var api = _api;   // this session's: a reconnect presents its renewed token, a new session gets a new connection
            _realtime = new RealtimeNotificationClient(DesktopClientOptions.ApiBaseUrl, () => api.AccessToken);
            _realtime.NotificationReceived += n => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                Status = string.IsNullOrWhiteSpace(n.Title) ? n.Body : n.Title;
                await LoadNotificationsAsync();
                MessagesArrived();   // a "new messages" signal reloads the Messages tab's open screens (ADR 0967)
            });
            // Modules' ephemeral events ride the same connection (ADR 0957): the Messages tab's "is typing".
            _realtime.ModuleEventReceived += e => ModuleLiveEvents.Dispatch(e);
            await _realtime.StartAsync();
        }
        catch (Exception)
        {
            // real-time is best-effort; the bell still works via load-on-login.
        }
    }

    private async Task StopRealtimeNotificationsAsync()
    {
        if (_realtime is not null)
        {
            await _realtime.DisposeAsync();
            _realtime = null;
        }
    }

    [RelayCommand]
    private async Task MarkAllNotificationsRead()
    {
        if (_api is null)
        {
            return;
        }

        if (_notificationsReadAllHref is not { } readAllHref)
        {
            return;
        }

        try { await _api.Notifications.MarkAllNotificationsReadAsync(readAllHref); } catch (Exception) { }
        foreach (var n in Notifications) n.IsRead = true;
        UnreadNotificationCount = 0;
    }

    // Clicking a notification marks it read and, if it relates to a document, navigates to it.
    [RelayCommand]
    private async Task OpenNotification(NotificationRowViewModel? n)
    {
        if (n is null)
        {
            return;
        }

        if (_api is not null && !n.IsRead)
        {
            try { await _api.Notifications.MarkNotificationReadAsync(n.Notification); } catch (Exception) { }
            n.IsRead = true;
            if (UnreadNotificationCount > 0) UnreadNotificationCount--;
        }

        // A "new messages" signal opens its conversation in the Messages tab, as a tapped one does on the phone; one that
        // is none of the user's conversations (another module's, or left since) falls through to the document path.
        if (await OpenSignalledConversationAsync(n))
        {
            return;
        }

        // Follow the row's `parent` (its home folder) and select the document there; a root document has no
        // parent, so its own `document` address opens as the folder (#443). Ids only for the row-matching.
        if (n.DocumentId is { } documentId && (n.Notification.Links?.Href("parent") ?? n.Notification.Links?.Href("document")) is { } href)
        {
            SelectedTab = 0; // Repositories
            await OpenFolderAsync(href, n.Notification.Links?.Has("parent") == true ? documentId : null);
        }
    }
}
