namespace Alcifra.Presentation;

/// <summary>The fixed places at the top of an opened password entry and its form (owner decision 2026-10-23).</summary>
public enum PasswordFieldSlot
{
    Url,
    UserName,
    Password,
    Pin,

    /// <summary>A verification code's setup (ADR 1013): an <c>otpauth://totp/…</c> link, shown as the CURRENT code,
    /// never as itself.</summary>
    VerificationCode,

    /// <summary>Every other field, in the entry's own order.</summary>
    Other,
}

/// <summary>
/// The ORDER of a password entry's fields, which every client answers identically (owner decision 2026-10-23, ADR 0964):
/// 1. URL, 2. user name, 3. password, 4. PIN, 5. verification code (ADR 1013), then every other field in the entry's own
/// order. A missing one is skipped when showing; a form always offers the first three (<see cref="AlwaysOffered"/>) and
/// suggests the PIN, then the verification code, as the first fields to add (<see cref="Suggested"/>); a PIN, once there,
/// is always shown like a password (<see cref="SecretFields.ForField"/>). A field is placed by its NAME, without case,
/// in the usual spellings; only the first field of a slot takes it, a second one is an ordinary field. Arithmetic, not
/// drawing: each client renders the rows it is handed.
/// </summary>
public static class PasswordEntryFieldOrder
{
    /// <summary>The order of the slots, first to last.</summary>
    public static readonly IReadOnlyList<PasswordFieldSlot> Order = [PasswordFieldSlot.Url, PasswordFieldSlot.UserName, PasswordFieldSlot.Password, PasswordFieldSlot.Pin, PasswordFieldSlot.VerificationCode];

    /// <summary>The slots a create or edit form always offers, in their order (owner, 2026-10-23).</summary>
    public static readonly IReadOnlyList<PasswordFieldSlot> AlwaysOffered = [PasswordFieldSlot.Url, PasswordFieldSlot.UserName, PasswordFieldSlot.Password];

    /// <summary>The slots a form offers to ADD, before a field of the user's own naming, where the entry has none yet: the
    /// PIN and the verification code (ADR 1013) appear only once added or the entry has one.</summary>
    public static readonly IReadOnlyList<PasswordFieldSlot> Suggested = [PasswordFieldSlot.Pin, PasswordFieldSlot.VerificationCode];

    /// <summary>Whether a field in this slot is shown like a password whatever its author marked: the password, the
    /// PIN and the verification code's setup (whose secret is never shown; a client shows the current code instead).</summary>
    public static bool IsSecret(PasswordFieldSlot slot) =>
        slot is PasswordFieldSlot.Password or PasswordFieldSlot.Pin or PasswordFieldSlot.VerificationCode;

    private static readonly Dictionary<string, PasswordFieldSlot> Spellings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["URL"] = PasswordFieldSlot.Url,
        ["Website"] = PasswordFieldSlot.Url,
        ["Web site"] = PasswordFieldSlot.Url,
        ["Link"] = PasswordFieldSlot.Url,
        ["User name"] = PasswordFieldSlot.UserName,
        ["Username"] = PasswordFieldSlot.UserName,
        ["Login"] = PasswordFieldSlot.UserName,
        ["E-mail"] = PasswordFieldSlot.UserName,
        ["Email"] = PasswordFieldSlot.UserName,
        ["Password"] = PasswordFieldSlot.Password,
        ["PIN"] = PasswordFieldSlot.Pin,
        ["Verification code"] = PasswordFieldSlot.VerificationCode,
        ["One-time code"] = PasswordFieldSlot.VerificationCode,
        ["TOTP"] = PasswordFieldSlot.VerificationCode,
        ["2FA"] = PasswordFieldSlot.VerificationCode,
        ["OTP"] = PasswordFieldSlot.VerificationCode,
    };

    /// <summary>The slot a field of this name belongs in; <see cref="PasswordFieldSlot.Other"/> for any other name.</summary>
    public static PasswordFieldSlot SlotOf(string fieldName) =>
        Spellings.TryGetValue(fieldName.Trim(), out var slot) ? slot : PasswordFieldSlot.Other;

    /// <summary>
    /// The fields in the shown order: the first field of each slot in slot order, then every other field (a second URL
    /// too) in its own order. <paramref name="nameOf"/> gives a field's name; the entry's password is handed in as a field
    /// named "Password".
    /// </summary>
    public static IReadOnlyList<T> Arrange<T>(IEnumerable<T> fields, Func<T, string> nameOf)
    {
        var list = fields.ToList();
        var first = new Dictionary<PasswordFieldSlot, int>();
        for (var i = 0; i < list.Count; i++)
        {
            var slot = SlotOf(nameOf(list[i]));
            if (slot != PasswordFieldSlot.Other)
            {
                first.TryAdd(slot, i);
            }
        }

        var placed = first.Values.ToHashSet();
        return
        [
            .. Order.Where(first.ContainsKey).Select(s => list[first[s]]),
            .. list.Where((_, i) => !placed.Contains(i)),
        ];
    }
}
