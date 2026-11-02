using System.Buffers.Text;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alcifra.ApiClient.PasswordStore;

/// <summary>
/// A passkey held in a password entry (Mobile#183, ADR 1033): the site it signs in to, the ids the site knows it by, and
/// its private key. Stored in the entry's sealed "Passkey" slot as <see cref="ToText"/>, so it is opened by the user's
/// hardware key like the password beside it.
/// </summary>
/// <remarks>
/// <para>
/// The private key leaves this type only through <see cref="ToText"/>, which builds the value the entry stores;
/// <see cref="ToString"/> leaves it out, so a passkey that reaches a log line carries nothing usable.
/// </para>
/// <para>
/// The key is P-256 with ECDSA over SHA-256 (COSE algorithm -7), which every relying party accepts. The sign counter is
/// always 0, as for every passkey that is shared or synchronised: a relying party then does not compare it.
/// </para>
/// </remarks>
public sealed class Passkey
{
    private const int FormatVersion = 1;

    private readonly byte[] _privateKey;

    private Passkey(string rpId, byte[] credentialId, byte[] userHandle, string? userName, byte[] privateKey)
    {
        RpId = rpId;
        CredentialId = credentialId;
        UserHandle = userHandle;
        UserName = userName;
        _privateKey = privateKey;
    }

    /// <summary>The site the passkey signs in to (WebAuthn's relying-party id), lowercased: a domain such as <c>example.com</c>.</summary>
    public string RpId { get; }

    /// <summary>The credential id the site stores for this passkey.</summary>
    public byte[] CredentialId { get; }

    /// <summary>The site's own opaque id for the account (WebAuthn's user handle).</summary>
    public byte[] UserHandle { get; }

    /// <summary>The account name the site gave at registration, or null.</summary>
    public string? UserName { get; }

    /// <summary>The credential id as the index and the wire carry it: base64url without padding.</summary>
    public string CredentialIdText => Base64Url.EncodeToString(CredentialId);

    /// <summary>The user handle as the index and the wire carry it: base64url without padding.</summary>
    public string UserHandleText => Base64Url.EncodeToString(UserHandle);

    /// <summary>A new passkey for this site and account, with a fresh P-256 key and a random 16-byte credential id.</summary>
    public static Passkey Create(string rpId, byte[] userHandle, string? userName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rpId);
        ArgumentNullException.ThrowIfNull(userHandle);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new Passkey(rpId.Trim().ToLowerInvariant(), RandomNumberGenerator.GetBytes(16), [.. userHandle],
            string.IsNullOrWhiteSpace(userName) ? null : userName, key.ExportPkcs8PrivateKey());
    }

    /// <summary>The value the entry's "Passkey" slot stores: a small versioned JSON object, the private key included.</summary>
    public string ToText() => new JsonObject
    {
        ["v"] = FormatVersion,
        ["rpId"] = RpId,
        ["credentialId"] = CredentialIdText,
        ["userHandle"] = UserHandleText,
        ["userName"] = UserName,
        ["alg"] = PasskeyAuthenticator.Es256,
        ["key"] = Base64Url.EncodeToString(_privateKey),
    }.ToJsonString();

    /// <summary>Reads a stored value; refused by a <see cref="PasskeyUnreadableException"/> that never carries the key.</summary>
    public static Passkey Parse(string text)
    {
        try
        {
            var json = JsonNode.Parse(text) as JsonObject ?? throw new PasskeyUnreadableException();
            if (json["v"]?.GetValue<int>() != FormatVersion || json["alg"]?.GetValue<int>() != PasskeyAuthenticator.Es256)
            {
                throw new PasskeyUnreadableException();
            }

            var rpId = json["rpId"]?.GetValue<string>();
            var key = Base64Url.DecodeFromChars(json["key"]?.GetValue<string>() ?? string.Empty);
            using var check = ECDsa.Create();
            check.ImportPkcs8PrivateKey(key, out _);
            return string.IsNullOrWhiteSpace(rpId)
                ? throw new PasskeyUnreadableException()
                : new Passkey(rpId, Base64Url.DecodeFromChars(json["credentialId"]?.GetValue<string>() ?? string.Empty),
                    Base64Url.DecodeFromChars(json["userHandle"]?.GetValue<string>() ?? string.Empty),
                    json["userName"]?.GetValue<string>(), key);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or CryptographicException)
        {
            throw new PasskeyUnreadableException();
        }
    }

    /// <summary>Reads a stored value, or null where it does not read.</summary>
    public static Passkey? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return Parse(text);
        }
        catch (PasskeyUnreadableException)
        {
            return null;
        }
    }

    internal ECDsa OpenKey()
    {
        var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(_privateKey, out _);
        return key;
    }

    /// <summary>The site and account, never the key.</summary>
    public override string ToString() => UserName is null ? RpId : $"{UserName} @ {RpId}";
}

