using Alcifra.Api.ProtocolDevices;
using Alcifra.Domain.Users;
using Alcifra.Infrastructure.Modules;

namespace Alcifra.UnitTests;

// #1970: the two pure rules of protocol devices — what an IMAP session seals to, and the password's shape.
public class ProtocolDeviceRulesTests
{
    private static readonly ReaderCertificateSource WithCertificate = new(["-----BEGIN CERTIFICATE-----"], AnsweredByModule: true);

    private static ProtocolDevice Device(ProtocolDeviceKind kind, bool encryptMail) => new()
    {
        Name = "d",
        SecretHash = "h",
        Kind = kind,
        EncryptMail = encryptMail,
        MailEnabled = true,
    };

    [Theory]
    // Where the organisation seals mail (EnvelopesMail), the device's flag is disregarded: as IMAP always did.
    [InlineData(ProtocolDeviceKind.Enrolled, false, true, true, true, false)]
    [InlineData(ProtocolDeviceKind.Enrolled, true, true, false, false, false)]
    // The earlier IMAP password behaves as IMAP always did, whatever its flag.
    [InlineData(ProtocolDeviceKind.LegacyImap, false, false, true, true, false)]
    [InlineData(ProtocolDeviceKind.LegacyImap, false, false, false, false, false)]
    // An enrolled device: on seals, and with nothing to seal to the login is REFUSED, never plaintext.
    [InlineData(ProtocolDeviceKind.Enrolled, true, false, true, true, false)]
    [InlineData(ProtocolDeviceKind.Enrolled, true, false, false, false, true)]
    // Off is plaintext, even with a certificate to seal to.
    [InlineData(ProtocolDeviceKind.Enrolled, false, false, true, false, false)]
    public void A_session_seals_as_the_device_and_the_organisation_decide(
        ProtocolDeviceKind kind, bool encryptMail, bool organisationSealsMail, bool hasCertificate, bool seals, bool refused)
    {
        var (certificates, wasRefused) = DeviceMailSealing.Decide(
            Device(kind, encryptMail), organisationSealsMail, hasCertificate ? WithCertificate : ReaderCertificateSource.None);

        Assert.Equal(seals, certificates.Envelopes);
        Assert.Equal(refused, wasRefused);
    }

    [Fact]
    public void A_generated_password_parses_back_into_its_key_and_secret()
    {
        var (key, secret, password) = ProtocolDevicePassword.Generate();

        Assert.True(ProtocolDevicePassword.TryParse(password, out var parsedKey, out var parsedSecret));
        Assert.Equal((key, secret), (parsedKey, parsedSecret));
        Assert.Equal(ProtocolDevicePassword.KeyLength, key.Length);
        Assert.DoesNotContain(':', password);   // Basic auth splits at the first colon
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef")]   // an earlier password: 32 hex, no dot
    [InlineData("ABCDEFGH.secret")]                     // not the key alphabet
    [InlineData("short.secret")]
    [InlineData("abcdefgh.")]
    public void Anything_else_is_not_a_device_password(string password) =>
        Assert.False(ProtocolDevicePassword.TryParse(password, out _, out _));

    [Fact]
    public void A_device_password_is_throttled_under_its_own_key_and_anything_else_under_the_account()
    {
        var (key, _, password) = ProtocolDevicePassword.Generate();

        Assert.Equal($"ANNA@EXAMPLE.TEST#{key}", ProtocolDevicePassword.ThrottleIdentity("ANNA@EXAMPLE.TEST", password));
        Assert.Equal("ANNA@EXAMPLE.TEST", ProtocolDevicePassword.ThrottleIdentity("ANNA@EXAMPLE.TEST", "0123456789abcdef0123456789abcdef"));
    }
}
