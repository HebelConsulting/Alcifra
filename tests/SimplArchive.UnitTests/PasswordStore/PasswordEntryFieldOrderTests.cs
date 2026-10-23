using SimplArchive.Presentation;

namespace SimplArchive.UnitTests.PasswordStore;

// Owner decision 2026-10-23: URL, user name, password, PIN, then the rest in the entry's own order — the same in every
// client, so it is shared arithmetic.
public sealed class PasswordEntryFieldOrderTests
{
    [Fact]
    public void The_first_four_come_first_in_their_order_and_the_rest_keep_theirs()
    {
        string[] stored = ["Notes", "Pin", "Security question", "Password", "Login", "Website", "Recovery code"];

        Assert.Equal(["Website", "Login", "Password", "Pin", "Notes", "Security question", "Recovery code"],
            PasswordEntryFieldOrder.Arrange(stored, n => n));
    }

    [Fact]
    public void A_missing_slot_is_skipped_and_a_second_field_of_a_slot_is_an_ordinary_field()
    {
        string[] stored = ["Password", "Notes", "URL", "url"];

        Assert.Equal(["URL", "Password", "Notes", "url"], PasswordEntryFieldOrder.Arrange(stored, n => n));
    }

    [Theory]
    [InlineData("URL", PasswordFieldSlot.Url)]
    [InlineData("url", PasswordFieldSlot.Url)]
    [InlineData("Website", PasswordFieldSlot.Url)]
    [InlineData("Link", PasswordFieldSlot.Url)]
    [InlineData("User name", PasswordFieldSlot.UserName)]
    [InlineData("Username", PasswordFieldSlot.UserName)]
    [InlineData("Login", PasswordFieldSlot.UserName)]
    [InlineData("E-mail", PasswordFieldSlot.UserName)]
    [InlineData(" PIN ", PasswordFieldSlot.Pin)]
    [InlineData("Pin", PasswordFieldSlot.Pin)]
    [InlineData("Password", PasswordFieldSlot.Password)]
    [InlineData("Pinboard", PasswordFieldSlot.Other)]
    [InlineData("Notes", PasswordFieldSlot.Other)]
    public void A_field_is_placed_by_its_name_in_the_usual_spellings(string name, PasswordFieldSlot slot) =>
        Assert.Equal(slot, PasswordEntryFieldOrder.SlotOf(name));

    // Owner, 2026-10-23: the PIN is not always offered; it is the first field suggested to add.
    [Fact]
    public void A_form_always_offers_url_user_name_and_password_and_suggests_the_PIN()
    {
        Assert.Equal([PasswordFieldSlot.Url, PasswordFieldSlot.UserName, PasswordFieldSlot.Password], PasswordEntryFieldOrder.AlwaysOffered);
        Assert.Equal([PasswordFieldSlot.Pin], PasswordEntryFieldOrder.Suggested);
    }

    [Theory]
    [InlineData("PIN", false, true)]
    [InlineData("pin", false, true)]
    [InlineData("Recovery code", true, true)]
    [InlineData("Recovery code", false, false)]
    [InlineData("User name", false, false)]
    public void A_PIN_is_always_hidden_like_a_password_and_other_fields_as_marked(string name, bool marked, bool secret) =>
        Assert.Equal(secret, SecretFields.ForField(name, marked).IsSecret);
}
