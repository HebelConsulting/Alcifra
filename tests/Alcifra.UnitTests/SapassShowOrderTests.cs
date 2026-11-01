using Alcifra.ApiClient.PasswordStore;
using Alcifra.Sapass.Commands;

namespace Alcifra.UnitTests;

// apass show and otp (owner, 2026-11-01, ADR 1024): show lists every field in the form's order (PasswordEntryFieldOrder,
// ADR 0964), the password labelled in its place; otp prints the code and its seconds left, --code-only the code alone. A verification code's setup is a secret, shown as the current code, never as itself.
public class SapassShowOrderTests
{
    private const string Setup = "otpauth://totp/Example:alice?secret=JBSWY3DPEHPK3PXP&issuer=Example";

    private static readonly PasswordEntryContent Entry = new(
        "s3cret",
        [
            new PasswordEntryField("Notes", "the old account"),
            new PasswordEntryField("PIN", "1234"),
            new PasswordEntryField("TOTP", Setup),
            new PasswordEntryField("User name", "alice"),
            new PasswordEntryField("URL", "https://mail.example"),
        ]);

    private static IReadOnlyList<string> Show(PasswordEntryContent content) => [.. ShowCommand.FieldLines(content, TimeProvider.System)];

    [Fact]
    public void Every_field_follows_the_forms_order_the_password_labelled_in_its_place()
    {
        var lines = Show(Entry);

        Assert.Equal(["URL", "User name", "Password", "PIN", "TOTP", "Notes"], lines.Select(line => line[..line.IndexOf(':')]));
        Assert.Equal("Password: s3cret", lines[2]);
    }

    [Fact]
    public void A_verification_code_is_shown_as_the_current_code_with_its_seconds_left_never_as_its_setup()
    {
        Assert.True(VerificationCodeSetup.TryParse(Setup, out var setup));

        var line = Assert.Single(Show(Entry), l => l.StartsWith("TOTP:", StringComparison.Ordinal));

        Assert.DoesNotContain("otpauth", line);
        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", line);
        Assert.Matches(@"^TOTP: \d{6} \(valid \d{1,2} s\)$", line);
        Assert.StartsWith($"TOTP: {setup!.Current(TimeProvider.System).Value} ", line);
    }

    [Fact]
    public void Otp_prints_the_code_and_its_seconds_left_unless_asked_for_the_code_alone()
    {
        Assert.True(VerificationCodeSetup.TryParse(Setup, out var setup));
        var code = setup!.Current(TimeProvider.System);

        Assert.Equal([code.Value, $"valid {code.SecondsLeft} s"], OtpCommand.Lines(code, codeOnly: false));
        Assert.Equal([code.Value], OtpCommand.Lines(code, codeOnly: true));
    }

}
