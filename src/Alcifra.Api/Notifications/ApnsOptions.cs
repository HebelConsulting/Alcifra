namespace Alcifra.Api.Notifications;

/// <summary>
/// The installation's Apple Push Notification service credentials (core #1805): token-based APNs, with a key from the
/// developer account. Unset, the installation sends no APNs pushes and offers none (iPhones then get notifications only
/// while the app is open). Partly set is refused in production (ProductionReadinessValidator): a half-configured
/// sender would fail every push silently.
/// </summary>
public sealed class ApnsOptions
{
    public const string SectionName = "Apns";

    /// <summary>The APNs key's id (ten characters, from the developer account's Keys page).</summary>
    public string? KeyId { get; set; }

    /// <summary>The developer team's id, the token's issuer.</summary>
    public string? TeamId { get; set; }

    /// <summary>The key itself, the <c>.p8</c> file's PEM content. A secret: from OpenBao where configured, never logged (ADR 0886).</summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>Whether all three are set: only then does the installation send through APNs and offer it to phones.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(TeamId) && !string.IsNullOrWhiteSpace(PrivateKeyPem);

    /// <summary>Whether some but not all are set — refused in production rather than sent with nothing.</summary>
    public bool IsPartlyConfigured =>
        !IsConfigured && (!string.IsNullOrWhiteSpace(KeyId) || !string.IsNullOrWhiteSpace(TeamId) || !string.IsNullOrWhiteSpace(PrivateKeyPem));
}
