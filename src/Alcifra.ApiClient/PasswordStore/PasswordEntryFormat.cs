using System.Text;
using MimeKit;

namespace Alcifra.ApiClient.PasswordStore;

/// <summary>
/// The stored form of a password entry (ADR 0964): a MIME entity, <c>multipart/mixed</c>, whose text body holds every
/// field but the password as name/value lines, and whose attachment <c>passwd.txt</c> holds the password alone. Sealed
/// whole as one CMS envelope, it is exactly what an S/MIME message's enveloped body carries, so a mail client opens it
/// with the same key (the IMAP slice wraps it in a message, unchanged).
/// </summary>
/// <remarks>
/// <para>The body's grammar, one field per line:</para>
/// <list type="bullet">
/// <item><c>Name: value</c>; a field shown like a password writes <c>Name [secret]: value</c>.</item>
/// <item>A value's further lines follow as CONTINUATION lines, each starting with one space (as mail headers fold), so a
/// multi-line note reads naturally in a mail client and round-trips exactly.</item>
/// <item>A name is never empty and holds no colon or line break; the reader is lenient and keeps a stray line as part of
/// the notes rather than dropping it.</item>
/// </list>
/// </remarks>
public static class PasswordEntryFormat
{
    /// <summary>The attachment holding the password alone.</summary>
    public const string PasswordFileName = "passwd.txt";

    /// <summary>The marker after a field's name that shows it like a password.</summary>
    public const string SecretMarker = " [secret]";

    /// <summary>The MIME entity, ready to seal.</summary>
    /// <exception cref="PasswordEntryEmptyException">The entry holds no password, verification code or passkey (ADR 1044).</exception>
    public static MimeEntity ToMime(PasswordEntryContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.HasAnyPart)
        {
            throw new PasswordEntryEmptyException();
        }

        var body = new StringBuilder();
        foreach (var field in content.Fields)
        {
            ValidateName(field.Name);
            var lines = field.Value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            body.Append(field.Name.Trim()).Append(field.IsSecret ? SecretMarker : string.Empty).Append(": ").Append(lines[0]).Append("\r\n");
            foreach (var line in lines.Skip(1))
            {
                body.Append(' ').Append(line).Append("\r\n");
            }
        }

        var text = new TextPart("plain") { ContentTransferEncoding = ContentEncoding.QuotedPrintable };
        text.SetText(Encoding.UTF8, body.ToString());

        // No password, no passwd.txt (ADR 1044): an entry may hold only a verification code or a passkey.
        if (!content.HasPassword)
        {
            return new Multipart("mixed") { text };
        }

        var password = new MimePart("text", "plain")
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(content.Password))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = PasswordFileName },
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = PasswordFileName,
        };
        password.ContentType.Charset = "utf-8";

        return new Multipart("mixed") { text, password };
    }

    /// <summary>The entity's bytes, headers included: what is sealed.</summary>
    public static byte[] ToBytes(PasswordEntryContent content)
    {
        using var stream = new MemoryStream();
        ToMime(content).WriteTo(stream);
        return stream.ToArray();
    }

    /// <summary>An opened entry, from the bytes its envelope held.</summary>
    /// <exception cref="PasswordEntryUnreadableException">The bytes are not a password entry.</exception>
    public static PasswordEntryContent Parse(byte[] mime)
    {
        try
        {
            return Parse(MimeEntity.Load(new MemoryStream(mime)));
        }
        catch (FormatException e)
        {
            throw new PasswordEntryUnreadableException("the opened content is not a MIME entity", e);
        }
    }

    /// <summary>An opened entry, from its MIME entity.</summary>
    public static PasswordEntryContent Parse(MimeEntity entity)
    {
        var parts = entity is Multipart multipart ? multipart.ToList() : [entity];
        // The password part is optional (ADR 1044); an entry written before with an empty one reads the same way.
        var passwordPart = parts.OfType<MimePart>().FirstOrDefault(p =>
            string.Equals(p.FileName, PasswordFileName, StringComparison.OrdinalIgnoreCase));
        var password = passwordPart is null ? string.Empty : Decode(passwordPart).TrimEnd('\r', '\n');

        var fields = new List<PasswordEntryField>();
        var text = parts.OfType<TextPart>().FirstOrDefault(p => !ReferenceEquals(p, passwordPart));
        if (text is not null)
        {
            fields.AddRange(Fields(text.Text ?? string.Empty));
        }
        else if (passwordPart is null)
        {
            throw new PasswordEntryUnreadableException($"it holds neither a {PasswordFileName} nor any field");
        }

        return new PasswordEntryContent(password, fields);
    }

    private static IEnumerable<PasswordEntryField> Fields(string body)
    {
        var fields = new List<(string Name, StringBuilder Value, bool Secret)>();
        foreach (var line in body.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n'))
        {
            if (line.StartsWith(' ') && fields.Count > 0)
            {
                fields[^1].Value.Append('\n').Append(line[1..]);
                continue;
            }

            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0 && !line.EndsWith(':'))
            {
                // A line nobody wrote with this grammar (an entry edited by hand): kept, in the notes.
                if (line.Length > 0)
                {
                    fields.Add((PasswordEntryFieldNames.Notes, new StringBuilder(line), false));
                }

                continue;
            }

            var name = colon > 0 ? line[..colon] : line[..^1];
            var value = colon > 0 ? line[(colon + 2)..] : string.Empty;
            var secret = name.EndsWith(SecretMarker, StringComparison.Ordinal);
            fields.Add((secret ? name[..^SecretMarker.Length] : name, new StringBuilder(value), secret));
        }

        return fields.Select(f => new PasswordEntryField(f.Name, f.Value.ToString(), f.Secret));
    }

    private static string Decode(MimePart part)
    {
        using var stream = new MemoryStream();
        part.Content?.DecodeTo(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains(':', StringComparison.Ordinal) || name.Contains('\n', StringComparison.Ordinal)
            || name.Contains('\r', StringComparison.Ordinal) || name.Trim().EndsWith(SecretMarker.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{name}' cannot be a field name: a name is not empty, holds no colon or line break, and does not end in {SecretMarker.Trim()}.", nameof(name));
        }
    }
}

/// <summary>The content opened, but it is not a password entry: never shown as an empty one.</summary>
public sealed class PasswordEntryUnreadableException(string reason, Exception? inner = null)
    : Exception($"This password entry could not be read: {reason}.", inner);

/// <summary>
/// An entry to seal holds no password, no verification code and no passkey (ADR 1044): there is nothing to sign in with.
/// Removing its last part is deleting the entry.
/// </summary>
public sealed class PasswordEntryEmptyException()
    : InvalidOperationException("An entry holds at least one of a password, a verification code and a passkey.");
