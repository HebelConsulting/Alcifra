using SimplArchive.Presentation;

namespace SimplArchive.ApiClient.PasswordStore;

/// <summary>A field of the form beyond the well-known ones: its name, its value, and whether it is shown like a password.</summary>
public sealed class PasswordFormField(string name = "", string value = "", bool isSecret = false)
{
    public string Name { get; set; } = name;

    public string Value { get; set; } = value;

    public bool IsSecret { get; set; } = isSecret;
}

/// <summary>
/// The create and edit form of an entry (Mobile ADR 0037; both native clients since ADR 0968): the name (on create only — a rename is the document's own door, and
/// one Save is one request, ADR 0794); then the places every form offers, in the core's order
/// (<see cref="PasswordEntryFieldOrder"/>, ADR 0965: URL, user name, password), the PIN where the entry has one or the
/// user adds it (always hidden); then every other field in the entry's own order, which an edit keeps; and the notes. Nothing of it leaves the device unsealed.
/// </summary>
public sealed class PasswordEntryForm
{
    /// <summary>The name a PIN is filed under when the form adds one.</summary>
    public const string PinFieldName = "PIN";

    /// <summary>The entry's name, in the clear: asked for on create.</summary>
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Pin { get; set; } = string.Empty;

    /// <summary>Whether the form shows the PIN: the entry has one, or the user added it (<see cref="AddPin"/>). The core
    /// suggests it as the first field to add, never offers it unasked (owner, 2026-10-23).</summary>
    public bool HasPin { get; private set; }

    /// <summary>What "Add field" offers first: the PIN while the form has none, then a field of the user's naming.</summary>
    public bool SuggestsPin => !HasPin && PasswordEntryFieldOrder.Suggested.Contains(PasswordFieldSlot.Pin);

    /// <summary>The user added the PIN.</summary>
    public void AddPin() => HasPin = true;

    public string Notes { get; set; } = string.Empty;

    /// <summary>Every other field, in its order; an edit keeps them, a new one may be added.</summary>
    public List<PasswordFormField> Fields { get; } = [];

    // The names the well-known boxes were stored under ("Website", "Login", …), kept so an edit renames nothing; and
    // whether the PIN was shown like a password (a new one is).
    private string _urlName = PasswordEntryFieldNames.Url;
    private string _userName = PasswordEntryFieldNames.UserName;
    private string _pinName = PinFieldName;
    private string _notesName = PasswordEntryFieldNames.Notes;

    /// <summary>A form filled from an opened entry: the first field of each place (by the core's spellings) and the first
    /// notes are the form's own boxes; every other field (a second URL too) stays a field of its own, so saving loses
    /// nothing.</summary>
    public static PasswordEntryForm From(PasswordEntryContent content)
    {
        var form = new PasswordEntryForm { Password = content.Password };
        var taken = new HashSet<PasswordFieldSlot>();
        var notes = false;
        foreach (var field in content.Fields)
        {
            var slot = PasswordEntryFieldOrder.SlotOf(field.Name);
            if (slot is PasswordFieldSlot.Url or PasswordFieldSlot.UserName or PasswordFieldSlot.Pin && taken.Add(slot))
            {
                switch (slot)
                {
                    case PasswordFieldSlot.Url:
                        (form.Url, form._urlName) = (field.Value, field.Name);
                        break;
                    case PasswordFieldSlot.UserName:
                        (form.UserName, form._userName) = (field.Value, field.Name);
                        break;
                    default:
                        (form.Pin, form._pinName, form.HasPin) = (field.Value, field.Name, true);
                        break;
                }
            }
            else if (!notes && !field.IsSecret && string.Equals(field.Name.Trim(), PasswordEntryFieldNames.Notes, StringComparison.OrdinalIgnoreCase))
            {
                (form.Notes, form._notesName, notes) = (field.Value, field.Name, true);
            }
            else
            {
                form.Fields.Add(new PasswordFormField(field.Name, field.Value, field.IsSecret));
            }
        }

        return form;
    }

    /// <summary>
    /// Why the form cannot be saved as it is, as a text key; null when it can. A field needs a name that the entry format can
    /// hold (no colon or line break, not ending in the secret marker); a field left wholly empty is dropped instead.
    /// </summary>
    public string? Problem(bool creating)
    {
        if (creating && string.IsNullOrWhiteSpace(Name))
        {
            return "ApiErrPasswordItemNameRequired";
        }

        return Fields.Where(f => !IsBlank(f)).Any(f => !IsValidName(f.Name)) ? "PwFieldNameInvalid" : null;
    }

    /// <summary>The content to seal: URL, user name, PIN, the further fields, then the notes; empty boxes are left out
    /// (the core's order decides how it is shown, whatever order it is stored in).</summary>
    public PasswordEntryContent ToContent() => new(Password,
    [
        .. Known(_urlName, Url),
        .. Known(_userName, UserName),
        .. Known(_pinName, Pin, secret: true),   // a PIN is always hidden like a password (ADR 0965)
        .. Fields.Where(f => !IsBlank(f)).Select(f => new PasswordEntryField(f.Name.Trim(), f.Value, f.IsSecret)),
        .. Known(_notesName, Notes),
    ]);

    /// <summary>Whether a field name is one the entry format can store (core <see cref="PasswordEntryFormat"/>).</summary>
    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.IndexOfAny([':', '\n', '\r']) < 0
        && !name.Trim().EndsWith(PasswordEntryFormat.SecretMarker.Trim(), StringComparison.Ordinal);

    private static IEnumerable<PasswordEntryField> Known(string name, string value, bool secret = false) =>
        string.IsNullOrEmpty(value) ? [] : [new PasswordEntryField(name, value, secret)];

    private static bool IsBlank(PasswordFormField field) => string.IsNullOrWhiteSpace(field.Name) && string.IsNullOrEmpty(field.Value);
}
