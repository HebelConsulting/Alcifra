using System.Buffers.Binary;
using System.Text.Json;

namespace Alcifra.BrowserHost;

/// <summary>
/// Chrome's native-messaging framing: a 32-bit length in NATIVE byte order followed by a UTF-8 JSON body, in both
/// directions.
/// </summary>
/// <remarks>
/// <para>
/// <b>Native order, deliberately.</b> The protocol says native, and every platform this ships for is little-endian;
/// writing it down here is what stops somebody "fixing" it to network order, which would leave every message with a
/// length of 0x01000000 and a host that appears to hang.
/// </para>
/// <para>
/// <b>A length is a HOSTILE number until proven otherwise.</b> The host is launched by a browser on behalf of an
/// extension; a corrupted or malicious channel must not make it allocate gigabytes. Anything above
/// <see cref="MaxBytes"/> is refused as a protocol error, and the caller answers and stops.
/// </para>
/// </remarks>
public static class NativeMessaging
{
    /// <summary>The largest frame the host accepts, and the browser's own limit on one it reads. Autofill traffic is
    /// kilobytes; a bookmark file can exceed it, and travels in chunks.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The most a message may add up to across chunks; the same hostile-number rule as a frame's length.</summary>
    public const int MaxAssembledBytes = 64 * 1024 * 1024;

    // Before base64, so a chunk's frame stays well inside the limit.
    private const int ChunkBytes = 512 * 1024;

    /// <summary>Whether this frame is one chunk of a larger message.</summary>
    public static bool IsChunk(JsonDocument frame) => frame.RootElement.TryGetProperty("chunk", out _);

    /// <summary>Puts a request sent in chunks back together; one connection's worth of state.</summary>
    public sealed class Assembly
    {
        private readonly MemoryStream _parts = new();

        /// <summary>The whole message once its last chunk has arrived; null for a frame that is whole or one more chunk.</summary>
        /// <exception cref="InvalidDataException">The chunks exceed <see cref="MaxAssembledBytes"/> or are not base64.</exception>
        public JsonDocument? Add(JsonDocument frame)
        {
            if (!frame.RootElement.TryGetProperty("chunk", out var chunk))
            {
                return null;
            }

            if (!chunk.TryGetBytesFromBase64(out var bytes) || _parts.Length + bytes.Length > MaxAssembledBytes)
            {
                throw new InvalidDataException("A chunk is not base64, or the chunks exceed the limit.");
            }

            _parts.Write(bytes);
            if (!frame.RootElement.TryGetProperty("last", out var last) || !last.GetBoolean())
            {
                return null;
            }

            var whole = JsonDocument.Parse(_parts.GetBuffer().AsMemory(0, (int)_parts.Length));
            _parts.SetLength(0);
            return whole;
        }
    }

    /// <summary>Reads one message, or null at end of input (the browser closed the port).</summary>
    /// <exception cref="InvalidDataException">A length outside 1..<see cref="MaxBytes"/>.</exception>
    public static async Task<JsonDocument?> ReadAsync(Stream input, CancellationToken cancellationToken = default)
    {
        var header = new byte[4];
        var read = 0;
        while (read < 4)
        {
            var got = await input.ReadAsync(header.AsMemory(read, 4 - read), cancellationToken);
            if (got == 0)
            {
                return read == 0 ? null : throw new InvalidDataException("The message header ended mid-length.");
            }

            read += got;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxBytes)
        {
            throw new InvalidDataException($"A message length of {length} is outside 1..{MaxBytes}.");
        }

        var body = new byte[length];
        read = 0;
        while (read < length)
        {
            var got = await input.ReadAsync(body.AsMemory(read, length - read), cancellationToken);
            if (got == 0)
            {
                throw new InvalidDataException("The message ended before its declared length.");
            }

            read += got;
        }

        return JsonDocument.Parse(body);
    }

    /// <summary>Writes one message and flushes: the browser reads nothing until the bytes are actually there.</summary>
    public static async Task WriteAsync(Stream output, object message, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        if (body.Length > MaxBytes)
        {
            throw new InvalidDataException($"A {body.Length}-byte message exceeds the {MaxBytes}-byte limit.");
        }

        await WriteFrameAsync(output, body, cancellationToken);
    }

    /// <summary>Writes one answer, in as many frames as it takes; every chunk carries the request's id.</summary>
    public static async Task WriteChunkedAsync(Stream output, long id, object message, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        if (body.Length <= MaxBytes)
        {
            await WriteFrameAsync(output, body, cancellationToken);
            return;
        }

        if (body.Length > MaxAssembledBytes)
        {
            throw new InvalidDataException($"A {body.Length}-byte message exceeds the {MaxAssembledBytes}-byte limit.");
        }

        for (var at = 0; at < body.Length; at += ChunkBytes)
        {
            var size = Math.Min(ChunkBytes, body.Length - at);
            await WriteAsync(output, new { id, chunk = Convert.ToBase64String(body, at, size), last = at + size == body.Length }, cancellationToken);
        }
    }

    private static async Task WriteFrameAsync(Stream output, byte[] body, CancellationToken cancellationToken)
    {
        var framed = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed, body.Length);
        body.CopyTo(framed, 4);
        await output.WriteAsync(framed, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
