namespace SimplArchive.ModuleAbi;

/// <summary>
/// An EPHEMERAL event a module sends to users' open apps over the core's live hub (ABI 1.25, core ADR 0957): the
/// Messenger's "Anna is typing…". Inject it into a controller.
/// </summary>
/// <remarks>
/// <para>
/// The event is <b>never stored</b> and <b>never reaches a closed app</b> — no notification row, no phone push, no mail, no
/// signal. It is fire-and-forget: a recipient with no open connection simply does not get it, and nothing tells the module.
/// Use it only for what is worthless a few seconds later; anything a user must not miss is a
/// <see cref="IModuleNotices.SignalAsync"/>.
/// </para>
/// <para>
/// The host stamps the event with the CALLING module's id, so a module cannot speak as another, and delivers it only to
/// active users of the tenant the module acts in. The module must be active there and its principal not revoked.
/// </para>
/// <para>
/// The payload is the module's own string (typically compact JSON), at most <see cref="ModuleLiveEvent.MaxPayloadBytes"/>
/// bytes as UTF-8. It travels in the clear over TLS, so it carries no sealed content — the Messenger sends who types, never
/// what. The host never logs it.
/// </para>
/// </remarks>
public interface IModuleLiveEvents
{
    /// <summary>
    /// Hands <paramref name="liveEvent"/> to the live hub for its recipients' open connections and returns how many of the
    /// addressed ids are active users of this tenant (the others are dropped). Throws a
    /// <see cref="ModuleLiveEventRefusedException"/> when the event is refused: too large, a bad name, too many recipients,
    /// the sender's rate exceeded, or the module not permitted to send here.
    /// </summary>
    Task<int> SendAsync(ModuleLiveEvent liveEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// What a live event says (ABI 1.25): to whom, a short name the module's clients branch on (<c>typing</c>), and the module's
/// payload.
/// </summary>
public sealed record ModuleLiveEvent(IReadOnlyList<Guid> RecipientUserIds, string Name, string Payload)
{
    /// <summary>The largest payload, in UTF-8 bytes: 2 KiB — enough for a conversation id and a few user ids, too small to
    /// be a side channel for content.</summary>
    public const int MaxPayloadBytes = 2048;

    /// <summary>The longest event name. A name is lowercase ASCII letters, digits, <c>.</c>, <c>_</c> and <c>-</c>,
    /// starting with a letter or digit.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The most distinct recipients one event may address.</summary>
    public const int MaxRecipients = 256;

    /// <summary>How many events one sender — the module acting for one caller — may send per
    /// <see cref="RateWindowSeconds"/>.</summary>
    public const int MaxEventsPerSender = 20;

    /// <summary>How many events the module may send per <see cref="RateWindowSeconds"/> in one tenant, over all callers.</summary>
    public const int MaxEventsPerModule = 1000;

    /// <summary>The rate limits' fixed window, in seconds.</summary>
    public const int RateWindowSeconds = 10;
}

/// <summary>A live event was refused (ABI 1.25); nothing was sent. Catch this to treat every refusal alike.</summary>
public abstract class ModuleLiveEventRefusedException(string errorCode, int statusCode, string message)
    : ModuleApiException(errorCode, statusCode, message);

/// <summary>The payload exceeds <see cref="ModuleLiveEvent.MaxPayloadBytes"/>, or the event addresses more than
/// <see cref="ModuleLiveEvent.MaxRecipients"/> users.</summary>
public sealed class ModuleLiveEventTooLargeException(string message)
    : ModuleLiveEventRefusedException("MODULE_LIVE_EVENT_TOO_LARGE", 413, message);

/// <summary>The event's name is empty, too long, or not made of lowercase letters, digits, <c>.</c>, <c>_</c> and
/// <c>-</c>, or it addresses nobody.</summary>
public sealed class ModuleLiveEventInvalidException(string message)
    : ModuleLiveEventRefusedException("MODULE_LIVE_EVENT_INVALID", 422, message);

/// <summary>The sender's or the module's rate (<see cref="ModuleLiveEvent.MaxEventsPerSender"/>,
/// <see cref="ModuleLiveEvent.MaxEventsPerModule"/>) is spent for the current window. A typing indicator drops the event.</summary>
public sealed class ModuleLiveEventRateLimitedException()
    : ModuleLiveEventRefusedException("MODULE_LIVE_EVENT_RATE_LIMITED", 429, "Too many live events; try again in a few seconds.");

/// <summary>The module may not send here: no module is acting, the module is not active in this tenant, or its principal
/// was revoked (the consent withdrawn).</summary>
public sealed class ModuleLiveEventNotPermittedException(string message)
    : ModuleLiveEventRefusedException("MODULE_LIVE_EVENT_NOT_PERMITTED", 403, message);
