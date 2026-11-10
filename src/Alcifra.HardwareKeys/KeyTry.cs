using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Alcifra.ApiClient;
using Alcifra.ApiClient.Inspection;

namespace Alcifra.HardwareKeys;

/// <summary>How a key try ended (#2007 slice 3, #2291).</summary>
public enum KeyTryOutcome
{
    /// <summary>The key opened the envelope; the content was wiped at once.</summary>
    Opened,

    /// <summary>The envelope names no recipient this key is.</summary>
    NotAddressedToKey,

    /// <summary>The envelope names the key, and it still did not open it.</summary>
    KeyDidNotOpen,

    /// <summary>No card could be reached: no smartcard software, or no card in a reader (<see cref="KeyTryResult.Detail"/>).</summary>
    NoCard,

    /// <summary>A card is in the reader, and it is not one the envelope is sealed to.</summary>
    CardNotARecipient,
}

/// <summary>What a key try found, and which key: its certificate's subject.</summary>
/// <param name="Detail">For <see cref="KeyTryOutcome.NoCard"/>: which of the two is missing, in English, for the CLI.</param>
public sealed record KeyTryResult(KeyTryOutcome Outcome, string? Key, string? Detail = null)
{
    public bool Opened => Outcome == KeyTryOutcome.Opened;
}

/// <summary>
/// Which of the caller's keys opens a sealed envelope (#2007's <c>--try-open</c>, the desktop's <i>Which of my keys opens
/// it?</i>, #2291): a key on this computer, or the hardware key in a reader. The content is wiped at once and never kept or
/// written; only which key opened it is said. One implementation for the CLI and the desktop; how each ASKS for a PIN or a
/// password is the host's (<see cref="CardSession.PinPrompt"/>, and the key it loads).
/// </summary>
public static class KeyTry
{
    /// <summary>
    /// Tries a key held in software: RSA through the shared S/MIME opener, ECDH through the key-agreement opener the card
    /// uses (the mail library's S/MIME context does not open ECDH key agreement).
    /// </summary>
    /// <param name="debug">Where a key that could not be used says why; never given a secret.</param>
    public static KeyTryResult WithKey(byte[] cms, IReadOnlyList<InspectedRecipient> recipients, X509Certificate2 key, Action<string>? debug = null)
    {
        if (!recipients.Any(r => r.Reference.Is(key)))
        {
            return new KeyTryResult(KeyTryOutcome.NotAddressedToKey, key.Subject);
        }

        if (key.GetECDiffieHellmanPrivateKey() is { } ecdh)
        {
            using (ecdh)
            {
                var index = recipients.ToList().FindIndex(r => r.Reference.Is(key));
                try
                {
                    Array.Clear(CardEnvelopeOpener.OpenWithAgreement(cms, point => Agree(ecdh, point), index));
                    return new KeyTryResult(KeyTryOutcome.Opened, key.Subject);
                }
                catch (Exception e) when (e is CryptographicException or EnvelopeNotOpenedException or ArgumentException)
                {
                    debug?.Invoke($"The ECDH key of {key.Subject} did not open it: {e.Message}");
                    return new KeyTryResult(KeyTryOutcome.KeyDidNotOpen, key.Subject);
                }
            }
        }

        if (SoftwareKeyEnvelope.TryOpenRaw(cms, [key], (certificate, e) => debug?.Invoke($"The key of {certificate.Subject} could not be used: {e.Message}")) is { } decrypted)
        {
            Array.Clear(decrypted);
            return new KeyTryResult(KeyTryOutcome.Opened, key.Subject);
        }

        return new KeyTryResult(KeyTryOutcome.KeyDidNotOpen, key.Subject);
    }

    /// <summary>
    /// Tries the hardware key in a reader: its PIN is asked (through <see cref="CardSession.PinPrompt"/>) only once the card
    /// holds a recipient's certificate that <paramref name="admitted"/> lets be used. The session is the host's to close.
    /// </summary>
    public static async Task<KeyTryResult> WithCardAsync(byte[] cms, IReadOnlyList<InspectedRecipient> recipients, Func<X509Certificate2, bool> admitted)
    {
        var named = recipients.Where(r => r.Reference is { Issuer: not null, Serial: not null })
            .Select(r => (r.Reference.Issuer!, r.Reference.Serial!)).ToList();
        if (await CardEnvelopeOpener.TryOpenRawAsync(cms, named, admitted) is { } content)
        {
            Array.Clear(content);
            var card = CardEnvelopeOpener.LastSeen.FirstOrDefault(c => recipients.Any(r => r.Reference.Is(c)));
            return new KeyTryResult(KeyTryOutcome.Opened, card?.Subject);
        }

        return CardEnvelopeOpener.LastSeen.Count == 0
            ? new KeyTryResult(KeyTryOutcome.NoCard, null, CardEnvelopeOpener.WhatIsMissing())
            : new KeyTryResult(KeyTryOutcome.CardNotARecipient, CardEnvelopeOpener.LastSeen[0].Subject);
    }

    /// <summary>
    /// A key from a file: a PEM holding the certificate and its private key, or a PKCS#12 file. Throws
    /// <see cref="CryptographicException"/> when a PKCS#12 file needs a <paramref name="password"/> it was not given (or a
    /// wrong one), so the host asks and calls again; the password is the host's to ask for and is never kept here.
    /// </summary>
    public static X509Certificate2 LoadKeyFile(byte[] bytes, string? password)
    {
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal) && text.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
        {
            // A key read from PEM is ephemeral, and the S/MIME context imports it by exporting it: re-loaded as exportable.
            using var ephemeral = X509Certificate2.CreateFromPem(text, text);
            return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        }

        return X509CertificateLoader.LoadPkcs12(bytes, password);
    }

    // The RAW agreement (the unhashed Z the X9.63 KDF consumes), with the sender's ephemeral point.
    private static byte[] Agree(ECDiffieHellman key, byte[] uncompressedPoint)
    {
        var half = (uncompressedPoint.Length - 1) / 2;
        using var originator = ECDiffieHellman.Create(new ECParameters
        {
            Curve = key.ExportParameters(false).Curve,
            Q = new ECPoint { X = uncompressedPoint[1..(1 + half)], Y = uncompressedPoint[(1 + half)..] },
        });
        return key.DeriveRawSecretAgreement(originator.PublicKey);
    }
}
