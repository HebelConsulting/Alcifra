using SimplArchive.Presentation;

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

    /// <summary>
    /// An Android package name this entry also fills (ADR 0973): an app with no verified link to its site is matched by
    /// it. The one metadata value a user sets deliberately, as an ordinary field of the entry.
    /// </summary>
    public const string App = "App";
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

    /// <summary>
    /// The site this entry belongs to (ADR 0973): the URL slot's host, lowercased and without a leading
    /// <c>www.</c>, which is what a browser's web domain or an app's verified link is compared against. Null where the
    /// entry has no URL, or one that names no host.
    /// </summary>
    /// <remarks>
    /// The scheme is optional, as users type it: <c>example.com/login</c> is read as <c>https://example.com/login</c>.
    /// This is index metadata derived from the content the client already holds in the clear when it files a version —
    /// it is NOT a second copy of the URL, and the server never sees the content.
    /// </remarks>
    public string? Site
    {
        get
        {
            // The URL SLOT, not one spelling of it: an entry saved with "Website" or "Link" names the same field
            // (PasswordEntryFieldOrder), and a site that ignored those would silently never match.
            var url = Fields
                .FirstOrDefault(f => PasswordEntryFieldOrder.SlotOf(f.Name) == PasswordFieldSlot.Url)?.Value?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            {
                if (!Uri.TryCreate($"https://{url}", UriKind.Absolute, out uri) || uri.Host.Length == 0)
                {
                    return null;
                }
            }

            var host = uri.Host.ToLowerInvariant();
            return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
        }
    }

    /// <summary>The Android package name the entry also fills (ADR 0973), or null.</summary>
    public string? App => Value(PasswordEntryFieldNames.App);

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
