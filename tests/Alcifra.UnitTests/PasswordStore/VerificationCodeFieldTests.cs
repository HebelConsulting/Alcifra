using Alcifra.ApiClient.PasswordStore;
using Alcifra.Presentation;

namespace Alcifra.UnitTests.PasswordStore;

// ADR 1013: the verification code as a FIELD of an entry — its slot after the PIN, its row that shows the current code
// and never the setup, its place in the form, and the index flag a client files from it.
public sealed class VerificationCodeFieldTests
{
    // RFC 6238's SHA-1 seed "12345678901234567890" in base32.
    private const string Key = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private static readonly string Link = $"otpauth://totp/Bank:anna?secret={Key}&issuer=Bank";

    [Theory]
    [InlineData("Verification code")]
    [InlineData("verification CODE")]
    [InlineData("One-time code")]
    [InlineData("TOTP")]
    [InlineData("2fa")]
    [InlineData(" OTP ")]
    public void The_usual_spellings_are_the_verification_code_slot(string name) =>
        Assert.Equal(PasswordFieldSlot.VerificationCode, PasswordEntryFieldOrder.SlotOf(name));

    [Fact]
    public void The_code_comes_after_the_PIN_is_always_secret_and_is_suggested_after_it()
    {
        Assert.Equal(PasswordFieldSlot.VerificationCode, PasswordEntryFieldOrder.Order[4]);
        Assert.Equal([PasswordFieldSlot.Pin, PasswordFieldSlot.VerificationCode], PasswordEntryFieldOrder.Suggested);
        Assert.DoesNotContain(PasswordFieldSlot.VerificationCode, PasswordEntryFieldOrder.AlwaysOffered);
        Assert.True(SecretFields.ForField("TOTP", markedSecret: false).IsSecret);

        string[] stored = ["Notes", "TOTP", "PIN", "Password", "URL"];
        Assert.Equal(["URL", "Password", "PIN", "TOTP", "Notes"], PasswordEntryFieldOrder.Arrange(stored, n => n));
    }

    [Fact]
    public void An_entry_has_a_code_only_when_the_slot_holds_a_value()
    {
        Assert.True(new PasswordEntryContent("pw", [new("2FA", Link, true)]).HasCode);
        Assert.False(new PasswordEntryContent("pw", [new("2FA", "  ", true)]).HasCode);
        Assert.False(new PasswordEntryContent("pw", [new("Recovery code", "abc", true)]).HasCode);
    }

    [Fact]
    public async Task The_row_shows_the_current_code_never_the_setup_and_copies_the_code_as_a_secret()
    {
        var clock = new ManualTime(DateTimeOffset.FromUnixTimeSeconds(59));
        var clipboard = new FakeClipboard();
        var row = Assert.Single(PasswordEntryRows.Of(new PasswordEntryContent("pw", [new("TOTP", Link, true)])), r => r.IsVerificationCode);
        var field = new SecretField(row, clipboard, clock);

        Assert.True(field.IsCode);
        Assert.Equal("287 082", field.Display);
        Assert.Equal(1, field.CurrentCode!.Value.SecondsLeft);
        field.Reveal.Press();                               // nothing reveals the setup, held or not
        Assert.DoesNotContain(Key, field.Display, StringComparison.Ordinal);

        var copied = field.CopyAsync(_ => throw new InvalidOperationException("a code is never copied as plain text"));
        Assert.Equal("287082", clipboard.Text);
        Assert.True(clipboard.Sensitive);
        clock.Advance(SecretFields.ClipboardLifetime);
        Assert.True(await copied);
        Assert.Null(clipboard.Text);
    }

    [Fact]
    public void A_setup_that_does_not_read_is_shown_masked_and_copies_nothing()
    {
        var row = new PasswordEntryRow("Verification code", "garbage!", SecretFields.ForField("TOTP", true), FieldName: "TOTP");
        var field = new SecretField(row, new FakeClipboard());

        Assert.Equal(SecretFields.Mask, field.Display);
        Assert.Null(field.CurrentCode);
    }

    [Fact]
    public void The_form_reads_the_code_and_files_a_pasted_key_as_the_standard_link()
    {
        var form = PasswordEntryForm.From(new PasswordEntryContent("pw", [new("User name", "anna"), new("OTP", Link, true)]));
        Assert.True(form.HasVerificationCode);
        Assert.False(form.SuggestsVerificationCode);
        Assert.Equal(Link, form.VerificationCode);

        // An untouched link is kept as it was stored, under the name it was stored under.
        Assert.Contains(form.ToContent().Fields, f => f is { Name: "OTP", IsSecret: true } && f.Value == Link);

        form.VerificationCode = "gezd gnbv gy3t qojq gezd gnbv gy3t qojq";
        var code = Assert.Single(form.ToContent().Fields, f => f.Name == "OTP");
        Assert.Equal($"otpauth://totp/anna?secret={Key}", code.Value);
        Assert.Null(form.Problem(creating: false));

        form.VerificationCode = string.Empty;   // emptied: the code is removed
        Assert.DoesNotContain(form.ToContent().Fields, f => f.Name == "OTP");
    }

    [Fact]
    public void A_new_entry_suggests_the_code_and_refuses_a_value_that_is_no_setup()
    {
        var form = new PasswordEntryForm { Name = "Bank" };
        Assert.True(form.SuggestsVerificationCode);
        form.AddVerificationCode();
        Assert.False(form.SuggestsVerificationCode);

        form.VerificationCode = "hunter1!";   // "1" and "!" are not base32
        Assert.True(form.VerificationCodeInvalid);
        Assert.Equal("PwVerificationCodeInvalid", form.Problem(creating: true));

        form.VerificationCode = Key;   // no user name: the link is labelled with the entry's name
        Assert.Equal($"otpauth://totp/Bank?secret={Key}",
            Assert.Single(form.ToContent().Fields, f => f.Name == PasswordEntryFieldNames.VerificationCode).Value);
    }
}
