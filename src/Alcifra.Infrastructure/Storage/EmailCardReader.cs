using System.Text;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using MimeKit;
using Alcifra.Application.Abstractions;

namespace Alcifra.Infrastructure.Storage;

/// <summary>
/// A mail's card (#2215, ADR 1073): the envelope, the body sanitised once on the server, and its inline images. MimeKit reads
/// .eml and MSGReader .msg, as for the rendition (ADR 0230). The sanitiser keeps formatting and links and removes scripts,
/// event handlers, forms and frames; an image is kept only when it comes from inside the mail, so opening a card never
/// tells a sender it was read.
/// </summary>
public sealed class EmailCardReader : IEmailCardReader
{
    public EmailCard? Read(byte[] bytes, string extension)
    {
        try
        {
            return extension.Equals(".msg", StringComparison.OrdinalIgnoreCase) ? ReadMsg(bytes) : ReadEml(bytes);
        }
        catch (Exception)
        {
            // Bytes that are not the format they claim, or a format corrupt underneath (MSGReader's compound-file layer
            // throws its own types): no card, and the preview falls back to the rendition.
            return null;
        }
    }

    private static EmailCard ReadEml(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var message = MimeMessage.Load(stream);
        var inline = message.BodyParts.OfType<MimePart>()
            .Where(p => !string.IsNullOrEmpty(p.ContentId) && p.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Id: p.ContentId!, Part: new EmailInlinePart(p.ContentType.MimeType, Decode(p))))
            .ToList();
        return Card(message.Subject ?? string.Empty, message.From.ToString(), message.To.ToString(),
            message.Cc.Count > 0 ? message.Cc.ToString() : null, message.Date == DateTimeOffset.MinValue ? null : message.Date,
            message.HtmlBody, message.TextBody, inline);
    }

    private static EmailCard ReadMsg(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var message = new MsgReader.Outlook.Storage.Message(stream);
        string Recipients(MsgReader.Outlook.RecipientType type) => string.Join(", ", message.Recipients
            .Where(r => r.Type == type).Select(r => string.IsNullOrEmpty(r.DisplayName) ? r.Email : $"{r.DisplayName} <{r.Email}>"));
        var inline = message.Attachments.OfType<MsgReader.Outlook.Storage.Attachment>()
            .Where(a => a.IsInline && !string.IsNullOrEmpty(a.ContentId) && a.Data is { Length: > 0 }
                && (a.MimeType ?? string.Empty).StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            .Select(a => (Id: a.ContentId!, Part: new EmailInlinePart(a.MimeType!, a.Data)))
            .ToList();
        var cc = Recipients(MsgReader.Outlook.RecipientType.Cc);
        return Card(message.Subject ?? string.Empty,
            message.Sender is { } sender ? (string.IsNullOrEmpty(sender.DisplayName) ? sender.Email : $"{sender.DisplayName} <{sender.Email}>") : string.Empty,
            Recipients(MsgReader.Outlook.RecipientType.To), cc.Length > 0 ? cc : null, message.SentOn,
            message.BodyHtml, message.BodyText, inline);
    }

    private static EmailCard Card(string subject, string from, string to, string? cc, DateTimeOffset? date,
        string? html, string? text, List<(string Id, EmailInlinePart Part)> inline)
    {
        var parts = new List<EmailInlinePart>();
        string? safe = null;
        if (!string.IsNullOrWhiteSpace(html))
        {
            safe = Sanitizer(inline, parts).Sanitize(html);
        }

        var body = !string.IsNullOrWhiteSpace(text) ? text : safe is null ? string.Empty : AsText(safe);
        return new EmailCard(subject, from, to, cc, date, safe, body.Trim(), parts);
    }

    // One sanitiser per mail, because an inline image's address is rewritten to its index in THIS mail's parts.
    private static HtmlSanitizer Sanitizer(List<(string Id, EmailInlinePart Part)> inline, List<EmailInlinePart> parts)
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedSchemes.Add("cid");
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedAttributes.Remove("style");
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.FilterUrl += (_, e) =>
        {
            if (!e.Tag.NodeName.Equals("IMG", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // An image is shown only from inside the mail: a remote one would fetch from the sender on every look.
            var id = e.OriginalUrl.StartsWith("cid:", StringComparison.OrdinalIgnoreCase) ? e.OriginalUrl[4..].Trim('<', '>') : null;
            var index = id is null ? -1 : inline.FindIndex(i => i.Id.Trim('<', '>').Equals(id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                e.SanitizedUrl = null;
                return;
            }

            var at = parts.IndexOf(inline[index].Part);
            if (at < 0)
            {
                parts.Add(inline[index].Part);
                at = parts.Count - 1;
            }

            e.SanitizedUrl = $"cid:{at}";
        };
        return sanitizer;
    }

    // The HTML as text, for a client that cannot show HTML: a block ends a line, a line break is one.
    private static string AsText(string html)
    {
        var document = new HtmlParser().ParseDocument(html);
        foreach (var element in document.QuerySelectorAll("br"))
        {
            element.Replace(document.CreateTextNode("\n"));
        }

        foreach (var element in document.QuerySelectorAll("p, div, li, tr, h1, h2, h3, h4, h5, h6, blockquote"))
        {
            element.Append(document.CreateTextNode("\n"));
        }

        var text = new StringBuilder();
        foreach (var line in (document.Body?.TextContent ?? string.Empty).Split('\n'))
        {
            text.AppendLine(line.TrimEnd());
        }

        return text.ToString();
    }

    private static byte[] Decode(MimePart part)
    {
        using var buffer = new MemoryStream();
        part.Content?.DecodeTo(buffer);
        return buffer.ToArray();
    }
}
