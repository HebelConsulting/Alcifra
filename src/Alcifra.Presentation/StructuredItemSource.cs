using System.Text;

namespace Alcifra.Presentation;

/// <summary>
/// The stored vCard or iCalendar text as a contact's or an appointment's card shows it in its collapsed source section
/// (#2274): verbatim, line for line, except a long <c>PHOTO</c>, whose inline image (often tens of kilobytes of base64)
/// is shown as one line naming its size. Copy takes the stored text itself, never this. Both clients show it through
/// here, so the shortening is the same on the desktop and the web.
/// </summary>
public static class StructuredItemSource
{
    // A PHOTO this long or longer is shortened: a URI fits easily below it, an inline image does not.
    public const int ShortenFrom = 256;

    /// <summary>The text to show: <paramref name="stored"/> with each long <c>PHOTO</c> property, folded lines included,
    /// replaced by <c>PHOTO;… (N bytes)</c>, N the property's size as stored.</summary>
    public static string Shown(string stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var lines = stored.ReplaceLineEndings("\n").Split('\n');
        var shown = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsPhoto(lines[i]))
            {
                shown.Add(lines[i]);
                continue;
            }

            // A property continues on every following line that starts with a space or a tab (RFC 6350 §3.2).
            var end = i;
            while (end + 1 < lines.Length && lines[end + 1] is [' ' or '\t', ..])
            {
                end++;
            }

            var size = 0;
            for (var j = i; j <= end; j++)
            {
                size += Encoding.UTF8.GetByteCount(lines[j]) + (j < end ? 2 : 0);   // each fold is a CRLF in the file
            }

            if (size < ShortenFrom)
            {
                shown.AddRange(lines[i..(end + 1)]);
            }
            else
            {
                shown.Add($"PHOTO;… ({size} bytes)");
            }

            i = end;
        }

        return string.Join('\n', shown);
    }

    // PHOTO, before its parameters or its value: "PHOTO;ENCODING=b:…", "PHOTO:data:…", or with a group, "item1.PHOTO:…".
    private static bool IsPhoto(string line)
    {
        if (line is [' ' or '\t', ..])
        {
            return false;
        }

        var nameEnd = line.IndexOfAny([';', ':']);
        var name = nameEnd < 0 ? line : line[..nameEnd];
        var dot = name.LastIndexOf('.');
        return (dot < 0 ? name : name[(dot + 1)..]).Equals("PHOTO", StringComparison.OrdinalIgnoreCase);
    }
}
