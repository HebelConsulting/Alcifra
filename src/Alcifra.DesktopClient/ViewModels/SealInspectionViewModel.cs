using System.Globalization;
using Alcifra.ApiClient.Inspection;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// A sealed file's inspection as the dialog shows it (#2291), at the three levels the CLI has (ADR 1029): the summary, the
/// details per recipient and signer, and the ASN.1 structure. The web's SealInspectionDialog is its twin.
/// </summary>
public sealed partial class SealInspectionViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private readonly Services.SealKeyTrier? _keys;

    /// <param name="keys">Tries the user's keys on the same bytes (#2291); null where none can be tried (a signed file, or
    /// a host without them, such as the screenshot hook).</param>
    public SealInspectionViewModel(SealReport report, bool mayReseal, Services.SealKeyTrier? keys = null)
    {
        _keys = keys;
        Summary =
        [
            new(Strings.Get("SealFormat"), report.SmimeWrapped ? string.Format(CultureInfo.CurrentCulture, Strings.Get("SealSmime"), report.Format) : report.Format),
            new(Strings.Get("SealSize"), string.Format(CultureInfo.CurrentCulture, Strings.Get("SealBytes"), report.Size.ToString("N0", CultureInfo.CurrentCulture))),
            .. report.ContentType is { } content ? [new SealSummaryRow(Strings.Get("SealContentType"), content)] : Array.Empty<SealSummaryRow>(),
            .. report.ContentEncryption is { } encryption ? [new SealSummaryRow(Strings.Get("SealEncryption"), encryption)] : Array.Empty<SealSummaryRow>(),
        ];
        Recipients = [.. report.Recipients.Select(SealRecipientRow.Of)];
        Signers = [.. report.Signers.Select(SealSignerRow.Of)];
        Certificates = [.. report.Certificates.Select(c => string.Format(CultureInfo.CurrentCulture, Strings.Get("SealCarries"), c))];
        RecipientsHeading = string.Format(CultureInfo.CurrentCulture, Strings.Get("SealRecipients"), Recipients.Count);
        SignersHeading = string.Format(CultureInfo.CurrentCulture, Strings.Get("SealSigners"), Signers.Count);
        HasRecipients = report.Kind is SealedKind.Enveloped or SealedKind.Archive;
        HasSigners = report.Kind == SealedKind.Signed;
        RecipientsNotDecoded = report.Kind == SealedKind.AuthEnveloped;
        Audience = report.Audience;
        AudienceVerdict = report.Audience is { } audience ? Strings.Get(audience.Matches ? "SealAudienceMatches" : "SealAudienceGap") : null;
        Missing = [.. (report.Audience?.Missing ?? []).Select(m => string.Format(CultureInfo.CurrentCulture, Strings.Get("SealMissing"), m.Person))];
        CanReseal = mayReseal && report.Audience is { Matches: false };
        Asn1 = Asn1Tree.Lines(report.Asn1);
    }

    public IReadOnlyList<SealSummaryRow> Summary { get; }

    public string RecipientsHeading { get; }

    public IReadOnlyList<SealRecipientRow> Recipients { get; }

    public bool HasRecipients { get; }

    /// <summary>AuthEnvelopedData: the platform's reader does not decode its recipients (ADR 1029); the ASN.1 shows it.</summary>
    public bool RecipientsNotDecoded { get; }

    public string SignersHeading { get; }

    public IReadOnlyList<SealSignerRow> Signers { get; }

    public bool HasSigners { get; }

    public IReadOnlyList<string> Certificates { get; }

    public SealReportAudience? Audience { get; }

    /// <summary>Whether the envelope matches the declared audience; null where there is none to compare.</summary>
    public string? AudienceVerdict { get; }

    public bool AudienceMatches => Audience is { Matches: true };

    public bool AudienceDiffers => Audience is { Matches: false };

    public IReadOnlyList<string> Missing { get; }

    /// <summary>The gap is real and the caller manages the audience: Re-seal opens it with their key and seals it again.</summary>
    public bool CanReseal { get; }

    /// <summary>The CMS structure, one node per line with offsets and lengths, as the CLI's <c>--extra-verbose</c>.</summary>
    public string Asn1 { get; }

    public bool HasAsn1 => Asn1.Length > 0;

    // ---- Which of my keys opens it (#2291, the CLI's --try-open) --------------------------------------------------------

    /// <summary>Keys can be tried here: an envelope, on the desktop.</summary>
    public bool CanTryKeys => _keys is not null && HasRecipients;

    /// <summary>What the last try found, in a sentence; null before any.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string? _keyVerdict;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _keyOpened;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _tryingKeys;

    /// <summary>The keys in this computer's certificate store, then the card (its PIN asked as for any read).</summary>
    public async Task TryMineAsync()
    {
        if (_keys is null || TryingKeys)
        {
            return;
        }

        TryingKeys = true;
        KeyVerdict = Strings.Get("SealTryBusy");
        try
        {
            Show(await _keys.TryMineAsync());
        }
        finally
        {
            TryingKeys = false;
        }
    }

    /// <summary>A key the user picked from a file; the window loads it and asks for its password.</summary>
    public async Task TryKeyAsync(System.Security.Cryptography.X509Certificates.X509Certificate2 key)
    {
        if (_keys is null || TryingKeys)
        {
            return;
        }

        TryingKeys = true;
        try
        {
            Show(await _keys.TryKeyAsync(key));
        }
        finally
        {
            TryingKeys = false;
        }
    }

    /// <summary>A key file that could not be read: said where the verdict goes.</summary>
    public void KeyUnreadable(string reason)
    {
        KeyOpened = false;
        KeyVerdict = string.Format(CultureInfo.CurrentCulture, Strings.Get("SealTryUnreadable"), reason);
    }

    private void Show(Alcifra.HardwareKeys.KeyTryResult result)
    {
        KeyOpened = result.Opened;
        KeyVerdict = result.Outcome switch
        {
            Alcifra.HardwareKeys.KeyTryOutcome.Opened => string.Format(CultureInfo.CurrentCulture, Strings.Get("SealTryOpened"), result.Key ?? string.Empty),
            Alcifra.HardwareKeys.KeyTryOutcome.NotAddressedToKey => string.Format(CultureInfo.CurrentCulture, Strings.Get("SealTryNotAddressed"), result.Key ?? string.Empty),
            Alcifra.HardwareKeys.KeyTryOutcome.KeyDidNotOpen => string.Format(CultureInfo.CurrentCulture, Strings.Get("SealTryKeyFailed"), result.Key ?? string.Empty),
            Alcifra.HardwareKeys.KeyTryOutcome.CardNotARecipient => string.Format(CultureInfo.CurrentCulture, Strings.Get("SealTryCardNotRecipient"), result.Key ?? string.Empty),
            _ => Strings.Get("SealTryNoKey"),
        };
    }
}

