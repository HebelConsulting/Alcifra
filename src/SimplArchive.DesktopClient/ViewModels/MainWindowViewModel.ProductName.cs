using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The product's name the window shows (core #1117): the build's (the ProductName build property) until the installation
/// names itself (Branding:ProductName, on its API root), and the build's again after sign-out, as the logon window shows
/// it for any server. A partial of its own: MainWindowViewModel.cs is on the 1000-line debt list and may only shrink.
/// </summary>
public partial class MainWindowViewModel
{
    [ObservableProperty] private string _productName = Strings.ProductName;

    // Signed in: the installation's name; every text that names the product says it from now on.
    private async Task ApplyProductNameAsync()
    {
        if (_api is not null && await _api.Core.GetProductNameAsync() is { } name)
        {
            Strings.ProductName = name;
            ProductName = name;
        }
    }

    // Signed out: back to the build's name.
    private void ForgetProductName()
    {
        Strings.ProductName = Strings.DefaultProductName;
        ProductName = Strings.DefaultProductName;
    }
}
