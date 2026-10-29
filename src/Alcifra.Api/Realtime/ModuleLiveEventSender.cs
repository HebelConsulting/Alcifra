using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Alcifra.Api.Security;
using Alcifra.Application.Abstractions;
using Alcifra.Infrastructure.Modules;
using Alcifra.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;
using WireEvent = Alcifra.Presentation.ModuleLiveEvent;

namespace Alcifra.Api.Realtime;

/// <summary>
/// The host's <see cref="IModuleLiveEvents"/> (ABI 1.25, ADR 0957): a module's ephemeral event, handed to the live hub for
/// its recipients' open connections — the same hub, connections and backplane as the in-app notifications.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing is stored</b>: no notification row, no outbox, no phone push. So there is nothing to audit (the audit trail
/// records what changed, and nothing does) and nothing to concurrency-track (an event is not an entity; there is no value
/// two writers could overwrite). Both are ADR 0957's stated reasons, not omissions.
/// </para>
/// <para>
/// Gate, in order: a module is acting (the controller gate set its identity), the tenant is known, the event is well
/// formed, the module is active in the tenant and its principal not revoked (the consent act, ADR 0736), and the sender's
/// and the module's rate is not spent. The rate counters live in the throttle's store — Valkey when configured, so the
/// budget is the installation's and not one per instance.
/// </para>
/// <para>
/// Logging: one Debug line per event (module, name, counts), the recipients at Trace, the payload NEVER — it is the
/// module's, and a typing state is exactly the kind of thing a log should not collect.
/// </para>
/// </remarks>
public sealed class ModuleLiveEventSender(
    ModuleIdentityAccessor identity,
    ICurrentTenantAccessor tenant,
    ICurrentUserAccessor currentUser,
    ICurrentServiceAccountAccessor currentServiceAccount,
    AlcifraDbContext dbContext,
    IThrottleCounterStore counters,
    IHubContext<NotificationsHub> hub,
    TimeProvider time,
    ILogger<ModuleLiveEventSender> logger) : IModuleLiveEvents
{
    public async Task<int> SendAsync(ModuleLiveEvent liveEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        var moduleId = identity.ModuleId
            ?? throw new ModuleLiveEventNotPermittedException("A live event can be sent only by a module acting in a request.");
        var tenantId = tenant.TenantId
            ?? throw new ModuleLiveEventNotPermittedException($"Module '{moduleId}' sent a live event with no tenant.");

        var recipients = ModuleLiveEventRules.Validate(liveEvent);
        await RequirePermittedAsync(moduleId, cancellationToken);
        await RequireRateAsync(tenantId, moduleId, cancellationToken);

        var active = await dbContext.Users.AsNoTracking()
            .Where(u => recipients.Contains(u.Id) && u.IsActive)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        logger.LogDebug("Module {ModuleId} sent live event {EventName} to {DeliveredCount} of {AddressedCount} addressed users",
            moduleId, liveEvent.Name, active.Count, recipients.Count);
        logger.LogTrace("Module {ModuleId} live event {EventName}: recipients {RecipientIds}, payload {PayloadBytes} bytes",
            moduleId, liveEvent.Name, active, Encoding.UTF8.GetByteCount(liveEvent.Payload));
        if (active.Count == 0)
        {
            return 0;
        }

        try
        {
            await hub.Clients.Users(active.Select(id => id.ToString()).ToList())
                .SendAsync(WireEvent.HubMethod, new WireEvent(moduleId, liveEvent.Name, liveEvent.Payload), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fire-and-forget (ADR 0957): the module learns nothing either way, so the dropped event is ours to report.
            logger.LogWarning(exception, "The live hub could not take module {ModuleId}'s live event {EventName}; it was dropped",
                moduleId, liveEvent.Name);
        }

        return active.Count;
    }

    // Activation is the consent act (ADR 0736) and the principal it created is the module here; a revoked principal is
    // consent withdrawn. The controller gate already asked "active"; this asks again because the seam is not only
    // reachable from a gated controller.
    private async Task RequirePermittedAsync(string moduleId, CancellationToken cancellationToken)
    {
        if (!await ModuleActivationCheck.IsActiveAsync(dbContext, moduleId, time.GetUtcNow(), cancellationToken))
        {
            logger.LogDebug("Refused module {ModuleId}'s live event: the module is not active in this tenant", moduleId);
            throw new ModuleLiveEventNotPermittedException($"Module '{moduleId}' is not active in this tenant.");
        }

        if (await ModulePrincipal.FindAsync(dbContext, moduleId, cancellationToken) is not { IsActive: true })
        {
            logger.LogDebug("Refused module {ModuleId}'s live event: its principal is revoked", moduleId);
            throw new ModuleLiveEventNotPermittedException($"Module '{moduleId}''s principal is revoked in this tenant.");
        }
    }

    // Fixed windows: the window's index is part of the key, so each window counts from zero. (The store's own window
    // restarts on every call, which for a steady stream would never end.)
    private async Task RequireRateAsync(Guid tenantId, string moduleId, CancellationToken cancellationToken)
    {
        var window = TimeSpan.FromSeconds(ModuleLiveEvent.RateWindowSeconds);
        var index = time.GetUtcNow().ToUnixTimeSeconds() / ModuleLiveEvent.RateWindowSeconds;
        var sender = currentUser.UserId is { } userId ? $"u:{userId}"
            : currentServiceAccount.ServiceAccountId is { } serviceAccountId ? $"s:{serviceAccountId}"
            : "none";
        var prefix = $"module-live-event:{tenantId}:{moduleId}";

        var perSender = await counters.CountAsync($"{prefix}:{sender}:{index}", window, cancellationToken);
        var perModule = await counters.CountAsync($"{prefix}:{index}", window, cancellationToken);
        if (perSender > ModuleLiveEvent.MaxEventsPerSender || perModule > ModuleLiveEvent.MaxEventsPerModule)
        {
            logger.LogDebug("Refused module {ModuleId}'s live event: rate spent ({SenderCount} by this sender, {ModuleCount} "
                + "by the module in this window)", moduleId, perSender, perModule);
            throw new ModuleLiveEventRateLimitedException();
        }
    }
}

/// <summary>The shape a live event must have (ABI 1.25, ADR 0957) — pure, so the limits are tested without a host.</summary>
public static class ModuleLiveEventRules
{
    /// <summary>Checks <paramref name="liveEvent"/> against the ABI's limits and returns its distinct, non-empty
    /// recipients; throws the specific refusal otherwise.</summary>
    public static HashSet<Guid> Validate(ModuleLiveEvent liveEvent)
    {
        ArgumentNullException.ThrowIfNull(liveEvent);
        if (!IsValidName(liveEvent.Name))
        {
            throw new ModuleLiveEventInvalidException(
                $"A live event's name is 1 to {ModuleLiveEvent.MaxNameLength} lowercase letters, digits, '.', '_' or '-'.");
        }

        var payloadBytes = Encoding.UTF8.GetByteCount(liveEvent.Payload ?? string.Empty);
        if (payloadBytes > ModuleLiveEvent.MaxPayloadBytes)
        {
            throw new ModuleLiveEventTooLargeException(
                $"A live event's payload is at most {ModuleLiveEvent.MaxPayloadBytes} bytes; this one is {payloadBytes}.");
        }

        var recipients = (liveEvent.RecipientUserIds ?? []).Where(id => id != Guid.Empty).ToHashSet();
        if (recipients.Count == 0)
        {
            throw new ModuleLiveEventInvalidException("A live event addresses at least one user.");
        }

        if (recipients.Count > ModuleLiveEvent.MaxRecipients)
        {
            throw new ModuleLiveEventTooLargeException(
                $"A live event addresses at most {ModuleLiveEvent.MaxRecipients} users; this one addresses {recipients.Count}.");
        }

        return recipients;
    }

    private static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= ModuleLiveEvent.MaxNameLength }
        && IsLowerAlphanumeric(name[0])
        && name.All(c => IsLowerAlphanumeric(c) || c is '.' or '_' or '-');

    private static bool IsLowerAlphanumeric(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
}
