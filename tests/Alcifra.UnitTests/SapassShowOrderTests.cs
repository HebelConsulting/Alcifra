using Alcifra.ApiClient.PasswordStore;
using Alcifra.Sapass.Commands;

namespace Alcifra.UnitTests;

// apass show (owner, 2026-10-31): the bare password stays the FIRST line (pass's shape, `| head -1`), and every other
// field follows in the form's order (PasswordEntryFieldOrder, ADR 0964) rather than the order it was stored in. A
// verification code's setup is a secret, so it is shown as the current code, never as its otpauth:// link.
public class SapassShowOrderTests
{
    private const string Setup = "otpauth://totp/Example:alice?secret=JBSWY3DPEHPK3PXP&issuer=Example";

    [Fact]
    public void The_fields_follow_the_forms_order_not_the_stored_one()
    {
        var content = new PasswordEntryContent(
            "s3cret",
            [
                new PasswordEntryField("Notes", "the old account"),
                new PasswordEntryField("PIN", "1234"),
                new PasswordEntryField("TOTP", Setup),
                new PasswordEntryField("User name", "alice"),
                new PasswordEntryField("URL", "https://mail.example"),
            ]);

        // The password line comes before these, printed on its own (ShowCommand), so `| head -1` stays the password.
        var lines = ShowCommand.FieldLines(content, TimeProvider.System).ToList();

        Assert.Equal(
            ["URL", "User name", "PIN", "TOTP", "Notes"],
            lines.Select(line => line[..line.IndexOf(':')]));
        Assert.DoesNotContain(lines, line => line.Contains("s3cret"));
    }

    [Fact]
    public void A_verification_code_is_shown_as_the_current_code_never_as_its_setup()
    {
        var content = new PasswordEntryContent("s3cret", [new PasswordEntryField("TOTP", Setup)]);
        Assert.True(VerificationCodeSetup.TryParse(Setup, out var setup));

        var line = ShowCommand.FieldLines(content, TimeProvider.System).Single();

        Assert.DoesNotContain("otpauth", line);
        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", line);
        Assert.Matches(@"^TOTP: \d{6}$", line);
        Assert.Equal($"TOTP: {setup!.Current(TimeProvider.System).Value}", line);
    }
}
