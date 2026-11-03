using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using MimeKit;
using Alcifra.ApiClient;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.SealedArchive;

namespace Alcifra.UnitTests.PasswordStore;

// The stored form of a password entry (ADR 0964): fields in the text body, the password alone in passwd.txt, the whole
// MIME entity sealed as one CMS envelope by the Messenger's sealer, and opened back through the shared S/MIME step.
public sealed class PasswordEntryFormatTests
{
    private static readonly PasswordEntryContent Sample = new("s3cr3t: with colon",
    [
        new(PasswordEntryFieldNames.UserName, "anna@example.org"),
        new(PasswordEntryFieldNames.Url, "https://example.org/login"),
        new("PIN", "0815", IsSecret: true),
        new("Empty", string.Empty),
        new(PasswordEntryFieldNames.Notes, "line one\nline two: with a colon\n\n  indented, and ünïcødé ✓"),
    ]);

    [Fact]
    public void An_entry_round_trips_every_field_its_order_its_secret_marks_and_its_password()
    {
        var parsed = PasswordEntryFormat.Parse(PasswordEntryFormat.ToBytes(Sample));

        Assert.Equal(Sample.Password, parsed.Password);
        Assert.Equal(Sample.Fields, parsed.Fields);
        Assert.Equal("anna@example.org", parsed.UserName);
        Assert.Equal("https://example.org/login", parsed.Url);
    }

    [Fact]
    public void The_password_lives_alone_in_passwd_txt_and_the_fields_in_the_text_body()
    {
        var entity = Assert.IsType<Multipart>(PasswordEntryFormat.ToMime(Sample));
        var attachment = Assert.Single(entity.OfType<MimePart>(), p => p.FileName == "passwd.txt");
        using var password = new MemoryStream();
        attachment.Content!.DecodeTo(password);
        Assert.Equal(Sample.Password, System.Text.Encoding.UTF8.GetString(password.ToArray()));

        var body = entity.OfType<TextPart>().First(p => !ReferenceEquals(p, attachment)).Text;
        Assert.Contains("PIN [secret]: 0815", body, StringComparison.Ordinal);
        Assert.Contains("User name: anna@example.org", body, StringComparison.Ordinal);
        Assert.DoesNotContain(Sample.Password, body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hand_written_line_without_the_grammar_is_kept_in_the_notes_rather_than_lost()
    {
        var entity = PasswordEntryFormat.ToMime(PasswordEntryContent.Of("pw"));
        var text = ((Multipart)entity).OfType<TextPart>().First(p => p.FileName is null);
        text.SetText("utf-8", "User name: bob\r\nremember the dog's name\r\n");

        var parsed = PasswordEntryFormat.Parse(entity);

        Assert.Equal("bob", parsed.UserName);
        Assert.Equal("remember the dog's name", parsed.Notes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a:b")]
    [InlineData("two\nlines")]
    [InlineData("PIN [secret]")]
    public void A_field_name_that_cannot_be_read_back_is_refused(string name) =>
        Assert.Throws<ArgumentException>(() => PasswordEntryFormat.ToBytes(new PasswordEntryContent("pw", [new(name, "v")])));

    // The password is optional (ADR 1044, #2130): an entry holds at least one of password, code and passkey.
    [Fact]
    public void An_entry_with_only_a_verification_code_has_no_passwd_txt_and_reads_back_without_a_password()
    {
        var content = new PasswordEntryContent(string.Empty,
            [new(PasswordEntryFieldNames.UserName, "anna"), new(PasswordEntryFieldNames.VerificationCode, "JBSWY3DPEHPK3PXP", IsSecret: true)]);

        var mime = PasswordEntryFormat.ToMime(content);
        Assert.DoesNotContain(((Multipart)mime).OfType<MimePart>(), p => p.FileName == PasswordEntryFormat.PasswordFileName);

        var read = PasswordEntryFormat.Parse(PasswordEntryFormat.ToBytes(content));
        Assert.False(read.HasPassword);
        Assert.True(read.HasCode);
        Assert.Equal("anna", read.UserName);
    }

    [Fact]
    public void An_entry_written_earlier_with_an_empty_password_reads_as_having_none()
    {
        var legacy = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "User name: x\r\n" },
            new MimePart("text", "plain") { Content = new MimeContent(new MemoryStream([])), FileName = PasswordEntryFormat.PasswordFileName },
        };

        Assert.False(PasswordEntryFormat.Parse(legacy).HasPassword);
    }

    [Fact]
    public void An_entry_with_no_password_code_or_passkey_is_refused_before_sealing() =>
        Assert.Throws<PasswordEntryEmptyException>(() => PasswordEntryFormat.ToBytes(new PasswordEntryContent(string.Empty, [new(PasswordEntryFieldNames.UserName, "x")])));

    [Fact]
    public void Content_with_neither_passwd_txt_nor_any_field_is_unreadable_never_an_empty_entry() =>
        Assert.Throws<PasswordEntryUnreadableException>(() => PasswordEntryFormat.Parse(new MimePart("application", "octet-stream") { Content = new MimeContent(new MemoryStream([1, 2])) }));

    [Fact]
    public void The_preview_shows_no_password_row_where_there_is_no_password()
    {
        var rows = PasswordEntryRows.Of(new PasswordEntryContent(string.Empty, [new(PasswordEntryFieldNames.VerificationCode, "JBSWY3DPEHPK3PXP", IsSecret: true)]));
        Assert.DoesNotContain(rows, r => r.IsPassword);
    }

    [Fact]
    public void Sealed_to_two_readers_each_opens_it_through_the_shared_S_MIME_step()
    {
        using var anna = Rsa("anna");
        using var bob = Rsa("bob");

        var cms = PasswordStoreClient.Seal(Sample, [Public(anna), Public(bob)]);
        Assert.True(CmsRecipients.AddressedToExactly(CmsRecipients.Of(cms), [Public(anna), Public(bob)]));

        foreach (var reader in new[] { anna, bob })
        {
            // What a host's opener does: unwrap the S/MIME message, decrypt the CMS with its key, read the payload.
            var envelope = new EnvelopedCms();
            envelope.Decode(SmimeEnvelope.Cms(PasswordStoreClient.AsSmime(cms)));
            envelope.Decrypt(new X509Certificate2Collection(reader));
            var (bytes, contentType) = SmimeEnvelope.Payload(envelope.ContentInfo.Content);

            Assert.Equal("multipart/mixed", contentType);
            Assert.Equal(Sample.Fields, PasswordEntryFormat.Parse(bytes).Fields);
        }
    }

    [Fact]
    public void Nothing_is_sealed_to_nobody() =>
        Assert.Throws<PasswordStoreHasNoRecipientsException>(() => PasswordStoreClient.Seal(Sample, []));

    internal static X509Certificate2 Rsa(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    internal static X509Certificate2 Ec(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    internal static X509Certificate2 Public(X509Certificate2 certificate) => X509CertificateLoader.LoadCertificate(certificate.RawData);
}
