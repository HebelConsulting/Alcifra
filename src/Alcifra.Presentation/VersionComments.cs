namespace Alcifra.Presentation;

/// <summary>
/// A version's comment may be a SYSTEM COMMENT — stored as a token and translated for each reader (owner decisions
/// 2026-11-25, core #2471). The data holds the token; every surface that shows a comment resolves it through
/// <see cref="Resolve"/>, so a German reader sees the German sentence and an English reader the English one.
/// </summary>
/// <remarks>
/// <para>
/// A CLOSED LIST, deliberately — not a templating engine. Only the tokens named here ever resolve; anything else,
/// including a user's text that merely looks like a token, renders literally. That keeps old and future data safe and
/// the list auditable (a new system comment is one constant and one localization key).
/// </para>
/// <para>
/// The ESCAPE (owner's decision): a comment beginning with a DOUBLED opening brace is the user meaning the literal —
/// <c>{{documentversion.annotations-embedded}}</c> displays as <c>{documentversion.annotations-embedded}</c>, and only
/// the doubled brace is affected. Without it a person quoting a token back could never write it.
/// </para>
/// </remarks>
public static class VersionComments
{
    /// <summary>The comment an "Embed annotations" bake files: the marks drawn into the page as flat content.</summary>
    public const string AnnotationsEmbeddedToken = "{documentversion.annotations-embedded}";

    /// <summary>The comment a SIGNING closure files — the same bake, but the version closes a signing session (#2454's
    /// gate), which the comment distinguishes for the history's reader.</summary>
    public const string SignaturesCompletedToken = "{documentversion.signatures-completed}";

    // The closed list: token → the localization key whose sentence it shows.
    private static readonly IReadOnlyDictionary<string, string> Tokens = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AnnotationsEmbeddedToken] = "VersionCommentAnnotationsEmbedded",
        [SignaturesCompletedToken] = "VersionCommentSignaturesCompleted",
    };

    /// <summary>Whether the comment is one of the listed tokens (ignoring the escape).</summary>
    public static bool IsSystemComment(string? comment) =>
        comment is not null && Tokens.ContainsKey(comment.Trim());

    /// <summary>
    /// The comment as the READER should see it: a listed token becomes its sentence in the reader's language (through
    /// the caller's translate), a doubled opening brace loses one brace and stays literal, and plain user text is
    /// returned untouched.
    /// </summary>
    /// <param name="comment">The stored comment; null or blank returns as it is.</param>
    /// <param name="translate">The client's localization lookup (<c>Strings.Get</c>).</param>
    public static string? Resolve(string? comment, Func<string, string> translate)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return comment;
        }

        var trimmed = comment.Trim();
        if (trimmed.StartsWith("{{", StringComparison.Ordinal))
        {
            // The escape: the user meant the literal. One brace goes from each end the user doubled — {{token}} reads
            // as {token} — and nothing resolves.
            var escaped = trimmed[1..];
            if (escaped.EndsWith("}}", StringComparison.Ordinal))
            {
                escaped = escaped[..^1];
            }

            return comment.Length == trimmed.Length ? escaped : comment.Replace(trimmed, escaped, StringComparison.Ordinal);
        }

        return Tokens.TryGetValue(trimmed, out var key) ? translate(key) : comment;
    }
}
