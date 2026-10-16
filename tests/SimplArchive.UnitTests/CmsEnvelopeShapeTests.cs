using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SimplArchive.Infrastructure.Encryption;

namespace SimplArchive.UnitTests;

// What the core accepts as sealed module content (ABI 1.20, ADR 0940): a CMS ContentInfo of enveloped or auth-enveloped
// data, exactly as long as the object when its length is definite — read from the head and the size alone.
public class CmsEnvelopeShapeTests
{
    private static readonly byte[] Envelope = Seal();

    [Fact]
    public void An_enveloped_message_is_an_envelope_from_its_head_and_size_alone() =>
        Assert.True(CmsEnvelopeShape.IsEnvelope(Envelope.AsSpan(0, CmsEnvelopeShape.HeadLength), Envelope.Length));

    [Fact]
    public void Auth_enveloped_data_and_an_indefinite_length_are_envelopes_too()
    {
        // 30 80 | 06 0B 1.2.840.113549.1.9.16.1.23 | A0 80 …: what a streaming encoder writes for AuthEnvelopedData.
        byte[] head = [0x30, 0x80, 0x06, 0x0B, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x09, 0x10, 0x01, 0x17, 0xA0, 0x80];
        Assert.True(CmsEnvelopeShape.IsEnvelope(head, 4096));
    }

    [Fact]
    public void Plaintext_another_cms_type_or_bytes_riding_behind_are_not()
    {
        Assert.False(CmsEnvelopeShape.IsEnvelope(Encoding.UTF8.GetBytes("plain words"), 11));
        Assert.False(CmsEnvelopeShape.IsEnvelope([], 0));
        Assert.False(CmsEnvelopeShape.IsEnvelope(Envelope, Envelope.Length + 4));   // a definite length must cover it exactly

        var signedData = (byte[])Envelope.Clone();
        byte[] envelopedData = [0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x03];
        var oid = Envelope.AsSpan().IndexOf(envelopedData);
        signedData[oid + 8] = 0x02;   // 1.2.840.113549.1.7.2: signed, readable by anyone
        Assert.False(CmsEnvelopeShape.IsEnvelope(signedData, signedData.Length));
    }

    private static byte[] Seal()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=reader", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var envelope = new EnvelopedCms(new ContentInfo(Encoding.UTF8.GetBytes("for the readers only")));
        envelope.Encrypt(new CmsRecipient(certificate));
        return envelope.Encode();
    }
}
