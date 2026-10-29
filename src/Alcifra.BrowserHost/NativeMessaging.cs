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
    /// <summary>The largest message the host accepts; autofill traffic is kilobytes, not megabytes.</summary>
    public const int MaxBytes = 1024 * 1024;

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

        var framed = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed, body.Length);
        body.CopyTo(framed, 4);
        await output.WriteAsync(framed, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
