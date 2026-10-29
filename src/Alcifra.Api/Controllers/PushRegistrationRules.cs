using Alcifra.Domain.Notifications;

namespace Alcifra.Api.Controllers;

/// <summary>
/// What a phone may register for push (SimplArchiveMobile #20; APNs, core #1805), per transport: a Web Push endpoint
/// with usable keys, or an APNs device token with its environment — the latter only where the installation sends through
/// APNs. Its own class so it is unit-tested without the controller.
/// </summary>
public static class PushRegistrationRules
{
    private const int EndpointMax = 2000, KeyMax = 200, LabelMax = 100;

    /// <summary>Whether <paramref name="request"/> is a usable registration for its transport.</summary>
    public static bool IsValid(PushRegistrationsController.RegistrationRequest request, bool requireApp, bool development, bool apnsConfigured)
    {
        var device = (!requireApp || request.App.Trim() is { Length: > 0 and <= LabelMax })
            && request.DeviceLabel.Trim() is { Length: > 0 and <= LabelMax };
        return device && request.Transport switch
        {
            PushTransports.WebPush => ValidWebPush(request, development),
            // Only where the installation sends through APNs: a token it cannot use would be a registration that never
            // receives anything, without anyone noticing.
            PushTransports.Apns => apnsConfigured && ValidApns(request),
            _ => false,
        };
    }

    // https only: the server posts to whatever address a device names, so a plain-http or internal address would let
    // a user aim it at the installation's own services. Loopback http is accepted in Development alone, where a test
    // or a developer's local push server listens on it.
    private static bool ValidWebPush(PushRegistrationsController.RegistrationRequest request, bool development) =>
        request.Endpoint.Length <= EndpointMax
        && Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint)
        && (endpoint.Scheme == Uri.UriSchemeHttps || (development && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
        && request.P256dh is { Length: > 0 and <= KeyMax } && DecodedLength(request.P256dh) is 65
        && request.Auth is { Length: > 0 and <= KeyMax } && DecodedLength(request.Auth) is 16;

    // An APNs device token: hex, as the device received it (32 bytes today; Apple allows it to grow, so up to 100 bytes),
    // and which APNs it belongs to. The server only ever sends it to Apple's own hosts, so it cannot aim anything elsewhere.
    private static bool ValidApns(PushRegistrationsController.RegistrationRequest request) =>
        request.Endpoint is { Length: >= 64 and <= 200 } token && token.Length % 2 == 0 && token.All(Uri.IsHexDigit)
        && request.ApnsEnvironment is PushTransports.Sandbox or PushTransports.Production
        && string.IsNullOrEmpty(request.P256dh) && string.IsNullOrEmpty(request.Auth);

    // RFC 8291's keys, checked here rather than at the first push: an uncompressed P-256 point (65 bytes) and a 16-byte
    // auth secret, base64url. A registration whose keys cannot encrypt would otherwise fail silently on every push.
    private static int DecodedLength(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        var buffer = new byte[padded.Length];
        return Convert.TryFromBase64String(padded, buffer, out var written) ? written : -1;
    }
}
