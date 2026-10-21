using Microsoft.Extensions.Logging;

namespace SimplArchive.ApiClient.KeyTest;

/// <summary>
/// Reads the archive's side of a key for the report — through rels the installation advertises, never a composed
/// address (ADR 0543), and only for a signed-in session (ADR 0955).
/// </summary>
/// <remarks>
/// <para>
/// <b>Best-effort, and it says what it missed.</b> The archive side is context for support, not part of the test:
/// a key that works is a key that works whether or not the server answered. So each read is guarded on its own,
/// and a failure becomes a line in <see cref="KeyTestArchiveSide.Problems"/> rather than a failed test.
/// </para>
/// <para>
/// <b>The tenant's encryption posture</b> is the <c>encryption</c> rel on <c>/me</c> (ADR 0955); an installation older
/// than it advertises no such rel, and the report says "not advertised by this server" rather than composing a URL.
/// </para>
/// </remarks>
public static class KeyTestArchiveProbe
{
    /// <summary>Reads the archive side for the certificates the test sealed to.</summary>
    /// <param name="core">The signed-in session's core.</param>
    /// <param name="profile">The same session's profile client (the <c>/me</c> rels).</param>
    /// <param name="testedThumbprints">SHA-256 of each certificate the test sealed to, lower-case hex.</param>
    public static async Task<KeyTestArchiveSide> GatherAsync(
        ApiCore core, ProfileClient profile, IReadOnlyList<string> testedThumbprints, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();

        var version = await Read(() => core.GetServerVersionAsync(cancellationToken), "the server version", problems);

        IReadOnlyList<KeyTestEnrolment>? enrolments = null;
        string? source = null, reason = null;
        var set = await Read(() => profile.GetReaderCertificatesAsync(cancellationToken), "your certificate set", problems);
        if (set is not null)
        {
            var enrolled = set.Certificates
                .Select(c => Normalise(c.ThumbprintSha256))
                .ToHashSet(StringComparer.Ordinal);
            enrolments = testedThumbprints.Select(t => new KeyTestEnrolment(t, enrolled.Contains(Normalise(t)))).ToList();
            source = set.Source;
            reason = set.Reason;
        }

        var policy = await Read(() => profile.GetOfflineCachePolicyAsync(cancellationToken), "the PIN policy", problems);
        var posture = await Read(() => profile.GetEncryptionPostureAsync(cancellationToken), "the encryption mode", problems);

        return new KeyTestArchiveSide(
            Address(core.RootUrl),
            version,
            source,
            reason,
            enrolments,
            posture is null
                ? null
                : new KeyTestEncryption(posture.Mode, posture.WrapsAtRest, posture.RefusesPlaintextDoors, posture.DeliversEnvelopes, posture.EnvelopesMail),
            policy?.PinOncePerSession,
            policy?.UnlockMode,
            problems);
    }

    /// <summary>The address as scheme, host and path — nothing that could carry a credential (user info, query).</summary>
    public static string Address(string rootUrl) =>
        Uri.TryCreate(rootUrl, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}".TrimEnd('/')
            : rootUrl;

    private static string Normalise(string thumbprint) =>
        thumbprint.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();

    private static async Task<T?> Read<T>(Func<Task<T?>> read, string what, List<string> problems)
        where T : class
    {
        try
        {
            return await read();
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or TaskCanceledException
                                      or System.Text.Json.JsonException or KeyNotFoundException)
        {
            // A Warning names what was missed; the report carries the reason in words (the exception's type and
            // status, never a body). ADR 0626: anyone reading the report finds out without our logs.
            ApiClientSettings.Logger.LogWarning(
                "The hardware-key report could not read {What} ({ExceptionType}); the report says so", what, e.GetType().Name);
            problems.Add(e is HttpRequestException { StatusCode: { } status }
                ? $"{what}: HTTP {(int)status}"
                : $"{what}: {e.GetType().Name}");
            return null;
        }
    }
}
