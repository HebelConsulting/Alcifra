using CommunityToolkit.Mvvm.ComponentModel;
using Alcifra.Localization;
using Alcifra.Theming;

namespace Alcifra.DesktopClient.ViewModels;

/// <summary>
/// The product's name the window shows (core #1117): the build's (the ProductName build property) until the installation
/// names itself (Branding:ProductName, on its API root), and the build's again after sign-out, as the logon window shows
/// it for any server. A white-label edition keeps its own throughout (ADR 1057). A partial of its own: MainWindowViewModel.cs is on the 1000-line debt list and may only shrink.
/// </summary>
public partial class MainWindowViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowsWordmark))] private string _productName = Strings.ProductName;

    /// <summary>
    /// The title bar shows a wordmark while one spells the product's name (ADR 0992) — the tenant's pack MARK where
    /// one was applied (ADRs 1103, 1105), the installation's own otherwise.
    /// </summary>
    public bool ShowsWordmark => Services.TenantBrand.HasPack
        ? Services.TenantBrand.ShowsWordmark
        : Brand.ShowsWordmark(ProductName);

    /// <summary>
    /// Whether the title bar writes the product's name as text. A tenant pack's mark names the TENANT, not the
    /// product, so the name stands beside it (ADR 1105); the installation's wordmark spells the name itself, so it
    /// stays alone. A tenant with no mark keeps the accent square and the name, as before.
    /// </summary>
    public bool ShowsBrandText => !ShowsWordmark || Services.TenantBrand.HasPack;

    // Signed in: the TENANT's runtime brand first (ADR 1103) — a pack names the product and marks it; only where the
    // tenant carries none does the installation's own name apply (core #1117).
    private async Task ApplyProductNameAsync()
    {
        if (_api is null)
        {
            return;
        }

        if (await Services.TenantBrand.ApplyAsync(_api))
        {
            ProductName = Strings.ProductName;
            OnPropertyChanged(nameof(ShowsWordmark));
            OnPropertyChanged(nameof(ShowsBrandText));
            return;
        }

        if (await _api.Core.GetProductNameAsync() is { } name)
        {
            Strings.ProductName = Brand.Shipped.NameOnServer(name);
            ProductName = Strings.ProductName;
        }
    }

    // Signed out: back to the build's name, and the tenant's mark and colours go with the session.
    private void ForgetProductName()
    {
        Services.TenantBrand.Forget();
        Strings.ProductName = Strings.DefaultProductName;
        ProductName = Strings.DefaultProductName;
        OnPropertyChanged(nameof(ShowsWordmark));
        OnPropertyChanged(nameof(ShowsBrandText));
    }
}
