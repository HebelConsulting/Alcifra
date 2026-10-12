using System.Text.RegularExpressions;

namespace SimplArchive.Presentation;

/// <summary>
/// How a document's chat reads (ADRs 0222, 0718; mentions #383), answered once for every client: which parts of a
/// body are mentions, which sentence an automatic entry says, and which replies belong under which message.
/// </summary>
/// <remarks>
/// The rules lived in the desktop's message view model until the phone needed them (mobile #15). They return KEYS and
/// PARTS rather than finished text, because the clients render differently: the web splices each mention in as its
/// own coloured element, the desktop and the phone flatten it into the line.
/// </remarks>
public static partial class ChatText
{
    /// <summary>One run of a body: plain text, or a mention of <see cref="MentionedUser"/> (null when the token's id
    /// is not a user id at all, which still reads as a mention of nobody).</summary>
    public sealed record Part(string Text, bool IsMention, Guid? MentionedUser = null);

    /// <summary>The body cut into text and mention parts. The token is <c>@[user-id]</c>, normatively defined by the
    /// server's Domain ChatMentions; parsed here rather than by referencing that assembly, which a client binary (and
    /// the web's WASM payload) must not carry.</summary>
    public static IReadOnlyList<Part> Split(string body)
    {
        var parts = new List<Part>();
        var at = 0;
        foreach (Match match in MentionToken().Matches(body))
        {
            if (match.Index > at)
            {
                parts.Add(new Part(body[at..match.Index], false));
            }

            parts.Add(new Part(match.Value, true, Guid.TryParse(match.Groups[1].Value, out var id) ? id : null));
            at = match.Index + match.Length;
        }

        if (at < body.Length)
        {
            parts.Add(new Part(body[at..], false));
        }

        return parts;
    }

    /// <summary>
    /// The body as one line, each mention as <c>@Name</c>. A token whose user is gone becomes
    /// <c>@<paramref name="unknownName"/></c>: the record that somebody was addressed outlives the account.
    /// </summary>
    public static string Flatten(string body, Func<Guid, string?> nameOf, string unknownName) =>
        string.Concat(Split(body).Select(p => p.IsMention
            ? $"@{(p.MentionedUser is { } id ? nameOf(id) : null) ?? unknownName}"
            : p.Text));

    /// <summary>
    /// An automatic entry's sentence as a template key and its arguments, or null for a typed message (kind 0) and any
    /// kind this client does not know, whose body is shown as it is. Version filed covers every version: version 1 is
    /// the document arriving, later ones are new versions of something already filed. A refused attachment's
    /// <c>{1}</c> is the FILE NAME the body carries, since that entry is about something that never became a version.
    /// </summary>
    public static (string Key, object?[] Args)? AutomaticEntry(int kind, string author, int? versionNumber, string body) => kind switch
    {
        1 => (versionNumber is null or <= 1 ? "ChatFiledNewDocument" : "ChatSavedNewVersion", [author]),
        2 => ("ChatActivatedVersion", [author, versionNumber]),
        3 => ("ChatAttachmentRefused", [author, body]),
        _ => null,
    };

    /// <summary>
    /// The thread one level deep, as the server enforces it: each top-level message with its replies, both in the
    /// order given. A reply whose parent is missing from the page is left out rather than promoted, since showing it
    /// at the top would answer a question nobody can see.
    /// </summary>
    public static IReadOnlyList<(T Message, IReadOnlyList<T> Replies)> Thread<T>(
        IReadOnlyList<T> messages, Func<T, Guid> id, Func<T, Guid?> parentId) =>
        [.. messages
            .Where(m => parentId(m) is null)
            .Select(top => (top, (IReadOnlyList<T>)[.. messages.Where(m => parentId(m) == id(top))]))];

    [GeneratedRegex(@"@\[([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\]")]
    private static partial Regex MentionToken();
}
