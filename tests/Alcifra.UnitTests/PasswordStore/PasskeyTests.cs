using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using Alcifra.ApiClient.PasswordStore;
using Alcifra.BrowserHost;
using Alcifra.Presentation;
using Alcifra.Pass;
using Alcifra.Pass.Infrastructure;

namespace Alcifra.UnitTests.PasswordStore;

// Mobile#183 slice 2, ADR 1033: a passkey held in a password entry. The authenticator's answers are checked as a
// relying party checks them (the attestation's public key verifies the sign-in's signature), and the key never reaches
// a row, the browser, the terminal or a log line.
public sealed class PasskeyTests
{
    private static readonly byte[] UserHandle = "anna-7f3a"u8.ToArray();

    [Fact]
    public void A_new_passkey_reads_back_from_what_the_entry_stores()
    {
        var passkey = Passkey.Create("Example.COM", UserHandle, "anna@example.com");

        var read = Passkey.Parse(passkey.ToText());

        Assert.Equal("example.com", read.RpId);
        Assert.Equal(passkey.CredentialId, read.CredentialId);
        Assert.Equal(16, read.CredentialId.Length);
        Assert.Equal(UserHandle, read.UserHandle);
        Assert.Equal("anna@example.com", read.UserName);
        Assert.Equal(PasskeyAuthenticator.CosePublicKey(passkey), PasskeyAuthenticator.CosePublicKey(read));
    }

    [Fact]
    public void A_passkey_describes_itself_without_its_key()
    {
        var passkey = Passkey.Create("example.com", UserHandle, "anna");
        var key = System.Text.Json.Nodes.JsonNode.Parse(passkey.ToText())!["key"]!.GetValue<string>();

        Assert.Equal("anna @ example.com", passkey.ToString());
        Assert.DoesNotContain(key, passkey.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"v\":2,\"alg\":-7,\"rpId\":\"a.b\",\"key\":\"AA\"}")]
    [InlineData("{\"v\":1,\"alg\":-8,\"rpId\":\"a.b\",\"key\":\"AA\"}")]
    [InlineData("{\"v\":1,\"alg\":-7,\"rpId\":\"a.b\",\"key\":\"AAAA\"}")]
    public void A_value_that_is_no_passkey_is_refused_without_echoing_it(string text)
    {
        var refused = Assert.Throws<PasskeyUnreadableException>(() => Passkey.Parse(text));
        Assert.DoesNotContain(text, refused.Message, StringComparison.Ordinal);
        Assert.Null(Passkey.TryParse(text));
    }

    [Fact]
    public void The_registration_answers_with_an_attestation_naming_the_site_the_credential_and_its_key()
    {
        var passkey = Passkey.Create("example.com", UserHandle, "anna");

        var reader = new CborReader(PasskeyAuthenticator.AttestationObject(passkey), CborConformanceMode.Ctap2Canonical);
        var map = ReadAttestation(reader);
        Assert.Equal("none", map.Format);
        var data = map.AuthData;

        Assert.Equal(SHA256.HashData("example.com"u8.ToArray()), data[..32]);
        Assert.Equal(0x5D, data[32]);                       // UP, UV, BE, BS, AT
        Assert.Equal(new byte[4], data[33..37]);            // sign counter 0
        Assert.Equal(new byte[16], data[37..53]);           // AAGUID
        Assert.Equal(16, (data[53] << 8) | data[54]);
        Assert.Equal(passkey.CredentialId, data[55..71]);
        Assert.Equal(PasskeyAuthenticator.CosePublicKey(passkey), data[71..]);
    }

