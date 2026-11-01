namespace Alcifra.EndToEndTests;

/// <summary>
/// A protocol device for a test that needs a password for IMAP, WebDAV, CalDAV or CardDAV (#1970): every switch on and a
/// name of its own, since a user's device names are unique. Posted to <c>/api/me/devices</c>; the answer's
/// <c>password</c> and <c>username</c> are what the earlier password endpoints answered.
/// </summary>
internal static class E2EDevices
{
    public static object Any() => new { name = $"e2e-{Guid.NewGuid():N}" };
}
