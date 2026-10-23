namespace SimplArchive.ApiClient.PasswordStore;

/// <summary>One field of an entry besides its password: a name, a value, and whether it is shown like a password.</summary>
public sealed record PasswordEntryField(string Name, string Value, bool IsSecret = false);

/// <summary>The names of the fields every client knows (ADR 0964). Stored in English, as mask and field names are; a
/// client translates them for display.</summary>
public static class PasswordEntryFieldNames
{
    public const string UserName = "User name";

    public const string Url = "URL";

    public const string Notes = "Notes";
}

/// <summary>
/// What a password entry holds once opened (ADR 0964): its password, and every other field in order. The NAME is not
/// here: it is the document's, in the clear.
/// </summary>
public sealed record PasswordEntryContent(string Password, IReadOnlyList<PasswordEntryField> Fields)
{
    public string? UserName => Value(PasswordEntryFieldNames.UserName);

    public string? Url => Value(PasswordEntryFieldNames.Url);

    public string? Notes => Value(PasswordEntryFieldNames.Notes);

    /// <summary>The first field of that name, compared without case; null when there is none.</summary>
    public string? Value(string fieldName) =>
        Fields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>A new entry from the usual fields; empty ones are left out.</summary>
    public static PasswordEntryContent Of(string password, string? userName = null, string? url = null, string? notes = null) =>
        new(password,
        [
            .. new[] { (PasswordEntryFieldNames.UserName, userName), (PasswordEntryFieldNames.Url, url), (PasswordEntryFieldNames.Notes, notes) }
                .Where(f => !string.IsNullOrEmpty(f.Item2))
                .Select(f => new PasswordEntryField(f.Item1, f.Item2!)),
        ]);
}
