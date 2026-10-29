using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alcifra.Domain.Notifications;

namespace Alcifra.Api.Notifications;

/// <summary>
/// One push to an iPhone or iPad through Apple's push service (core #1805): token-based APNs over HTTP/2, the provider
/// token signed (ES256) with the installation's <c>.p8</c> key and reused for a while, as Apple asks.
/// </summary>
/// <remarks>
/// <b>What it carries (owner decision 2026-10-19): nothing about the notification.</b> iOS shows a generic alert itself —
/// the title "Alcifra" and the text key <c>MobPushNew</c>, which iOS looks up in the app in the device's language —
/// so it reaches a closed app reliably; tapping it opens the app, which fetches the real notifications over TLS. The
/// data names only the notification and its tenant, as the Web Push payload names the notification alone.
/// </remarks>
public sealed class ApnsClient(ApnsOptions options, TimeProvider clock)
{
    /// <summary>The name of the registered HttpClient (HTTP/2, the outbound guard).</summary>
    public const string HttpClientName = "Apns";

    // Apple refuses a provider token older than an hour and throttles one renewed more than every 20 minutes.
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(40);
    private readonly Lock _gate = new();
    private (string Token, DateTimeOffset IssuedAt)? _token;

    /// <summary>How Apple answered one push.</summary>
    public enum Outcome
    {
        Delivered,

        /// <summary>The token is no longer valid for this app (uninstalled, a wrong environment): unregister it.</summary>
        Gone,

        /// <summary>The installation's credentials were refused: every push fails until an administrator fixes them.</summary>
        CredentialsRefused,

        /// <summary>Anything else, logged and not retried.</summary>
        Failed,
    }

    public async Task<(Outcome Outcome, int Status, string? Reason)> SendAsync(
        HttpClient http, PhonePushRegistration device, Guid notificationId, CancellationToken cancellationToken)
    {
        var host = device.ApnsEnvironment == PushTransports.Production ? "api.push.apple.com" : "api.sandbox.push.apple.com";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/3/device/{device.Endpoint}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(Payload(notificationId, device.TenantId), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("authorization", $"bearer {ProviderToken()}");
        request.Headers.TryAddWithoutValidation("apns-topic", device.App);   // the app's bundle id
        request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
        request.Headers.TryAddWithoutValidation("apns-priority", "10");
        request.Headers.TryAddWithoutValidation("apns-expiration", clock.GetUtcNow().AddDays(1).ToUnixTimeSeconds().ToString());

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return (Outcome.Delivered, (int)response.StatusCode, null);
        }

        var reason = ReasonOf(await response.Content.ReadAsStringAsync(cancellationToken));
        return (Classify((int)response.StatusCode, reason), (int)response.StatusCode, reason);
    }

    /// <summary>How an APNs refusal is acted on (Apple's documented status codes and reasons).</summary>
    public static Outcome Classify(int status, string? reason) => (status, reason) switch
    {
        (410, _) => Outcome.Gone,
        (400, "BadDeviceToken" or "DeviceTokenNotForTopic") => Outcome.Gone,
        (403, "ExpiredProviderToken" or "InvalidProviderToken" or "MissingProviderToken") => Outcome.CredentialsRefused,
        _ => Outcome.Failed,
    };

    /// <summary>The push's body: a generic alert iOS shows itself, and which notification it is, nothing about it.</summary>
    public static string Payload(Guid notificationId, Guid tenantId) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["aps"] = new Dictionary<string, object>
        {
            ["alert"] = new Dictionary<string, object> { ["title"] = Alcifra.Localization.Strings.ProductName, ["loc-key"] = "MobPushNew" },
            ["sound"] = "default",
            ["thread-id"] = tenantId.ToString("N"),   // grouped per installation's tenant in Notification Center
        },
        ["id"] = notificationId,
        ["tenant"] = tenantId,
    });

    /// <summary>The provider token (RFC 7519, ES256): the team as issuer, the key's id in the header, reused for 40 minutes.</summary>
    public string ProviderToken()
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_token is { } current && now - current.IssuedAt < TokenLifetime)
            {
                return current.Token;
            }

            var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = options.KeyId }));
            var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = options.TeamId, iat = now.ToUnixTimeSeconds() }));
            using var key = ECDsa.Create();
            key.ImportFromPem(options.PrivateKeyPem);
            var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var token = $"{header}.{claims}.{Base64Url(signature)}";
            _token = (token, now);
            return token;
        }
    }

    private static string? ReasonOf(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
