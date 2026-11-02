using System.Globalization;
using System.Text;

namespace Alcifra.ApiClient.Inspection;

/// <summary>One element of a DER/BER structure: where it starts, its header and content length, its tag and value.</summary>
/// <param name="Offset">Where the element starts in the file.</param>
/// <param name="HeaderLength">Its tag and length octets.</param>
/// <param name="Length">Its content's length; -1 for an indefinite one (BER).</param>
/// <param name="Value">A primitive's value as a person reads it (an OID, an integer, a short string); null otherwise.</param>
public sealed record Asn1Node(int Offset, int Depth, int HeaderLength, int Length, bool Constructed, string Tag, string? Value);

/// <summary>
/// The full ASN.1 tree of a CMS structure (#2007's <c>--extra-verbose</c>): every element with its offset, header and
/// content length, in the layout ASN.1 dumps commonly use. A plain reader of tag and length octets, so it shows what is
/// there even where a CMS decoder would refuse it.
/// </summary>
public static class Asn1Tree
{
    public static IReadOnlyList<Asn1Node> Of(byte[] der)
    {
        var nodes = new List<Asn1Node>();
        Walk(der, 0, der.Length, 0, nodes);
        return nodes;
    }

    /// <summary>One line per node, indented by depth.</summary>
    public static string Lines(IReadOnlyList<Asn1Node> nodes) => string.Join('\n', nodes.Select(n =>
        string.Create(CultureInfo.InvariantCulture,
            $"{n.Offset,5}:d={n.Depth,-2} hl={n.HeaderLength} l={(n.Length < 0 ? "inf" : n.Length.ToString(CultureInfo.InvariantCulture)),4} {(n.Constructed ? "cons" : "prim")}: {new string(' ', n.Depth * 2)}{n.Tag}{(n.Value is null ? string.Empty : $" :{n.Value}")}")));

    // Returns where the walk stopped; an end-of-contents marker ends an indefinite-length parent.
    private static int Walk(byte[] der, int at, int end, int depth, List<Asn1Node> nodes)
    {
        while (at < end)
        {
            var start = at;
            if (at + 1 < end && der[at] == 0 && der[at + 1] == 0)
            {
                return at + 2;   // end of contents
            }

            var tagByte = der[at++];
            var tagNumber = tagByte & 0x1F;
            if (tagNumber == 0x1F)
            {
                tagNumber = 0;
                while (at < end && (der[at] & 0x80) != 0)
                {
                    tagNumber = (tagNumber << 7) | (der[at++] & 0x7F);
                }

                tagNumber = at < end ? (tagNumber << 7) | der[at++] : tagNumber;
            }

            if (at >= end)
            {
                return end;
            }

            var length = (int)der[at++];
            if (length == 0x80)
            {
                length = -1;
            }
            else if (length > 0x80)
            {
                var octets = length & 0x7F;
                length = 0;
                for (var i = 0; i < octets && at < end; i++)
                {
                    length = (length << 8) | der[at++];
                }
            }

            var constructed = (tagByte & 0x20) != 0;
            var headerLength = at - start;
            var contentEnd = length < 0 ? end : Math.Min(end, at + length);
            nodes.Add(new Asn1Node(start, depth, headerLength, length, constructed, TagName(tagByte, tagNumber),
                constructed ? null : Value(tagByte, der.AsSpan(at, contentEnd - at))));
            at = constructed ? Walk(der, at, contentEnd, depth + 1, nodes) : contentEnd;
            if (length >= 0)
            {
                at = contentEnd;
            }
        }

        return at;
    }

    private static string TagName(byte tag, int number) => (tag & 0xC0) switch
    {
        0x80 => $"cont [ {number} ]",
        0x40 => $"appl [ {number} ]",
        0xC0 => $"priv [ {number} ]",
        _ => number switch
        {
            1 => "BOOLEAN",
            2 => "INTEGER",
            3 => "BIT STRING",
            4 => "OCTET STRING",
            5 => "NULL",
            6 => "OBJECT",
            12 => "UTF8STRING",
            16 => "SEQUENCE",
            17 => "SET",
            19 => "PRINTABLESTRING",
            22 => "IA5STRING",
            23 => "UTCTIME",
            24 => "GENERALIZEDTIME",
            _ => $"universal [ {number} ]",
        },
    };

    private static string? Value(byte tag, ReadOnlySpan<byte> content) => (tag & 0xC0) != 0 ? null : (tag & 0x1F) switch
    {
        6 => Oid(content),
        2 => content.Length <= 20 ? Convert.ToHexString(content) : $"{content.Length} bytes",
        12 or 19 or 22 or 23 or 24 => Encoding.UTF8.GetString(content),
        4 or 3 => $"[{content.Length} bytes]",
        _ => null,
    };

    private static string Oid(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0)
        {
            return string.Empty;
        }

        var parts = new List<string> { (content[0] / 40).ToString(CultureInfo.InvariantCulture), (content[0] % 40).ToString(CultureInfo.InvariantCulture) };
        long value = 0;
        foreach (var b in content[1..])
        {
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                parts.Add(value.ToString(CultureInfo.InvariantCulture));
                value = 0;
            }
        }

        var dotted = string.Join('.', parts);
        var name = new System.Security.Cryptography.Oid(dotted).FriendlyName;
        return string.IsNullOrEmpty(name) ? dotted : $"{name} ({dotted})";
    }
}
