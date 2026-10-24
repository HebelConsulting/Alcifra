using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace SimplArchive.ApiClient;

/// <summary>One of the user's enrolled keys, as far as matching a certificate or an envelope's recipients needs it.</summary>
/// <param name="Issuer">The certificate's issuer, as .NET formats a distinguished name (what a recipient names).</param>
/// <param name="Serial">The certificate's serial number, hexadecimal.</param>
/// <param name="Label">The name the key was enrolled under, shown to the user.</param>
public sealed record EnrolledKey(string Issuer, string Serial, string Label)
{
    /// <summary>The key a certificate stands for.</summary>
    public static EnrolledKey Of(X509Certificate2 certificate, string label) => new(certificate.Issuer, certificate.SerialNumber, label);

    /// <summary>Whether this is the key of <paramref name="certificate"/>.</summary>
    public bool Names(X509Certificate2 certificate) =>
        string.Equals(Serial, certificate.SerialNumber, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Issuer, certificate.Issuer, StringComparison.Ordinal);
}

/// <summary>The signed-in user's enrolled keys as far as the client knows them, or not known (<see cref="Keys"/> null).</summary>
/// <param name="FromMemory">Recalled from when the client was last online, not asked now.</param>
public sealed record EnrolledKeysAnswer(IReadOnlyList<EnrolledKey>? Keys, bool FromMemory = false)
{
    /// <summary>Nothing known: no key may be used.</summary>
    public static readonly EnrolledKeysAnswer Unknown = new((IReadOnlyList<EnrolledKey>?)null);
}

/// <summary>
/// Where a host keeps the user's enrolled keys as last seen online, so a client without the server (the phone's offline
/// copy) still uses only the user's own keys (ADR 0969). Public data — issuer, serial and label — never a secret.
/// </summary>
public interface IEnrolledKeysMemory
{
    IReadOnlyList<EnrolledKey>? Recall();

    void Remember(IReadOnlyList<EnrolledKey> keys);
}

/// <summary>The memory for one run only — the desktop, which opens nothing without the server.</summary>
public sealed class InMemoryEnrolledKeys : IEnrolledKeysMemory
{
    private IReadOnlyList<EnrolledKey>? _keys;

    public IReadOnlyList<EnrolledKey>? Recall() => _keys;

    public void Remember(IReadOnlyList<EnrolledKey> keys) => _keys = keys;
}

/// <summary>
/// The caller's enrolled keys, as the installation states them (ADR 0969): the enrolment listing (<c>certificates</c>,
/// every enrolled certificate, usable or not) and the reader set (<c>/me</c>'s <c>readerCertificates</c>, the user's own
/// column where no module enrols). Followed rels only.
/// </summary>
public static class EnrolledKeys
{
    /// <summary>The union of both, or null where neither is offered (an older installation, or the offline copy).</summary>
    public static async Task<IReadOnlyList<EnrolledKey>?> MineAsync(ApiCore core, CancellationToken cancellationToken = default)
    {
        var listed = await new EnrolledCertificatesClient(core).GetMineForMatchingAsync(cancellationToken);

        // The offline copy's responder answers no "me": asked only where the root offers it.
        var readers = (await core.GetRootLinksAsync(cancellationToken)).ContainsKey("me")
            ? await new ProfileClient(core).GetReaderCertificatesAsync(cancellationToken)
            : null;
        if (listed is null && readers is null)
        {
            return null;
        }

        var keys = new List<EnrolledKey>(listed ?? []);
        foreach (var reader in readers?.Certificates ?? [])
        {
            try
            {
                using var certificate = X509Certificate2.CreateFromPem(reader.Pem);
                keys.Add(EnrolledKey.Of(certificate, reader.Label));
            }
            catch (CryptographicException)
            {
                // A certificate this client cannot read is not one it can match: left out.
            }
        }

        return keys;
    }
}

/// <summary>
/// The signed-in user's enrolled keys for one session: asked of the server at most every <see cref="Fresh"/>, remembered
/// for when it cannot be asked, and <see cref="EnrolledKeysAnswer.Unknown"/> where it never could (ADR 0969).
/// </summary>
/// <remarks>
/// Asked before a key is used, so it must be cheap on the second ask: a preview opening a document and its pages
/// asks once. A key enrolled a moment ago is seen within <see cref="Fresh"/>.
/// </remarks>
public sealed class SignedInKeys(Func<CancellationToken, Task<IReadOnlyList<EnrolledKey>?>> fetch, IEnrolledKeysMemory memory)
{
    /// <summary>How long an answer from the server is used without asking again.</summary>
    public static TimeSpan Fresh { get; set; } = TimeSpan.FromMinutes(2);

    private EnrolledKeysAnswer? _answer;
    private DateTimeOffset _askedAt;

    /// <summary>The session over <paramref name="core"/>, remembering what it saw in <paramref name="memory"/>.</summary>
    public static SignedInKeys For(ApiCore core, IEnrolledKeysMemory? memory = null) =>
        new(ct => EnrolledKeys.MineAsync(core, ct), memory ?? new InMemoryEnrolledKeys());

    /// <summary>The user's enrolled keys now: fresh from the server, else as last seen online, else unknown.</summary>
    public async Task<EnrolledKeysAnswer> CurrentAsync(CancellationToken cancellationToken = default)
    {
        if (_answer is { FromMemory: false } fresh && DateTimeOffset.UtcNow - _askedAt < Fresh)
        {
            return fresh;
        }

        IReadOnlyList<EnrolledKey>? keys = null;
        try
        {
            keys = await fetch(cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            // Not reachable now: what was seen online stands in, below.
        }

        if (keys is not null)
        {
            memory.Remember(keys);
            (_answer, _askedAt) = (new EnrolledKeysAnswer(keys), DateTimeOffset.UtcNow);
            return _answer;
        }

        return memory.Recall() is { } recalled ? new EnrolledKeysAnswer(recalled, FromMemory: true) : EnrolledKeysAnswer.Unknown;
    }

    /// <summary>Forgets the server's answer, so the next ask goes to it again (a key was enrolled, the user asked again).</summary>
    public void Forget() => _answer = null;
}