/// <summary>
/// The authenticator's half of WebAuthn for a <see cref="Passkey"/> (Mobile#183, ADR 1033): the attestation object a
/// registration answers with and the signature a sign-in answers with. <b>The one implementation every client uses</b>;
/// what a platform's passkey interface wraps around these bytes is the platform's.
/// </summary>
/// <remarks>
/// The client-data hash comes from the caller: the system computes it from the page's request, and the authenticator
/// signs it without reading it. User presence and user verification are both claimed, because the key is opened by the
/// user's hardware key and its PIN. The passkey is marked backup-eligible and backed up, which it is: it lives in the
/// store, not on the device.
/// </remarks>
public static class PasskeyAuthenticator
{
    /// <summary>COSE's ECDSA with SHA-256 over P-256, the one algorithm a <see cref="Passkey"/> uses.</summary>
    public const int Es256 = -7;

    // WebAuthn's authenticator-data flags.
    private const byte UserPresent = 0x01;
    private const byte UserVerified = 0x04;
    private const byte BackupEligible = 0x08;
    private const byte BackedUp = 0x10;
    private const byte AttestedCredentialData = 0x40;

    /// <summary>The authenticator's model id: all zero, as for an authenticator that makes no attestation.</summary>
    public static readonly byte[] Aaguid = new byte[16];

    /// <summary>The authenticator data a sign-in answers with: the site's hash, the flags and a zero sign counter.</summary>
    public static byte[] AuthenticatorData(Passkey passkey) => AuthenticatorDataFor(passkey, attested: false);

    /// <summary>
    /// The attestation object a registration answers with (format "none"): the authenticator data with the new
    /// credential's id and public key in it.
    /// </summary>
    public static byte[] AttestationObject(Passkey passkey)
    {
        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(3);
        writer.WriteTextString("fmt");
        writer.WriteTextString("none");
        writer.WriteTextString("attStmt");
        writer.WriteStartMap(0);
        writer.WriteEndMap();
        writer.WriteTextString("authData");
        writer.WriteByteString(AuthenticatorDataFor(passkey, attested: true));
        writer.WriteEndMap();
        return writer.Encode();
    }

    /// <summary>The public key as COSE (a CBOR map), as it sits in the attestation object.</summary>
    public static byte[] CosePublicKey(Passkey passkey)
    {
        using var key = passkey.OpenKey();
        var point = key.ExportParameters(includePrivateParameters: false).Q;
        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(5);
        writer.WriteInt32(1);       // kty: EC2
        writer.WriteInt32(2);
        writer.WriteInt32(3);       // alg: ES256
        writer.WriteInt32(Es256);
        writer.WriteInt32(-1);      // crv: P-256
        writer.WriteInt32(1);
        writer.WriteInt32(-2);      // x
        writer.WriteByteString(point.X!);
        writer.WriteInt32(-3);      // y
        writer.WriteByteString(point.Y!);
        writer.WriteEndMap();
        return writer.Encode();
    }

    /// <summary>The public key as SubjectPublicKeyInfo (DER), which some platforms hand back beside the attestation.</summary>
    public static byte[] SubjectPublicKeyInfo(Passkey passkey)
    {
        using var key = passkey.OpenKey();
        return key.ExportSubjectPublicKeyInfo();
    }

    /// <summary>
    /// The signature a sign-in answers with: ECDSA (DER-encoded, as WebAuthn requires) over the authenticator data
    /// followed by the client-data hash.
    /// </summary>
    public static byte[] Sign(Passkey passkey, byte[] authenticatorData, byte[] clientDataHash)
    {
        using var key = passkey.OpenKey();
        return key.SignData([.. authenticatorData, .. clientDataHash], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static byte[] AuthenticatorDataFor(Passkey passkey, bool attested)
    {
        var data = new List<byte>(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(passkey.RpId)))
        {
            (byte)(UserPresent | UserVerified | BackupEligible | BackedUp | (attested ? AttestedCredentialData : 0)),
            0, 0, 0, 0,     // sign counter: always 0 (see Passkey)
        };

        if (attested)
        {
            data.AddRange(Aaguid);
            data.Add((byte)(passkey.CredentialId.Length >> 8));
            data.Add((byte)passkey.CredentialId.Length);
            data.AddRange(passkey.CredentialId);
            data.AddRange(CosePublicKey(passkey));
        }

        return [.. data];
    }
}

/// <summary>A stored passkey that does not read (an unknown version or algorithm, a damaged key); never carries the key.</summary>
public sealed class PasskeyUnreadableException() : Exception("The passkey stored in this entry does not read.");

/// <summary>
/// What the index says about an entry's passkey (ADR 1033), without opening it: the site it signs in to, its credential
/// id and the site's user handle (both base64url). What a phone lists to its system and a sign-in is matched by.
/// </summary>
public sealed record PasskeyIdentity(string Site, string CredentialId, string UserHandle)
{
    /// <summary>The identity a resource or row carries, or null where it names no passkey.</summary>
    internal static PasskeyIdentity? From(System.Text.Json.JsonElement json) =>
        ApiWire.StrOrNull(json, "passkeyId") is { Length: > 0 } id
            ? new PasskeyIdentity(ApiWire.StrOrNull(json, "passkeySite") ?? string.Empty, id, ApiWire.StrOrNull(json, "passkeyUserHandle") ?? string.Empty)
            : null;
}
