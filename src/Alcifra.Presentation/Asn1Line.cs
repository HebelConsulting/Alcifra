using System.Globalization;

namespace Alcifra.Presentation;

/// <summary>
/// One node of a sealed file's ASN.1 structure as a line (#2007, #2291): offset, depth, header and content length, whether it
/// is constructed, then the tag indented by depth. The CLI's <c>--extra-verbose</c>, the desktop and the web print the same
/// line, so a structure reads alike wherever it is looked at.
/// </summary>
public static class Asn1Line
{
    /// <param name="length">The content length; negative for an indefinite length.</param>
    public static string Of(int offset, int depth, int headerLength, int length, bool constructed, string tag, string? value) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{offset,5}:d={depth,-2} hl={headerLength} l={(length < 0 ? "inf" : length.ToString(CultureInfo.InvariantCulture)),4} {(constructed ? "cons" : "prim")}: {new string(' ', depth * 2)}{tag}{(value is null ? string.Empty : $" :{value}")}");
}
