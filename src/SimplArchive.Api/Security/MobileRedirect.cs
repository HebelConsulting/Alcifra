namespace SimplArchive.Api.Security;

/// <summary>
/// The mobile app's redirect (ADR 0916): a private-use URI scheme (RFC 8252 §7.1), the reverse-DNS form of the app id,
/// in ONE place for the reason <see cref="DesktopLoopback"/> gives. The client registration accepts it, and the
/// content-security policy's <c>form-action</c> must let the browser reach it, or sign-in silently never returns to the app.
/// </summary>
public static class MobileRedirect
{
    /// <summary>The registered redirect URI, which must match what the app requests exactly.</summary>
    public const string RedirectUri = SimplArchive.Presentation.WireNames.MobileRedirectUri;

    /// <summary>Every redirect the mobile client accepts: the app's before its identifier moves (Mobile #187) too.</summary>
    public static readonly IReadOnlyList<string> RedirectUris = [RedirectUri, SimplArchive.Presentation.WireNames.Legacy.MobileRedirectUri];

    /// <summary>Their schemes as content-security-policy scheme sources, space-separated.</summary>
    public static readonly string SchemeSources = string.Join(' ', RedirectUris.Select(u => u[..(u.IndexOf(':') + 1)]));
}