public sealed record SealSummaryRow(string Label, string Value);

/// <summary>One recipient: who it is (or that nobody here knows), what is wrong with its key, and how the envelope names it.</summary>
public sealed record SealRecipientRow(string Who, string? Problem, bool IsExtra, string Method, string Reference, string? Validity)
{
    public bool HasProblem => Problem is not null;

    public bool HasValidity => Validity is not null;

    public static SealRecipientRow Of(SealReportRecipient r)
    {
        var who = r.IsKnown
            ? $"{r.HolderName ?? Strings.Get("SealHiddenHolder")}{(r.HolderEmail is { } email ? $" <{email}>" : string.Empty)}, "
                + string.Format(CultureInfo.CurrentCulture, Strings.Get("SealKeyLabel"), r.Label)
            : Strings.Get("SealUnknownRecipient");
        string?[] problems =
        [
            r.RevokedAt is { } revoked ? string.Format(CultureInfo.CurrentCulture, Strings.Get("SealRevoked"), revoked.InZone(SessionTimeZone.Current).ToString("d", CultureInfo.CurrentCulture)) : null,
            r.Attestation switch { "rootNotTrusted" => Strings.Get("SealAttRootNotTrusted"), "notAttested" => Strings.Get("SealAttNotAttested"), _ => null },
            r.InAudience == false ? Strings.Get("SealExtra") : null,
        ];
        var method = string.Format(CultureInfo.CurrentCulture, Strings.Get("SealMethod"), r.Method, r.KeyEncryptionAlgorithm)
            + (r.OriginatorKeyAlgorithm is { } originator ? $", {string.Format(CultureInfo.CurrentCulture, Strings.Get("SealOriginator"), originator)}" : string.Empty);
        var reference = r.Reference switch
        {
            { SubjectKeyIdentifier: { } ski } => string.Format(CultureInfo.CurrentCulture, Strings.Get("SealRefSki"), ski),
            { Issuer: { } issuer, Serial: { } serial } => string.Format(CultureInfo.CurrentCulture, Strings.Get("SealRefIssuerSerial"), issuer, serial),
            _ => string.Empty,
        };
        var validity = r.NotAfter is { } notAfter
            ? string.Format(CultureInfo.CurrentCulture, Strings.Get("SealValidUntil"), notAfter.InZone(SessionTimeZone.Current).ToString("d", CultureInfo.CurrentCulture))
            : null;
        var problem = string.Join(Environment.NewLine, problems.OfType<string>());
        return new SealRecipientRow(who, problem.Length > 0 ? problem : null, r.InAudience == false, method, reference, validity);
    }
}

/// <summary>One signer, whether its signature holds, and the digest and time it signed with.</summary>
public sealed record SealSignerRow(string Who, string Verdict, string Detail)
{
    public static SealSignerRow Of(InspectedSigner s) => new(
        s.Subject ?? Strings.Get("SealUnnamedSigner"),
        Strings.Get(s.SignatureValid switch { true => "SealSignatureHolds", false => "SealSignatureFails", null => "SealSignatureDetached" }),
        string.Join(", ", new[]
        {
            string.Format(CultureInfo.CurrentCulture, Strings.Get("SealSignedWith"), s.DigestAlgorithm),
            s.SigningTime is { } t ? string.Format(CultureInfo.CurrentCulture, Strings.Get("SealSignedAt"), t.InZone(SessionTimeZone.Current).ToString("g", CultureInfo.CurrentCulture)) : null,
        }.OfType<string>()));
}
