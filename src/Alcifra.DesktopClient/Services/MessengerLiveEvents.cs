using Alcifra.Messenger.Client.Display;
using Alcifra.Presentation;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// The Messages tab's view of the window's one live connection (SimplArchiveMessenger #26): the Messenger module's events,
/// by name and payload, off the notifications' own hub — no second connection is opened. As the phone does it.
/// </summary>
public sealed class MessengerLiveEvents(ModuleLiveEventRouter events) : IMessengerLiveEvents
{
    /// <summary>The Messenger module's id: the one the server stamps on its events.</summary>
    public const string ModuleId = "messenger";

    public IDisposable Subscribe(Action<string, string> handler) =>
        events.Subscribe(ModuleId, e => handler(e.Name, e.Payload));
}
