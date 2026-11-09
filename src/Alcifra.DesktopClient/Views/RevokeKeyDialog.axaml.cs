using Avalonia.Controls;
using Avalonia.Interactivity;
using Alcifra.DesktopClient.Services;
using Alcifra.Localization;
using Alcifra.Presentation;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// Revoking one key (#2278, ADR 1083): what is revoked and what follows, why (one of the reasons, or Other in one's own
/// words), the advice to enrol the new key first, and a warning when it is the holder's only valid key. One POST to the
/// key's <c>revoke</c> rel. Closes with true once the key is revoked.
/// </summary>
public partial class RevokeKeyDialog : Window
{
    private readonly AlcifraApiClient _api;
    private readonly string _href;
    private string? _choice;

    // Parameterless ctor for the XAML designer/loader only.
    public RevokeKeyDialog() : this(null!, string.Empty, string.Empty, false)
    {
    }

    public RevokeKeyDialog(AlcifraApiClient api, string label, string href, bool onlyValidKey)
    {
        InitializeComponent();
        _api = api;
        _href = href;
        ConfirmText.Text = string.Format(Strings.Get("RevokeConfirm"), label);
        OnlyKeyText.IsVisible = onlyValidKey;
        foreach (var choice in CertificateRevocationReasons.Choices)
        {
            var radio = new RadioButton { GroupName = "reason", Content = Strings.Get(CertificateRevocationReasons.ChoiceKey(choice)) };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    _choice = choice;
                    OtherBox.IsVisible = choice == CertificateRevocationReasons.Other;
                    Refresh();
                }
            };
            ReasonsPanel.Children.Add(radio);
        }

        OtherBox.TextChanged += (_, _) => Refresh();
    }

    private string? Stored => CertificateRevocationReasons.Stored(_choice, OtherBox.Text);

    private void Refresh() => RevokeButton.IsEnabled = Stored is not null;

    private void OnRevoke(object? sender, RoutedEventArgs e) => Safe.Fire(async () =>
    {
        RevokeButton.IsEnabled = false;
        try
        {
            var refusal = await _api.EnrolledCertificates.RevokeAsync(_href, _choice!, OtherBox.Text?.Trim());
            if (refusal is null)
            {
                Close(true);
                return;
            }

            StatusText.Text = refusal.Length > 0 ? ApiErrorText.For(refusal) : Strings.Get("RevokeFailed");
        }
        catch (HttpRequestException ex)
        {
            DesktopLog.Warn(ex, "Revoking a key failed");
            StatusText.Text = Strings.Get("RevokeFailed");
        }

        Refresh();
    });

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
