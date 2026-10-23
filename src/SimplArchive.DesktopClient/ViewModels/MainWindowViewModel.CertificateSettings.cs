using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The Tenant tab's "Reader certificates" group (ADR 0936): whether people enrol their own reader certificate, and where
/// the "Reader certificates" folder is created. Its own resource, saved whole with <c>If-Match</c> on the tenant's tag,
/// which it shares with the other groups, so a save of either re-reads the other.
/// </summary>
public partial class MainWindowViewModel
{
    private CertificateSettingsClient.CertificateSettings? _certificateSettings;

    /// <summary>Shown while a module licensing enrolment is active and the caller administers modules.</summary>
    [ObservableProperty] private bool _hasTenantCertificateSettings;
    [ObservableProperty] private bool _tenantCertSelfService;
    [ObservableProperty] private Guid? _tenantCertParentId;
    [ObservableProperty] private string _tenantCertParentName = string.Empty;
    [ObservableProperty] private bool _tenantCertUnattested;   // hardware without attestation allowed (ADR 0963)

    public bool IsEditingTenantCertificates => TenantEditingGroup == "certificates";

    /// <summary>Opens the folder picker; supplied by the view (a dialog belongs to a view that does not exist yet, ADR 0730).</summary>
    public Func<Task<Guid?>>? PickCertificateParentAsync { get; set; }

    private async Task LoadCertificateSettingsAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            ApplyCertificateSettings(await _api.CertificateSettings.GetAsync());
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
        {
            ApplyCertificateSettings(null);   // an installation that answers badly shows no group rather than a wrong one
        }
    }

    private void ApplyCertificateSettings(CertificateSettingsClient.CertificateSettings? settings)
    {
        _certificateSettings = settings;
        HasTenantCertificateSettings = settings is not null;
        TenantCertSelfService = settings?.SelfServiceEnabled ?? false;
        TenantCertParentId = settings?.ParentFolderId;
        TenantCertParentName = settings?.ParentFolderName ?? Strings.Get("TenantCertParentNone");
        TenantCertUnattested = settings?.UnattestedHardwareAllowed ?? false;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task ChooseCertificateParent()
    {
        if (PickCertificateParentAsync is not null && await PickCertificateParentAsync() is { } folderId)
        {
            // The name arrives with the next read; until then the id stands in, so the form shows that something changed.
            TenantCertParentId = folderId;
            TenantCertParentName = folderId.ToString();
        }
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ClearCertificateParent()
    {
        TenantCertParentId = null;
        TenantCertParentName = Strings.Get("TenantCertParentNone");
    }

    private async Task SaveCertificateSettingsGroupAsync()
    {
        if (_api is null || _certificateSettings is not { } read)
        {
            return;
        }

        try
        {
            await _api.CertificateSettings.SaveAsync(read with { SelfServiceEnabled = TenantCertSelfService, ParentFolderId = TenantCertParentId, UnattestedHardwareAllowed = TenantCertUnattested });
            await LoadTenantSettingsAsync();   // re-reads both: they share the tenant's tag
            Status = Strings.Get("StTenantSaved");
        }
        catch (SimplArchive.ApiClient.ApiActionException ex)
        {
            ReportError(ex.Message);
        }
    }
}
