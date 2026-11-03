using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.ApiClient.PasswordStore;

/// <summary>One row of an opened entry: its label, its value, and how it is shown (core <see cref="SecretFields"/>).</summary>
/// <param name="FieldName">The stored name (the password's is "Password"): what the core's order places it by.</param>
/// <param name="IsPassword">The entry's password.</param>
/// <param name="IsMultiline">Notes, or any value of several lines: shown on several lines.</param>
public sealed record PasswordEntryRow(string Label, string Value, SecretFieldPresentation Presentation, bool IsPassword = false, bool IsMultiline = false,
    string FieldName = "")
{
    public bool IsSecret => Presentation.IsSecret;

    /// <summary>Where the row opens in the browser, or null (core #1949): the URL's row, by <see cref="WebLinks.Typed"/>.</summary>
    public Uri? Link { get; init; }

    /// <summary>The verification code's row (ADR 1013): its value is the setup link, which is never shown; a client shows
    /// the current code from it (<see cref="SecretField.CurrentCode"/>).</summary>
    public bool IsVerificationCode => PasswordEntryFieldOrder.SlotOf(FieldName) == PasswordFieldSlot.VerificationCode;

    /// <summary>The passkey's row (ADR 1033): its value is already the account and site it signs in to, shown plainly; the
    /// key behind it is never handed to a row.</summary>
    public bool IsPasskey => PasswordEntryFieldOrder.SlotOf(FieldName) == PasswordFieldSlot.Passkey;
}

/// <summary>
/// An opened entry as both native clients show it (Mobile ADR 0037; both native clients since ADR 0968), in the core's order (<see cref="PasswordEntryFieldOrder"/>, core ADR
/// 0965): URL, user name, password, PIN, verification code, passkey, then every other field in the entry's own order; what it does not have is
/// skipped. The password, the PIN and fields marked secret are hidden, the rest plain. The well-known names are labelled in the
/// user's language, every other field by its own name.
/// </summary>
public static class PasswordEntryRows
{
    private const string PasswordName = "Password";

    public static IReadOnlyList<PasswordEntryRow> Of(PasswordEntryContent content) => PasswordEntryFieldOrder.Arrange(
        [
            // No password row where the entry has none (ADR 1044): it may hold only a code or a passkey.
            .. content.HasPassword
                ? [new PasswordEntryRow(Strings.Get("PwPassword"), content.Password, SecretFields.For(isPassword: true, markedSecret: false), IsPassword: true, FieldName: PasswordName)]
                : Array.Empty<PasswordEntryRow>(),
            .. content.Fields.Select(Row),
        ],
        row => row.FieldName);

    /// <summary>The label of a field: the user's language for the well-known names, else its own name.</summary>
    public static string Label(string fieldName) => PasswordEntryFieldOrder.SlotOf(fieldName) switch
    {
        PasswordFieldSlot.Url => Strings.Get("PwUrl"),
        PasswordFieldSlot.UserName => Strings.Get("PwUserName"),
        PasswordFieldSlot.Pin => Strings.Get("MobKeyPin"),
        PasswordFieldSlot.VerificationCode => Strings.Get("PwVerificationCode"),
        PasswordFieldSlot.Passkey => Strings.Get("PwPasskey"),
        _ when IsNotes(fieldName) => Strings.Get("PwNotes"),
        _ => fieldName,
    };

    // A field named like the password but stored as an ordinary one ("Password: …" in the body) is shown under its own name
    // after the real one, never in its place.
    private static PasswordEntryRow Row(PasswordEntryField field)
    {
        // A passkey is shown as the account and site it signs in to, plainly, and its key never reaches a row: there is
        // nothing to reveal or copy (ADR 1033). One that does not read says so.
        if (PasswordEntryFieldOrder.SlotOf(field.Name) == PasswordFieldSlot.Passkey)
        {
            return new(Label(field.Name), Passkey.TryParse(field.Value)?.ToString() ?? Strings.Get("PwPasskeyUnreadable"),
                SecretFields.For(isPassword: false, markedSecret: false), FieldName: field.Name);
        }

        var presentation = SecretFields.ForField(field.Name, field.IsSecret);
        return new(Label(field.Name), field.Value, presentation,
            IsMultiline: IsNotes(field.Name) || field.Value.Contains('\n'), FieldName: field.Name)
        {
            // The URL opens in the browser (core #1949); a URL marked secret stays hidden and is no link.
            Link = PasswordEntryFieldOrder.SlotOf(field.Name) == PasswordFieldSlot.Url && !presentation.IsSecret ? WebLinks.Typed(field.Value) : null,
        };
    }

    private static bool IsNotes(string name) => string.Equals(name.Trim(), PasswordEntryFieldNames.Notes, StringComparison.OrdinalIgnoreCase);
}
