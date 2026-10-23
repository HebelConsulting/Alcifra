using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using SimplArchive.Presentation;

namespace SimplArchive.ApiClient;

// The desktop SignalR client for real-time notifications (ADR "Real-time notifications (SignalR)"). Connects to
// the Api's /hubs/notifications with the caller's access token (passed as ?access_token=, since the token is a
// bearer credential) and raises NotificationReceived when the server pushes one, so the bell updates live.
// Best-effort: a failed connect never breaks the app (the bell still loads on login).
//
// The same connection carries modules' ephemeral events (ABI 1.25, ADR 0957): the Messenger's "is typing". A module's
// client code subscribes with OnModuleEvent(moduleId, …) and sees only its own module's events; nothing is stored, so an
// event that arrives while nobody listens is gone.
public sealed class RealtimeNotificationClient : IAsyncDisposable
{
    private readonly string _baseUrl;
    private readonly Func<string> _accessToken;
    private readonly ModuleLiveEventRouter _moduleEvents = new();
    private HubConnection? _hub;

    public RealtimeNotificationClient(string baseUrl, string accessToken)
        : this(baseUrl, () => accessToken)
    {
    }

    /// <param name="accessToken">Read at every (re)connect rather than once: an automatic reconnect after the token's
    /// lifetime then presents the session's renewed token, not the one the connection was opened with.</param>
    public RealtimeNotificationClient(string baseUrl, Func<string> accessToken)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _accessToken = accessToken;
    }

    // Raised (off the connection thread) with the pushed notification's title + body.
    public event Action<RealtimeNotification>? NotificationReceived;

    // Raised (off the connection thread) for every module event, whichever module sent it.
    public event Action<ModuleLiveEvent>? ModuleEventReceived;

    // Calls handler (off the connection thread) for each event moduleId sends, until the result is disposed. Subscribing
    // before StartAsync is fine; nothing is replayed.
    public IDisposable OnModuleEvent(string moduleId, Action<ModuleLiveEvent> handler) =>
        _moduleEvents.Subscribe(moduleId, handler);

    public async Task StartAsync()
    {
        _hub = new HubConnectionBuilder()
            .WithUrl($"{_baseUrl}/hubs/notifications", options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_accessToken());
            })
            .WithAutomaticReconnect()
            .Build();

        _hub.On<RealtimeNotification>("notification", n => NotificationReceived?.Invoke(n));
        _hub.On<ModuleLiveEvent>(ModuleLiveEvent.HubMethod, ReceiveModuleEvent);
        await _hub.StartAsync();
    }

    private void ReceiveModuleEvent(ModuleLiveEvent liveEvent)
    {
        ModuleEventReceived?.Invoke(liveEvent);
        _moduleEvents.Dispatch(liveEvent);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
            _hub = null;
        }
    }

    public sealed record RealtimeNotification(string Title, string Body);
}
