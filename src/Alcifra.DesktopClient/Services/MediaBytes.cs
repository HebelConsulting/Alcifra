using System.Net;
using System.Net.Http.Headers;

namespace Alcifra.DesktopClient.Services;

/// <summary>
/// Where the system's player reads a video or a sound from (#2036): its length, and any range of it, asked as it plays.
/// </summary>
public interface IMediaBytes
{
    /// <summary>The whole length, which the player is told before it asks for anything.</summary>
    long Length { get; }

    /// <summary>The bytes at <paramref name="offset"/>, exactly <paramref name="length"/> of them.</summary>
    Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken);
}

/// <summary>A file held in memory, whole: opened with the reader's key, or sent in Messages. Never written to storage.</summary>
public sealed class MemoryMediaBytes(byte[] content) : IMediaBytes
{
    public long Length => content.LongLength;

    public Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken) =>
        Task.FromResult(content.AsSpan((int)offset, length).ToArray());
}

/// <summary>
/// A document's version read from object storage a range at a time, as the player asks (owner, 2026-11-11): playback
/// starts at once, and a seek fetches only what it needs, instead of the whole file first. Only for a presigned storage
/// address, which carries no credential and answers ranges; content the installation serves itself (a sealed envelope,
/// the at-rest token door) is read whole instead, because it is opened or decrypted as a whole.
/// </summary>
public sealed class RangedMediaBytes : IMediaBytes
{
    private readonly Uri _address;

    private RangedMediaBytes(Uri address, long length) => (_address, Length) = (address, length);

    public long Length { get; }

    /// <summary>
    /// The address read by ranges, or null where it cannot be: our own installation's address, or storage that does not
    /// answer a range with its total length. Asked with one byte, so deciding costs nothing.
    /// </summary>
    public static async Task<RangedMediaBytes?> OpenAsync(string downloadUrl, CancellationToken cancellationToken = default)
    {
        var address = ApiCore.ResolveContentUrl(downloadUrl);
        if (ApiCore.IsOwnInstallation(address))
        {
            return null;
        }

        using var probe = await SendAsync(address, 0, 1, cancellationToken);
        return probe.StatusCode == HttpStatusCode.PartialContent && probe.Content.Headers.ContentRange?.Length is { } length
            && !Envelopes.IsEnvelope(probe.Content.Headers.ContentType?.MediaType ?? string.Empty)
            ? new RangedMediaBytes(address, length)
            : null;
    }

    public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(_address, offset, length, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    // The anonymous client: a presigned address must never receive our bearer (ApiCore.Anonymous).
    private static Task<HttpResponseMessage> SendAsync(Uri address, long offset, int length, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, address) { Headers = { Range = new RangeHeaderValue(offset, offset + length - 1) } };
        return ApiCore.Anonymous.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }
}
