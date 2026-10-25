using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SimplArchive.ApiClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// Choose who a sealed document's content is sealed to (ADR 0975/0976): the tenant's readers, and certificates added
/// from a file with their attestation chain.
/// </summary>
/// <remarks>
/// <para>
/// <b>The content is sealed on THIS device.</b> The dialog's answer is a set of certificates, and the caller seals with
/// them — the server never sees the audience. That is why the certificates are fetched from the core's own directory
/// rather than guessed from a name, and why a pasted certificate is sent to the core for its verdict first: the rule
/// "only attested hardware addresses a reader" is applied once, where enrolment applies it.
/// </para>
/// <para>
/// <b>A file is admitted only as its whole chain</b>: the certificate, its PIV attestation certificate and the token's
/// attestation certificate, in that order (the order the "Export certificate with its attestation" action writes).
/// A file without the chain is refused here, before anything is sealed to it — a wrong order is refused by the core
/// (the key it attests is not the certificate's).
/// </para>
/// </remarks>
public partial class AudienceDialog : Window
{
    private static readonly Regex PemCertificate = new(
        "-----BEGIN CERTIFICATE-----.+?-----END CERTIFICATE-----", RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly SimplArchiveApiClient? _api;
    private readonly List<FileCertificateRow> _fromFiles = [];
    private List<ReaderRow> _rows = [];

    /// <summary>The certificates the user chose; null when they cancelled.</summary>
    public IReadOnlyList<X509Certificate2>? Chosen { get; private set; }

    // Parameterless ctor for the XAML designer/loader only.
    public AudienceDialog() : this(null!)
    {
    }

    public AudienceDialog(SimplArchiveApiClient api)
    {
        _api = api;
        InitializeComponent();
        Opened += (_, _) => Safe.Fire(LoadAsync);
    }

    private async Task LoadAsync()
    {
        if (_api is null)
        {
            return;
        }

        StatusText.Text = Strings.Get("AudienceLoading");
        try
        {
            var readers = await _api.ReaderDirectory.GetAllAsync(SearchBox.Text?.Trim());
            if (readers is null)
            {
                StatusText.Text = Strings.Get("AudienceUnavailable");
                return;
            }

            _rows = [.. readers.Select(r => new ReaderRow(r))];
            ReaderList.ItemsSource = _rows;
            StatusText.Text = _rows.Count == 0
                ? Strings.Get("AudienceNone")
                : string.Format(Strings.Get("AudiencePeople"), _rows.Count);
        }
        catch (Exception e)
        {
            DesktopLog.Warn(e, "The reader directory could not be read");
            StatusText.Text = Strings.Get("AudienceUnavailable");
        }
    }

    private void OnSearch(object? sender, RoutedEventArgs e) => Safe.Fire(LoadAsync);

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Safe.Fire(LoadAsync);
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSealButton();

    private void UpdateSealButton() =>
        SealButton.IsEnabled = (ReaderList.SelectedItems?.Count ?? 0) > 0 || _fromFiles.Count > 0;

    private void OnAddFile(object? sender, RoutedEventArgs e) => Safe.Fire(AddFileAsync);

    private async Task AddFileAsync()
    {
        if (_api is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.Get("AudienceAddFile"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PEM") { Patterns = ["*.pem", "*.crt", "*.cer", "*.txt"] }],
        });
        if (files.Count != 1)
        {
            return;
        }

        string text;
        await using (var stream = await files[0].OpenReadAsync())
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            text = await reader.ReadToEndAsync();
        }

        var blocks = PemCertificate.Matches(text).Select(m => m.Value.Trim()).ToList();
        if (blocks.Count < 3)
        {
            StatusText.Text = Strings.Get("AudienceFileNeedsChain");
            return;
        }

        // The chain is certificate, its attestation certificate, then the token's — the order the export writes.
        var evidence = new EnrolledCertificatesClient.KeyAttestation(
            "piv", blocks[1], string.Join("\n", blocks.Skip(2)), Slot: null);

        try
        {
            using var response = await _api.ReaderDirectory.ValidateAsync(blocks[0], evidence);
            if (response is null)
            {
                StatusText.Text = Strings.Get("AudienceUnavailable");
                return;
            }

            await ApiCore.ThrowIfProblemAsync(response, Strings.Get("AudienceFileRefused"), CancellationToken.None);
            var certificate = ReaderDirectoryClient.ParseValidation(
                await response.Content.ReadFromJsonAsync<JsonElement>());

            var leaf = X509Certificate2.CreateFromPem(certificate.CertificatePem);
            _fromFiles.Add(new FileCertificateRow(certificate.Subject, leaf));
            FileList.ItemsSource = null;
            FileList.ItemsSource = _fromFiles;
            StatusText.Text = string.Format(Strings.Get("AudienceFileAdded"), certificate.Subject);
            UpdateSealButton();
        }
        catch (ApiActionException refused)
        {
            // The core's codes are translated (ApiErrorText), so a broken chain, another key's evidence and an
            // untrusted root send the user to three different places.
            StatusText.Text = refused.Message;
        }
    }

    private void OnSeal(object? sender, RoutedEventArgs e)
    {
        var chosen = new List<X509Certificate2>();
        foreach (var row in ReaderList.SelectedItems?.OfType<ReaderRow>() ?? [])
        {
            // A person is sealed to on EVERY usable key they hold (ADR 0842), not the first one.
            chosen.AddRange(row.Certificates.Select(c => X509Certificate2.CreateFromPem(c.CertificatePem)));
        }

        chosen.AddRange(_fromFiles.Select(f => f.Certificate));
        if (chosen.Count == 0)
        {
            return;
        }

        Chosen = chosen;
        Close(chosen);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);

    /// <summary>
    /// Synthetic rows for the headless render — <c>--audience-screenshot --rows</c>. A Window can be looked at in no
    /// other way, and the empty state would pass while the populated one (two readers, one wrapping label, a
    /// certificate added from a file) is what the user actually meets.
    /// </summary>
    internal void FillForShot()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=external@example.test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var pem = selfSigned.ExportCertificatePem();

        _rows =
        [
            new ReaderRow(new ReaderDirectoryClient.Reader(Guid.NewGuid(), "Anna Beispiel",
            [
                new ReaderDirectoryClient.Certificate("YubiKey 5C NFC", "CN=anna@example.test", "CN=Test CA",
                    "1A2B3C", "AABBCC", DateTimeOffset.UtcNow.AddYears(1), pem),
            ])),
            new ReaderRow(new ReaderDirectoryClient.Reader(Guid.NewGuid(), "Tom Muster",
            [
                new ReaderDirectoryClient.Certificate("YubiKey 5C", "CN=tom@example.test", "CN=Test CA",
                    "4D5E6F", "DDEEFF", DateTimeOffset.UtcNow.AddYears(2), pem),
                new ReaderDirectoryClient.Certificate("Notebook token", "CN=tom@example.test", "CN=Test CA",
                    "708192", "112233", DateTimeOffset.UtcNow.AddMonths(6), pem),
            ])),
        ];
        ReaderList.ItemsSource = _rows;
        ReaderList.SelectedIndex = 0;

        _fromFiles.Add(new FileCertificateRow("CN=external@example.test", X509Certificate2.CreateFromPem(pem)));
        FileList.ItemsSource = _fromFiles;

        StatusText.Text = string.Format(Strings.Get("AudiencePeople"), _rows.Count);
        UpdateSealButton();
    }
}
