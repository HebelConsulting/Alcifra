using System.Text;
using Alcifra.ApiClient.PasswordStore;

namespace Alcifra.UnitTests.PasswordStore;

// ADR 1013: the ONE verification-code rule every client computes with — RFC 6238 over a parsed otpauth link or a bare
// setup key. The vectors are RFC 6238 Appendix B's, whose seeds are ASCII digits of the hash's own length.
public sealed class VerificationCodeSetupTests
{
    private static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                text.Append(alphabet[(buffer >> bits) & 31]);
            }
        }

        if (bits > 0)
        {
            text.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        return text.ToString();
    }

    private static string Seed(int length) =>
        Base32(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("1234567890", 7))[..length]));

    [Theory]
    [InlineData(59L, "SHA1", "94287082")]
    [InlineData(59L, "SHA256", "46119246")]
    [InlineData(59L, "SHA512", "90693936")]
    [InlineData(1111111109L, "SHA1", "07081804")]
    [InlineData(1111111109L, "SHA256", "68084774")]
    [InlineData(1111111109L, "SHA512", "25091201")]
    [InlineData(1111111111L, "SHA1", "14050471")]
    [InlineData(1111111111L, "SHA256", "67062674")]
    [InlineData(1111111111L, "SHA512", "99943326")]
    [InlineData(1234567890L, "SHA1", "89005924")]
    [InlineData(1234567890L, "SHA256", "91819424")]
    [InlineData(1234567890L, "SHA512", "93441116")]
    [InlineData(2000000000L, "SHA1", "69279037")]
    [InlineData(2000000000L, "SHA256", "90698825")]
    [InlineData(2000000000L, "SHA512", "38618901")]
    [InlineData(20000000000L, "SHA1", "65353130")]
    [InlineData(20000000000L, "SHA256", "77737706")]
    [InlineData(20000000000L, "SHA512", "47863826")]
    public void The_RFC_6238_test_vectors_give_their_eight_digit_codes(long unixSeconds, string algorithm, string expected)
    {
        var seedLength = algorithm switch { "SHA256" => 32, "SHA512" => 64, _ => 20 };
        var setup = VerificationCodeSetup.Parse($"otpauth://totp/Test?secret={Seed(seedLength)}&algorithm={algorithm}&digits=8");

        Assert.Equal(expected, setup.CodeAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
    }

    // Six digits are the last six of the same truncation: the code most sites use.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    public void A_bare_key_means_SHA1_six_digits_thirty_seconds(long unixSeconds, string expected)
    {
        var setup = VerificationCodeSetup.Parse(Seed(20));

        Assert.Equal((VerificationCodeAlgorithm.Sha1, 6, 30), (setup.Algorithm, setup.Digits, setup.Period));
        Assert.Equal(expected, setup.CodeAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds)));
    }

    [Fact]
    public void A_key_is_read_whatever_its_spacing_case_and_padding()
    {
        var plain = Seed(20);
        var grouped = string.Join(' ', plain.ToLowerInvariant().Chunk(4).Select(c => new string(c))) + "====";

        var at = DateTimeOffset.FromUnixTimeSeconds(59);
        Assert.Equal(VerificationCodeSetup.Parse(plain).CodeAt(at), VerificationCodeSetup.Parse($"  {grouped} ").CodeAt(at));
        Assert.Equal(VerificationCodeSetup.Parse(plain).CodeAt(at), VerificationCodeSetup.Parse(plain.Insert(4, "-")).CodeAt(at));
    }

    [Fact]
    public void A_link_names_its_issuer_and_account_and_its_parameters_in_any_case()
    {
        var setup = VerificationCodeSetup.Parse(
            $"OTPAUTH://TOTP/ACME%20Co:alice%40example.com?SECRET={Seed(20).ToLowerInvariant()}&Issuer=ACME%20Co&Algorithm=sha256&Digits=8&Period=60");

        Assert.Equal("ACME Co", setup.Issuer);
        Assert.Equal("alice@example.com", setup.Account);
        Assert.Equal((VerificationCodeAlgorithm.Sha256, 8, 60), (setup.Algorithm, setup.Digits, setup.Period));
    }

    [Fact]
    public void The_label_gives_the_issuer_when_the_link_names_none()
    {
        var setup = VerificationCodeSetup.Parse($"otpauth://totp/Example:bob?secret={Seed(20)}");

        Assert.Equal(("Example", "bob"), (setup.Issuer, setup.Account));
    }

    [Fact]
    public void The_countdown_is_the_seconds_left_in_the_period()
    {
        var clock = new ManualTime(DateTimeOffset.FromUnixTimeSeconds(1111111109));
        var setup = VerificationCodeSetup.Parse(Seed(20));

        var code = setup.Current(clock);
        Assert.Equal(("081804", 1, 30), (code.Value, code.SecondsLeft, code.Period));   // 1111111109 % 30 == 29

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(30, setup.Current(clock).SecondsLeft);   // a new period, a new code
        Assert.NotEqual("081804", setup.Current(clock).Value);
    }

    [Theory]
    [InlineData("287082", "287 082")]
    [InlineData("94287082", "9428 7082")]
    public void A_code_is_shown_in_two_groups(string value, string grouped) =>
        Assert.Equal(grouped, new VerificationCode(value, 10, 30).Grouped);

    [Fact]
    public void A_bare_key_becomes_the_standard_link_and_reads_back_the_same()
    {
        var link = VerificationCodeSetup.Parse(Seed(20).ToLowerInvariant()).ToUri("anna@example.com");

        Assert.Equal($"otpauth://totp/anna%40example.com?secret={Seed(20)}", link);
        var at = DateTimeOffset.FromUnixTimeSeconds(59);
        Assert.Equal("287082", VerificationCodeSetup.Parse(link).CodeAt(at));
    }

    [Fact]
    public void A_link_with_parameters_keeps_them_in_the_link_it_makes()
    {
        var link = VerificationCodeSetup.Parse($"otpauth://totp/A:b?secret={Seed(20)}&issuer=A&algorithm=SHA512&digits=8&period=60").ToUri();

        Assert.Equal($"otpauth://totp/A:b?secret={Seed(20)}&issuer=A&algorithm=SHA512&digits=8&period=60", link);
    }

    [Fact]
    public void The_secret_never_appears_in_its_description() =>
        Assert.DoesNotContain(Seed(20), VerificationCodeSetup.Parse(Seed(20)).ToString(), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData(null, typeof(VerificationCodeSetupEmptyException))]
    [InlineData("   ", typeof(VerificationCodeSetupEmptyException))]
    [InlineData("not base32!", typeof(VerificationCodeSecretNotBase32Exception))]
    [InlineData("ABC1", typeof(VerificationCodeSecretNotBase32Exception))]          // 1 is not in the alphabet
    [InlineData("====", typeof(VerificationCodeSecretNotBase32Exception))]          // padding alone is no key
    [InlineData("otpauth://hotp/x?secret=GEZDGNBV&counter=1", typeof(NotATimeBasedCodeException))]
    [InlineData("otpauth://totp/x", typeof(VerificationCodeSecretMissingException))]
    [InlineData("otpauth://totp/x?secret=", typeof(VerificationCodeSecretMissingException))]
    [InlineData("otpauth://totp/x?secret=GEZ1", typeof(VerificationCodeSecretNotBase32Exception))]
    [InlineData("otpauth://totp/x?secret=GEZDGNBV&algorithm=MD5", typeof(UnsupportedCodeAlgorithmException))]
    [InlineData("otpauth://totp/x?secret=GEZDGNBV&digits=7", typeof(UnsupportedCodeDigitsException))]
    [InlineData("otpauth://totp/x?secret=GEZDGNBV&period=0", typeof(UnsupportedCodePeriodException))]
    [InlineData("otpauth://totp/x?secret=GEZDGNBV&period=-30", typeof(UnsupportedCodePeriodException))]
    [InlineData("otpauth://totp/x?secret=GEZDGNBV&period=abc", typeof(UnsupportedCodePeriodException))]
    [InlineData("otpauth:", typeof(VerificationCodeLinkUnreadableException))]
    public void A_value_that_is_no_setup_is_refused_by_name(string? value, Type refusal)
    {
        var thrown = Assert.ThrowsAny<VerificationCodeSetupException>(() => VerificationCodeSetup.Parse(value));
        Assert.IsType(refusal, thrown);
        Assert.False(VerificationCodeSetup.TryParse(value, out _));
    }

    [Fact]
    public void A_refusal_never_carries_the_secret()
    {
        var thrown = Assert.ThrowsAny<VerificationCodeSetupException>(() =>
            VerificationCodeSetup.Parse("otpauth://totp/x?secret=GEZDGNBVGY3TQOJQ&digits=9"));
        Assert.DoesNotContain("GEZDGNBVGY3TQOJQ", thrown.Message, StringComparison.Ordinal);
    }
}
