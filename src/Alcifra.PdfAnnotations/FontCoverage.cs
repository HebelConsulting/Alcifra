using System.Buffers.Binary;

namespace Alcifra.PdfAnnotations;

/// <summary>
/// Which code points a font can actually draw, read from its own <c>cmap</c> table (#2451). The writer must know
/// this before it can split a caption between the two embedded faces: PDFsharp 6.2 exposes no public glyph lookup,
/// and guessing from the string would repeat the very defect the split exists to fix. The bytes are the package's
/// OWN resources — trusted, never caller-supplied — so a plain range reader is enough; glyph ids are only resolved
/// far enough to tell "a glyph" from ".notdef".
/// </summary>
internal sealed class FontCoverage
{
    private readonly List<(int Start, int End)> _ranges;

    private FontCoverage(List<(int Start, int End)> ranges) => _ranges = ranges;

    /// <summary>Whether the font has a real glyph for <paramref name="codePoint"/>.</summary>
    public bool Covers(int codePoint) => _ranges.Any(range => codePoint >= range.Start && codePoint <= range.End);

    public static FontCoverage Read(byte[] ttf)
    {
        var cmap = FindTable(ttf, "cmap")
            ?? throw new PdfEmbeddingException("The embedded font carries no character map; the flattened export cannot draw text.");

        // The best subtable wins: a full-repertoire mapping (format 12) over the BMP one (format 4), Windows/Unicode
        // encodings over any other platform's.
        int bestScore = -1;
        List<(int Start, int End)>? best = null;
        var tables = BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(cmap + 2));
        for (var i = 0; i < tables; i++)
        {
            var record = cmap + 4 + (i * 8);
            var platform = BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(record));
            var encoding = BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(record + 2));
            var subtable = cmap + (int)BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(record + 4));
            var format = BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(subtable));

            // Formats 0/6 cover the BMP too; 13 is a many-to-one giant like 12. Anything else is not worth reading.
            var score = format switch
            {
                12 or 13 => (platform is 3 or 0 ? 60 : 40) + (encoding is 10 or 4 or 6 ? 2 : 0),
                4 => (platform is 3 or 0 ? 30 : 20) + (encoding is 1 or 3 ? 2 : 0),
                6 => 10,
                0 => 5,
                _ => -1,
            };
            if (score <= bestScore)
            {
                continue;
            }

            var ranges = format switch
            {
                12 or 13 => ReadFormat12(ttf, subtable),
                4 => ReadFormat4(ttf, subtable),
                6 => ReadFormat6(ttf, subtable),
                _ => ReadFormat0(ttf, subtable),
            };
            if (ranges is not null)
            {
                bestScore = score;
                best = ranges;
            }
        }

        return best is null
            ? throw new PdfEmbeddingException("The embedded font's character map is unreadable; the flattened export cannot draw text.")
            : new FontCoverage(best);
    }

    // The table directory: numTables at 4, then 16-byte records (tag, checksum, offset, length).
    private static int? FindTable(byte[] ttf, string tag)
    {
        if (ttf.Length < 12)
        {
            return null;
        }

        var tables = BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(4));
        for (var i = 0; i < tables; i++)
        {
            var record = 12 + (i * 16);
            if (record + 16 > ttf.Length)
            {
                return null;
            }

            if (ttf.AsSpan(record, 4).SequenceEqual(tag.Select(c => (byte)c).ToArray()))
            {
                return (int)BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(record + 8));
            }
        }

        return null;
    }

    // Format 4: endCodes, then startCodes, then idDelta, then idRangeOffset — coverage means "maps to a glyph other
    // than .notdef", so the idRangeOffset path is walked rather than assumed.
    private static List<(int Start, int End)>? ReadFormat4(byte[] ttf, int subtable)
    {
        if (subtable + 14 > ttf.Length)
        {
            return null;
        }

        var segCount = BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(subtable + 6)) / 2;
        var ends = subtable + 14;
        var starts = ends + (segCount * 2) + 2;   // + the reservedPad
        var deltas = starts + (segCount * 2);
        var offsets = deltas + (segCount * 2);
        if (offsets + (segCount * 2) > ttf.Length)
        {
            return null;
        }

        var ranges = new List<(int, int)>();
        for (var i = 0; i < segCount; i++)
        {
            int start = BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(starts + (i * 2)));
            int end = BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(ends + (i * 2)));
            int delta = BinaryPrimitives.ReadInt16BigEndian(ttf.AsSpan(deltas + (i * 2)));
            int rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(offsets + (i * 2)));
            if (end == 0xFFFF && start == 0xFFFF)
            {
                continue;   // the mandatory terminator
            }

            var covered = new List<(int Start, int End)>();
            for (var code = start; code <= end && code <= 0xFFFF; code++)
            {
                var glyph = rangeOffset == 0
                    ? (code + delta) & 0xFFFF
                    : GlyphThroughRangeOffset(ttf, offsets + (i * 2), rangeOffset, code - start, delta);
                if (glyph != 0)
                {
                    if (covered.Count > 0 && covered[^1].End == code - 1)
                    {
                        covered[^1] = (covered[^1].Start, code);
                    }
                    else
                    {
                        covered.Add((code, code));
                    }
                }
            }

            ranges.AddRange(covered);
        }

        return ranges;
    }

    private static int GlyphThroughRangeOffset(byte[] ttf, int rangeOffsetPosition, int rangeOffset, int index, int delta)
    {
        var position = rangeOffsetPosition + rangeOffset + (index * 2);
        if (position + 2 > ttf.Length)
        {
            return 0;
        }

        var glyph = BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(position));
        return glyph == 0 ? 0 : (glyph + delta) & 0xFFFF;
    }

    // Format 12/13: groups of (start, end, startGlyph) — the full Unicode repertoire, non-BMP included.
    private static List<(int Start, int End)>? ReadFormat12(byte[] ttf, int subtable)
    {
        if (subtable + 16 > ttf.Length)
        {
            return null;
        }

        var groups = (int)BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(subtable + 12));
        var ranges = new List<(int, int)>(groups);
        for (var i = 0; i < groups; i++)
        {
            var group = subtable + 16 + (i * 12);
            if (group + 12 > ttf.Length)
            {
                return ranges;
            }

            var start = (int)BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(group));
            var end = (int)BinaryPrimitives.ReadUInt32BigEndian(ttf.AsSpan(group + 4));
            ranges.Add((start, end));
        }

        return ranges;
    }

    // Format 6: a trimmed array of glyph ids from firstCode.
    private static List<(int Start, int End)>? ReadFormat6(byte[] ttf, int subtable)
    {
        if (subtable + 10 > ttf.Length)
        {
            return null;
        }

        int first = BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(subtable + 6));
        int count = BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(subtable + 8));
        var ranges = new List<(int, int)>();
        for (var i = 0; i < count; i++)
        {
            var glyphPosition = subtable + 10 + (i * 2);
            if (glyphPosition + 2 > ttf.Length)
            {
                break;
            }

            if (BinaryPrimitives.ReadUInt16BigEndian(ttf.AsSpan(glyphPosition)) != 0)
            {
                ranges.Add((first + i, first + i));
            }
        }

        return ranges;
    }

    // Format 0: the legacy 256-byte map.
    private static List<(int Start, int End)>? ReadFormat0(byte[] ttf, int subtable)
    {
        if (subtable + 262 > ttf.Length)
        {
            return null;
        }

        var ranges = new List<(int Start, int End)>();
        for (var code = 0; code < 256; code++)
        {
            if (ttf[subtable + 6 + code] != 0)
            {
                if (ranges.Count > 0 && ranges[^1].End == code - 1)
                {
                    ranges[^1] = (ranges[^1].Start, code);
                }
                else
                {
                    ranges.Add((code, code));
                }
            }
        }

        return ranges;
    }
}