    [Fact]
    public void A_sign_in_signature_verifies_with_the_key_the_registration_gave_the_site()
    {
        var passkey = Passkey.Create("example.com", UserHandle, "anna");
        using var registered = PublicKeyFromCose(PasskeyAuthenticator.CosePublicKey(passkey));
        var clientDataHash = SHA256.HashData(Encoding.UTF8.GetBytes("{\"type\":\"webauthn.get\",\"challenge\":\"abc\"}"));

        var authenticatorData = PasskeyAuthenticator.AuthenticatorData(passkey);
        var signature = PasskeyAuthenticator.Sign(passkey, authenticatorData, clientDataHash);

        Assert.Equal(37, authenticatorData.Length);
        Assert.Equal(0x1D, authenticatorData[32]);          // UP, UV, BE, BS; no attested data on a sign-in
        byte[] signed = [.. authenticatorData, .. clientDataHash];
        byte[] other = [.. authenticatorData, .. new byte[32]];
        Assert.True(registered.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.False(registered.VerifyData(other, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

        using var spki = ECDsa.Create();
        spki.ImportSubjectPublicKeyInfo(PasskeyAuthenticator.SubjectPublicKeyInfo(passkey), out _);
        Assert.Equal(registered.ExportParameters(false).Q.X, spki.ExportParameters(false).Q.X);
    }

    [Fact]
    public void An_entry_files_its_passkey_in_the_slot_after_the_code_and_indexes_its_site_and_ids()
    {
        var passkey = Passkey.Create("example.com", UserHandle, "anna");
        var content = PasswordEntryContent.Of("pw", url: "https://login.example.com").WithPasskey(passkey);

        Assert.Equal(PasswordFieldSlot.Passkey, PasswordEntryFieldOrder.Order[5]);
        Assert.True(PasswordEntryFieldOrder.IsSecret(PasswordFieldSlot.Passkey));
        Assert.DoesNotContain(PasswordFieldSlot.Passkey, PasswordEntryFieldOrder.Suggested);
        Assert.Equal("anna", content.UserName);              // taken from the registration where the entry had none
        Assert.Equal(("example.com", passkey.CredentialIdText, Base64Url.EncodeToString(UserHandle)), content.PasskeyIndex);
        Assert.Equal("login.example.com", content.Site);
        Assert.Equal((string.Empty, string.Empty, string.Empty), PasswordEntryContent.Of("pw").PasskeyIndex);

        var replaced = content.WithPasskey(Passkey.Create("example.com", UserHandle, "anna"));
        Assert.Single(replaced.Fields, f => f.Name == PasswordEntryFieldNames.Passkey);
    }

    [Fact]
    public void An_opened_entry_shows_the_passkey_as_its_account_and_site_never_its_key()
    {
        var passkey = Passkey.Create("example.com", UserHandle, "anna");
        var content = PasswordEntryContent.Of("pw", userName: "anna").WithPasskey(passkey);

        var row = Assert.Single(PasswordEntryRows.Of(content), r => r.IsPasskey);

        Assert.Equal("anna @ example.com", row.Value);
        Assert.False(row.IsSecret);
        Assert.DoesNotContain(PasswordEntryRows.Of(content), r => r.Value.Contains(passkey.ToText(), StringComparison.Ordinal));
    }

    [Fact]
    public void An_edit_keeps_the_passkey_untouched_and_removing_it_drops_it()
    {
        var passkey = Passkey.Create("example.com", UserHandle, "anna");
        var content = PasswordEntryContent.Of("pw", userName: "anna").WithPasskey(passkey);

        var form = PasswordEntryForm.From(content);
        Assert.True(form.HasPasskey);
        Assert.Equal("anna @ example.com", form.PasskeyDescription);
        Assert.DoesNotContain(form.Fields, f => f.Name == PasswordEntryFieldNames.Passkey);
        form.Password = "changed";
        Assert.Equal(passkey.ToText(), form.ToContent().Value(PasswordEntryFieldNames.Passkey));

        form.RemovePasskey();
        Assert.Null(form.ToContent().Passkey);
    }

    [Fact]
    public void The_browser_is_never_handed_the_passkey()
    {
        var passkey = Passkey.Create("example.com", UserHandle, "anna");
        var content = PasswordEntryContent.Of("pw", userName: "anna").WithPasskey(passkey);

        var handed = PasswordStoreAutofill.Credentials(content, TimeProvider.System);

        Assert.DoesNotContain(handed.Fields, f => f.Name == PasswordEntryFieldNames.Passkey);
    }

    [Fact]
    public void The_terminal_shows_the_passkey_as_its_account_and_site_and_an_edit_carries_it_over()
    {
        var passkey = Passkey.Create("example.com", UserHandle, "anna");
        var content = PasswordEntryContent.Of("pw", userName: "anna").WithPasskey(passkey);

        Assert.Contains("Passkey: anna @ example.com", Alcifra.Pass.Commands.ShowCommand.FieldLines(content, TimeProvider.System));
        var text = EntryText.Format(content);
        Assert.DoesNotContain("Passkey", text, StringComparison.Ordinal);

        var edited = EntryText.Parse(text.Replace("pw\n", "new\n", StringComparison.Ordinal), content.Fields);
        Assert.Equal("new", edited.Password);
        Assert.Equal(passkey.ToText(), edited.Value(PasswordEntryFieldNames.Passkey));
        Assert.Throws<PassException>(() => EntryText.Parse("pw\nPasskey: {}\n", content.Fields));
    }

    private static (string Format, byte[] AuthData) ReadAttestation(CborReader reader)
    {
        string? format = null;
        byte[]? authData = null;
        reader.ReadStartMap();
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            switch (reader.ReadTextString())
            {
                case "fmt":
                    format = reader.ReadTextString();
                    break;
                case "authData":
                    authData = reader.ReadByteString();
                    break;
                default:
                    reader.SkipValue();
                    break;
            }
        }

        return (format!, authData!);
    }

    private static ECDsa PublicKeyFromCose(byte[] cose)
    {
        var reader = new CborReader(cose, CborConformanceMode.Ctap2Canonical);
        var values = new Dictionary<int, object>();
        reader.ReadStartMap();
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var label = reader.ReadInt32();
            values[label] = reader.PeekState() == CborReaderState.ByteString ? reader.ReadByteString() : reader.ReadInt32();
        }

        Assert.Equal((2, -7, 1), ((int)values[1], (int)values[3], (int)values[-1]));
        return ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = (byte[])values[-2], Y = (byte[])values[-3] },
        });
    }
}
