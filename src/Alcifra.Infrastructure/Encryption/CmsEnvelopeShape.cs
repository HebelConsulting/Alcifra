namespace Alcifra.Infrastructure.Encryption;

/// <summary>
/// Whether bytes are SHAPED as a CMS enveloped message (ABI 1.20, ADR 0940): a <c>ContentInfo</c> whose content type is
/// <c>id-envelopedData</c> (RFC 5652) or <c>id-ct-authEnvelopedData</c> (RFC 5083), in DER or BER, and — when its length is
/// definite — exactly as long as the object. Read from the first <see cref="HeadLength"/> bytes and the total length, so a
/// door can ask without reading a large object twice.
/// </summary>
/// <remarks>
/// <b>A shape, not a proof.</b> It catches what goes wrong by mistake: a module or a client filing plaintext where sealed
/// content belongs, which on the strict tier would otherwise leave through a door meant for envelopes. It cannot stop
/// someone who already holds the plaintext from dressing it as an envelope — and that person needs no door to share it.
/// </remarks>
public static class CmsEnvelopeShape
{
    /// <summary>How many leading bytes the check reads: the outer header and the content-type OID, with room to spare.</summary>
    public const int HeadLength = 32;

    // The DER contents of the two OIDs: 1.2.840.113549.1.7.3 and 1.2.840.113549.1.9.16.1.23.
    private static readonly byte[] EnvelopedData = [0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x03];
    private static readonly byte[] AuthEnvelopedData = [0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x09, 0x10, 0x01, 0x17];

    /// <summary>True when <paramref name="head"/> (the object's first bytes) and <paramref name="length"/> (its whole size)
    /// describe a CMS enveloped message.</summary>
    public static bool IsEnvelope(ReadOnlySpan<byte> head, long length)
    {
        var at = 0;
        if (!Tag(head, ref at, 0x30) || !Length(head, ref at, out var outer))
        {
            return false;
        }

        if (outer is { } definite && at + definite != length)
        {
            return false;   // a definite outer length must cover the object exactly: no plaintext riding behind it
        }

        if (!Tag(head, ref at, 0x06) || !Length(head, ref at, out var oidLength) || oidLength is not { } oid
            || at + oid > head.Length)
        {
            return false;
        }

        var contentType = head.Slice(at, (int)oid);
        at += (int)oid;
        return (contentType.SequenceEqual(EnvelopedData) || contentType.SequenceEqual(AuthEnvelopedData))
            && Tag(head, ref at, 0xA0);
    }

    private static bool Tag(ReadOnlySpan<byte> head, ref int at, byte expected)
    {
        if (at >= head.Length || head[at] != expected)
        {
            return false;
        }

        at++;
        return true;
    }

    // A BER length: short form, long form up to four octets, or indefinite (null), which a streaming encoder may write.
    private static bool Length(ReadOnlySpan<byte> head, ref int at, out long? length)
    {
        length = null;
        if (at >= head.Length)
        {
            return false;
        }

        var first = head[at++];
        if (first < 0x80)
        {
            length = first;
            return true;
        }

        if (first == 0x80)
        {
            return true;
        }

        var octets = first & 0x7F;
        if (octets > 4 || at + octets > head.Length)
        {
            return false;
        }

        long value = 0;
        for (var i = 0; i < octets; i++)
        {
            value = (value << 8) | head[at++];
        }

        length = value;
        return true;
    }
}
