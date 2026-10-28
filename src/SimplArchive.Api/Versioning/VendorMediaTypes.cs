using System.Text.RegularExpressions;

namespace SimplArchive.Api.Versioning;

/// <summary>
/// The API's own media types (ADR 0986): <c>application/vnd.alcifra.v{n}+json|xml</c>. Part of the wire protocol, so it
/// is fixed whatever the build or the installation is called: a white-labelled server answers every client the same.
/// Answers always carry it.
/// </summary>
/// <remarks>
/// The name before the rename, <c>vnd.simplarchive</c>, is still ACCEPTED in requests, so a caller written against it
/// keeps working while it moves. Retirement trigger (ADR 0885): remove <see cref="LegacySubtype"/> and its accept paths
/// in the first core release after the one that introduced <c>vnd.alcifra</c> (core #1117).
/// </remarks>
public static class VendorMediaTypes
{
    /// <summary>The subtype every answer carries; the wire never follows the brand.</summary>
    public const string Subtype = $"vnd.{SimplArchive.Presentation.WireNames.Product}";

    /// <summary>The subtype before the rename, accepted in requests only, until its retirement (see remarks).</summary>
    public const string LegacySubtype = "vnd.simplarchive";

    /// <summary>The subtypes a request may name.</summary>
    public static IReadOnlyList<string> Accepted { get; } = [Subtype, LegacySubtype];

    /// <summary>
    /// An answer's content type in the CURRENT subtype: a formatter answers with the media type the caller named, so a
    /// request naming the one before the rename would otherwise be answered in it. Anything else is returned as is.
    /// </summary>
    public static string Current(string contentType) =>
        contentType.StartsWith($"application/{LegacySubtype}.", StringComparison.OrdinalIgnoreCase)
            ? $"application/{Subtype}.{contentType[$"application/{LegacySubtype}.".Length..]}"
            : contentType;

    /// <summary>The media type of an answer.</summary>
    public static string Of(string version, string format) => $"application/{Subtype}.v{version}+{format}";

    /// <summary>One of our media types with its version, in any accepted subtype, regardless of the format suffix.</summary>
    public static readonly Regex Versioned = new(
        $@"application/(?:{string.Join('|', Accepted.Select(Regex.Escape))})\.v(?<version>[\d.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>One of our media types, named at all (a caller being explicit).</summary>
    public static readonly Regex Named = new(
        $@"application/(?:{string.Join('|', Accepted.Select(Regex.Escape))})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
