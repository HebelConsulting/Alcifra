using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alcifra.Api.Controllers;
using Alcifra.Api.Notifications;
using Alcifra.Domain.Notifications;

namespace Alcifra.UnitTests;

// Core #1805: pushes to iPhones and iPads through APNs. The provider token, the content-free payload, how Apple's answers
// are acted on, and what a phone may register — without contacting Apple.
public class ApnsTests
{
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static ApnsOptions Options() => new() { KeyId = "ABC123DEFG", TeamId = "Y45XPY3925", PrivateKeyPem = Key.ExportPkcs8PrivateKeyPem() };

    [Fact]
    public void The_provider_token_is_es256_signed_by_the_key_with_its_id_and_the_team_as_issuer()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-19T08:00:00Z"));
        var token = new ApnsClient(Options(), clock).ProviderToken().Split('.');

        using var header = JsonDocument.Parse(FromBase64Url(token[0]));
        using var claims = JsonDocument.Parse(FromBase64Url(token[1]));
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("ABC123DEFG", header.RootElement.GetProperty("kid").GetString());
        Assert.Equal("Y45XPY3925", claims.RootElement.GetProperty("iss").GetString());
        Assert.Equal(clock.GetUtcNow().ToUnixTimeSeconds(), claims.RootElement.GetProperty("iat").GetInt64());
        Assert.True(Key.VerifyData(Encoding.ASCII.GetBytes($"{token[0]}.{token[1]}"), FromBase64Url(token[2]),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void The_provider_token_is_reused_for_forty_minutes_then_renewed()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-19T08:00:00Z"));
        var client = new ApnsClient(Options(), clock);
        var first = client.ProviderToken();

        clock.Advance(TimeSpan.FromMinutes(39));
        Assert.Equal(first, client.ProviderToken());

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.NotEqual(first, client.ProviderToken());
    }

    [Fact]
    public void The_payload_is_a_generic_alert_and_names_only_the_notification_and_its_tenant()
    {
        var (id, tenant) = (Guid.NewGuid(), Guid.NewGuid());
        using var payload = JsonDocument.Parse(ApnsClient.Payload(id, tenant));
        var alert = payload.RootElement.GetProperty("aps").GetProperty("alert");

        Assert.Equal(Alcifra.Localization.Strings.ProductName, alert.GetProperty("title").GetString());
        Assert.Equal("MobPushNew", alert.GetProperty("loc-key").GetString());   // the app's own text, in the device's language
        Assert.False(alert.TryGetProperty("body", out _));
        Assert.Equal(id, payload.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(tenant, payload.RootElement.GetProperty("tenant").GetGuid());
        Assert.Equal(["aps", "id", "tenant"], payload.RootElement.EnumerateObject().Select(p => p.Name).Order());
    }

    [Theory]
    [InlineData(410, "Unregistered", ApnsClient.Outcome.Gone)]
    [InlineData(400, "BadDeviceToken", ApnsClient.Outcome.Gone)]
    [InlineData(400, "DeviceTokenNotForTopic", ApnsClient.Outcome.Gone)]
    [InlineData(403, "ExpiredProviderToken", ApnsClient.Outcome.CredentialsRefused)]
    [InlineData(403, "InvalidProviderToken", ApnsClient.Outcome.CredentialsRefused)]
    [InlineData(429, "TooManyRequests", ApnsClient.Outcome.Failed)]
    [InlineData(500, null, ApnsClient.Outcome.Failed)]
    public void Apples_refusals_are_acted_on_as_documented(int status, string? reason, ApnsClient.Outcome outcome) =>
        Assert.Equal(outcome, ApnsClient.Classify(status, reason));

    [Theory]
    [InlineData(PushTransports.Sandbox, true, true)]
    [InlineData(PushTransports.Production, true, true)]
    [InlineData(PushTransports.Production, false, false)]   // the installation does not send through APNs
    [InlineData("staging", true, false)]
    [InlineData(null, true, false)]
    public void An_apns_registration_needs_a_hex_token_an_environment_and_an_installation_that_sends_through_apns(
        string? environment, bool apnsConfigured, bool valid)
    {
        var request = new PushRegistrationsController.RegistrationRequest
        {
            App = "dev.simplarchive.mobile",
            DeviceLabel = "iPhone · Anna on demo",
            Transport = PushTransports.Apns,
            Endpoint = new string('a', 64),
            ApnsEnvironment = environment,
        };

        Assert.Equal(valid, PushRegistrationRules.IsValid(request, requireApp: true, development: false, apnsConfigured));
    }

    [Theory]
    [InlineData("not-hex-not-hex-not-hex-not-hex-not-hex-not-hex-not-hex-not-hex-")]
    [InlineData("abc")]
    public void An_apns_token_that_is_not_hex_of_a_plausible_length_is_refused(string token)
    {
        var request = new PushRegistrationsController.RegistrationRequest
        {
            App = "dev.simplarchive.mobile",
            DeviceLabel = "iPhone",
            Transport = PushTransports.Apns,
            Endpoint = token,
            ApnsEnvironment = PushTransports.Sandbox,
        };

        Assert.False(PushRegistrationRules.IsValid(request, requireApp: true, development: false, apnsConfigured: true));
    }

    [Fact]
    public void An_apns_registration_carries_no_web_push_keys_and_an_unknown_transport_is_refused()
    {
        var withKeys = new PushRegistrationsController.RegistrationRequest
        {
            App = "dev.simplarchive.mobile",
            DeviceLabel = "iPhone",
            Transport = PushTransports.Apns,
            Endpoint = new string('b', 64),
            ApnsEnvironment = PushTransports.Sandbox,
            P256dh = "x",
            Auth = "y",
        };
        var unknown = new PushRegistrationsController.RegistrationRequest { App = "a", DeviceLabel = "d", Transport = "fcm", Endpoint = "https://x" };

        Assert.False(PushRegistrationRules.IsValid(withKeys, requireApp: true, development: false, apnsConfigured: true));
        Assert.False(PushRegistrationRules.IsValid(unknown, requireApp: true, development: false, apnsConfigured: true));
    }

    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("K", "T", "PEM", false)]
    [InlineData("K", null, null, true)]
    [InlineData(null, "T", "PEM", true)]
    public void Apns_is_configured_whole_or_not_at_all(string? keyId, string? teamId, string? pem, bool partly) =>
        Assert.Equal(partly, new ApnsOptions { KeyId = keyId, TeamId = teamId, PrivateKeyPem = pem }.IsPartlyConfigured);

    // A local clock, as the other tests here keep one, rather than a package for it.
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}
