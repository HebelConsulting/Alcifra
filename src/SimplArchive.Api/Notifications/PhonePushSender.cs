using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.CalDav;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Notifications;

/// <summary>
/// Sends a new notification to the recipient's registered phones (SimplArchiveMobile #20, slice B; owner decisions
/// 2026-10-12): right after the save, best effort, never blocking the request. Per recipient it skips a type whose
/// phone switch is off, encrypts the (id-only) payload to each device (RFC 8291 aes128gcm, <see cref="WebPushMessage"/>; signed
/// with the installation's VAPID key, RFC 8292),
/// and deletes a registration the push server reports gone.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a push carries: the notification's id, nothing else, on every tenant</b> (owner decision 2026-10-12). Web
/// Push allows only AES-128-GCM over a P-256 key agreement (RFC 8291), which is not what the owner wants content to
/// rest on, least of all with quantum attacks in mind. So the push only wakes the phone; it fetches the notification
/// over TLS and keeps it in its own encrypted cache, readable offline.
/// </para>
/// <para>
/// It runs in its own scope with the notification's tenant set, as the email dispatcher does, because the request
/// that saved the notification has moved on. A crash between the save and the send loses that one push; the stored
/// notification still shows when the app is opened.
/// </para>
/// </remarks>
public sealed class PhonePushSender(
    IServiceScopeFactory scopes,
    DavPushConfiguration webPush,
    Microsoft.Extensions.Options.IOptions<ApnsOptions> apnsOptions,
    ApnsClient apns,
    IHttpClientFactory httpClients,
    ILogger<PhonePushSender> logger) : IPhonePushNotifier
{
    /// <summary>How long one device's push may take before it is given up.</summary>
    private static readonly TimeSpan PerDevice = TimeSpan.FromSeconds(10);

    public void Notify(IReadOnlyList<PhonePushItem> items)
    {
        if ((!webPush.IsEnabled && !apnsOptions.Value.IsConfigured) || items.Count == 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (var tenant in items.GroupBy(i => i.TenantId))
            {
                try
                {
                    await SendAsync(tenant.Key, [.. tenant]);
                }
                catch (Exception e)
                {
                    // Best effort: the notification is stored, the app shows it when opened.
                    logger.LogWarning(e, "Phone pushes for tenant {TenantId} could not be sent", tenant.Key);
                }
            }
        });
    }

    private async Task SendAsync(Guid tenantId, IReadOnlyList<PhonePushItem> items)
    {
        using var scope = scopes.CreateScope();
        ((CurrentTenantAccessor)scope.ServiceProvider.GetRequiredService<ICurrentTenantAccessor>()).TenantId = tenantId;
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        var recipients = items.Select(i => i.UserId).Distinct().ToList();
        var devices = await db.PhonePushRegistrations.AsNoTracking().Where(r => recipients.Contains(r.UserId)).ToListAsync();
        if (devices.Count == 0)
        {
            return;
        }

        var phoneOff = await db.UserNotificationPreferences.AsNoTracking()
            .Where(p => recipients.Contains(p.UserId) && !p.PhoneEnabled)
            .Select(p => new { p.UserId, Type = (int)p.Type })
            .ToListAsync();

        using var http = httpClients.CreateClient(nameof(PhonePushSender));
        http.Timeout = PerDevice;
        using var apple = httpClients.CreateClient(ApnsClient.HttpClientName);
        apple.Timeout = PerDevice;
        var gone = new List<Guid>();
        foreach (var item in items.Where(i => !phoneOff.Any(p => p.UserId == i.UserId && p.Type == i.Type)))
        {
            var payload = System.Text.Encoding.UTF8.GetBytes(Payload(item));
            foreach (var device in devices.Where(d => d.UserId == item.UserId))
            {
                if (device.Transport == Domain.Notifications.PushTransports.Apns)
                {
                    await SendApnsAsync(apple, device, item, gone);
                    continue;
                }

                if (!webPush.IsEnabled)
                {
                    continue;
                }

                try
                {
                    using var response = await http.SendAsync(Request(device, payload));
                    if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
                    {
                        gone.Add(device.Id);   // the push server forgot it: retrying a gone address forever is how a queue rots
                    }
                    else if (!response.IsSuccessStatusCode)
                    {
                        logger.LogWarning("A phone push to registration {RegistrationId} was refused with {Status}", device.Id, (int)response.StatusCode);
                    }
                }
                catch (Exception e)
                {
                    // Never the endpoint: it identifies a device. The registration id is enough to find it.
                    logger.LogWarning(e, "A phone push to registration {RegistrationId} failed", device.Id);
                }
            }
        }

        if (gone.Count > 0)
        {
            await db.PhonePushRegistrations.Where(r => gone.Contains(r.Id)).ExecuteDeleteAsync();
            logger.LogInformation("Removed {Count} phone push registration(s) the push server reported gone", gone.Count);
        }
    }

    // An iPhone or iPad through APNs (core #1805): a token Apple calls gone is unregistered; refused credentials are an
    // administrator's to fix, and say so (ADR 0626) — never the token itself, which identifies a device.
    private async Task SendApnsAsync(HttpClient apple, Domain.Notifications.PhonePushRegistration device, PhonePushItem item, List<Guid> gone)
    {
        if (!apnsOptions.Value.IsConfigured)
        {
            return;
        }

        try
        {
            var (outcome, status, reason) = await apns.SendAsync(apple, device, item.NotificationId, CancellationToken.None);
            switch (outcome)
            {
                case ApnsClient.Outcome.Gone:
                    gone.Add(device.Id);
                    break;
                case ApnsClient.Outcome.CredentialsRefused:
                    logger.LogWarning("APNs refused the installation's credentials ({Status} {Reason}): no iPhone or iPad receives pushes until the Apns settings are fixed", status, reason);
                    break;
                case ApnsClient.Outcome.Failed:
                    logger.LogWarning("An APNs push to registration {RegistrationId} was refused with {Status} {Reason}", device.Id, status, reason);
                    break;
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "An APNs push to registration {RegistrationId} failed", device.Id);
        }
    }

    // RFC 8030: the encrypted body, how long the push service may hold it, and the VAPID authorisation (RFC 8292).
    private HttpRequestMessage Request(Domain.Notifications.PhonePushRegistration device, byte[] payload)
    {
        var body = new ByteArrayContent(WebPushMessage.Encrypt(
            WebPushMessage.FromBase64Url(device.P256dh!), WebPushMessage.FromBase64Url(device.Auth!), payload));
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        body.Headers.ContentEncoding.Add("aes128gcm");
        var request = new HttpRequestMessage(HttpMethod.Post, device.Endpoint) { Content = body };
        request.Headers.Add("TTL", "86400");   // a phone off for a day still gets it when it comes back
        request.Headers.Add("Urgency", "normal");
        request.Headers.TryAddWithoutValidation("Authorization",
            WebPushMessage.Authorization(device.Endpoint, webPush.Subject, webPush.VapidPublicKey!, webPush.VapidPrivateKey!, DateTimeOffset.UtcNow));
        return request;
    }

    /// <summary>The push's content, before encryption to the device: the notification's id, and nothing that says what it is about.</summary>
    public static string Payload(PhonePushItem item) => JsonSerializer.Serialize(new { id = item.NotificationId });
}
