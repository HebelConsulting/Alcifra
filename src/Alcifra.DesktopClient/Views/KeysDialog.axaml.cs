using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Alcifra.ApiClient;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// A person's enrolled keys, to see and revoke (#2278, ADR 1083): "My keys" from the account menu, or a user's from the
/// Users &amp; groups tab, whose <c>certificates</c> rel names the listing. Who may revoke which key is the server's answer
/// alone, as each key's <c>revoke</c> rel. The canonical surface (ADR 0511); the web's key list mirrors it.
/// </summary>
public partial class KeysDialog : Window
{
    private readonly AlcifraApiClient _api;
    private readonly string? _href;

    // Parameterless ctor for the XAML designer/loader only.
    public KeysDialog() : this(null!, null, null)
    {
    }

    /// <param name="href">A user's listing; null for the caller's own, from the root.</param>
    /// <param name="title">The window's title; null for "My keys".</param>
    public KeysDialog(AlcifraApiClient api, string? href, string? title)
    {
        InitializeComponent();
        _api = api;
        _href = href;
        if (title is not null)
        {
            Title = title;
        }

        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IReadOnlyList<EnrolledCertificatesClient.Enrolled> keys;
        try
        {
            keys = _href is null
                ? await _api.EnrolledCertificates.GetMineAsync()
                : await _api.EnrolledCertificates.GetAtAsync(_href);
        }
        catch (Exception e) when (e is HttpRequestException or System.Text.Json.JsonException)
        {
            DesktopLog.Warn(e, "Reading enrolled keys failed");
            StatusText.Text = Strings.Get("KeysLoadError");
            return;
        }

        KeysPanel.Children.Clear();
        StatusText.Text = keys.Count == 0 ? Strings.Get("KeysNone") : string.Empty;
        foreach (var key in keys)
        {
            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock { Text = key.Label, FontWeight = Avalonia.Media.FontWeight.SemiBold, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            var state = new TextBlock { Text = Describe(key), FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            state.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(key.Usable ? "WbMuted" : "WbWarning"));
            text.Children.Add(state);

            var row = new DockPanel { LastChildFill = true };
            if (key.RevokeHref is { } revoke)
            {
                var button = new Button { Content = Strings.Get("KeysRevoke"), VerticalAlignment = VerticalAlignment.Center };
                button.Click += (_, _) => Safe.Fire(() => RevokeAsync(key, revoke, keys));
                DockPanel.SetDock(button, Dock.Right);
                row.Children.Add(button);
            }

            row.Children.Add(text);
            KeysPanel.Children.Add(row);
        }
    }

    private async Task RevokeAsync(EnrolledCertificatesClient.Enrolled key, string href, IReadOnlyList<EnrolledCertificatesClient.Enrolled> keys)
    {
        // The only key that still reads for its holder: revoking it leaves them unable to open sealed items.
        var onlyValid = key.Usable && keys.Count(k => k.Usable && k.RevokedAt is null) == 1;
        if (await new RevokeKeyDialog(_api, key.Label, href, onlyValid).ShowDialog<bool>(this))
        {
            await LoadAsync();
            StatusText.Text = Strings.Get("StKeyRevoked");
        }
    }

    /// <summary>A key's state, or when and why it was revoked.</summary>
    internal static string Describe(EnrolledCertificatesClient.Enrolled key)
    {
        if (key.RevokedAt is { } revokedAt)
        {
            var on = revokedAt.InZone(SessionTimeZone.Current).DateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
            return key.RevocationReason is { Length: > 0 } reason
                ? string.Format(Strings.Get("KeysRevokedBecause"), on,
                    CertificateRevocationReasons.StoredKey(reason) is { } reasonKey ? Strings.Get(reasonKey) : reason)
                : string.Format(Strings.Get("KeysRevokedOn"), on);
        }

        var (textKey, namesRoot) = CertificateAttestationText.For(key.Attestation, key.Usable);
        var state = namesRoot ? string.Format(Strings.Get(textKey), key.AttestationRoot) : Strings.Get(textKey);
        return key.Attestation == "attested" && key.DeviceSerial is { Length: > 0 } serial
            ? $"{state}, {string.Format(Strings.Get("EnrolAttestedToken"), serial)}"
            : state;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
