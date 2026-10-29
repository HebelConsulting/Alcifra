using Alcifra.ApiClient.PasswordStore;

namespace Alcifra.Sapass.Infrastructure;

/// <summary>
/// The text an entry is edited as: the password on the first line, then one <c>Name: value</c> per field — exactly
/// what <c>show</c> prints, so an entry can be read, edited and understood as one format (pass's shape).
/// </summary>
/// <remarks>
/// <b>Strict on the way in.</b> A line without a <c>Name: value</c> pair, or two fields of one name, is a mistake
/// to be told about with its line number — guessing what somebody meant is how an edit silently loses a field.
/// A field's SECRET flag is carried over by name from what was opened: the text does not say which fields are
/// secret, and an edited PIN must stay one.
/// </remarks>
internal static class EntryText
{
    /// <summary>The text the editor opens.</summary>
    public static string Format(PasswordEntryContent content) =>
        string.Join('\n', new[] { content.Password }.Concat(content.Fields.Select(f => $"{f.Name}: {f.Value}"))) + "\n";

    /// <summary>The content a saved edit describes.</summary>
    /// <param name="text">What the editor left behind.</param>
    /// <param name="previous">The fields as they were opened: only their <c>IsSecret</c> flags are carried over.</param>
    /// <exception cref="SapassException">A line that is not a field, a duplicated name, or no password.</exception>
    public static PasswordEntryContent Parse(string text, IReadOnlyList<PasswordEntryField> previous)
    {
        // \r\n from a Windows editor, \n elsewhere; the trailing newline is not a field.
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines[0].Length == 0)
        {
            throw new SapassException("The first line is the password and it is empty; nothing was saved.");
        }

        var secret = previous.ToDictionary(f => f.Name, f => f.IsSecret, StringComparer.Ordinal);
        var fields = new List<PasswordEntryField>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(": ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new SapassException($"Line {i + 1} is not a 'Name: value' field: {line}");
            }

            var name = line[..separator];
            if (fields.Any(f => string.Equals(f.Name, name, StringComparison.Ordinal)))
            {
                throw new SapassException($"Line {i + 1} repeats the field '{name}'; nothing was saved.");
            }

            fields.Add(new PasswordEntryField(name, line[(separator + 2)..], secret.GetValueOrDefault(name)));
        }

        return new PasswordEntryContent(lines[0], fields);
    }
}
