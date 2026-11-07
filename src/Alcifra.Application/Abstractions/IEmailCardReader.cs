namespace Alcifra.Application.Abstractions;

/// <summary>A mail as the preview's card shows it (#2215): its envelope, its body as sanitised HTML and as text, and the inline
/// images the HTML refers to.</summary>
/// <param name="Html">The body's HTML, sanitised: no scripts, no remote images; an inline image's <c>src</c> is
/// <c>cid:</c> followed by its index in <paramref name="Inline"/>. Null where the mail has no HTML part.</param>
/// <param name="Text">The body as text: the mail's own text part, else the HTML turned into text.</param>
public sealed record EmailCard(
    string Subject, string From, string To, string? Cc, DateTimeOffset? Date, string? Html, string Text, IReadOnlyList<EmailInlinePart> Inline);

/// <summary>An image the HTML body shows from inside the mail (a <c>cid:</c> reference), decoded.</summary>
public sealed record EmailInlinePart(string ContentType, byte[] Bytes);

/// <summary>Reads an .eml or .msg into its card; null when the bytes are not that format.</summary>
public interface IEmailCardReader
{
    EmailCard? Read(byte[] bytes, string extension);
}
