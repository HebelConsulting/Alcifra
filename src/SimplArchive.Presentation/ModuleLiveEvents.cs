namespace SimplArchive.Presentation;

/// <summary>
/// A module's ephemeral event as it arrives over the live hub (ABI 1.25, ADR 0957): which module sent it (stamped by the
/// server, never by the module), the module's event name and its payload string. Never stored, so a client that was not
/// connected never sees it.
/// </summary>
public sealed record ModuleLiveEvent(string ModuleId, string Name, string Payload)
{
    /// <summary>The hub method the server invokes for a module event — one fact for the server and every client.</summary>
    public const string HubMethod = "module-event";
}

/// <summary>
/// Hands each received <see cref="ModuleLiveEvent"/> to the handlers subscribed to its module (ADR 0957), so a module's
/// client code sees its own events and no other module's. One router for the web, the desktop and the phone.
/// </summary>
/// <remarks>Thread-safe: the hub raises events on its own thread while a view subscribes on the UI thread. A handler that
/// throws does not stop the others; the exception is swallowed, because a live event is best-effort by definition.</remarks>
public sealed class ModuleLiveEventRouter
{
    private readonly object _gate = new();
    private readonly List<(string ModuleId, Action<ModuleLiveEvent> Handler)> _subscriptions = [];

    /// <summary>Calls <paramref name="handler"/> for every event <paramref name="moduleId"/> sends, until the returned
    /// subscription is disposed.</summary>
    public IDisposable Subscribe(string moduleId, Action<ModuleLiveEvent> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        ArgumentNullException.ThrowIfNull(handler);
        var entry = (moduleId, handler);
        lock (_gate)
        {
            _subscriptions.Add(entry);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _subscriptions.Remove(entry);
            }
        });
    }

    /// <summary>Delivers <paramref name="liveEvent"/> to its module's subscribers; returns how many were called.</summary>
    public int Dispatch(ModuleLiveEvent liveEvent)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        Action<ModuleLiveEvent>[] handlers;
        lock (_gate)
        {
            handlers = _subscriptions.Where(s => string.Equals(s.ModuleId, liveEvent.ModuleId, StringComparison.Ordinal))
                .Select(s => s.Handler).ToArray();
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(liveEvent);
            }
            catch (Exception)
            {
                // A subscriber's failure must not starve the others; best-effort by definition (ADR 0957).
            }
        }

        return handlers.Length;
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
