using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SimplArchive.ApiClient.PasswordStore;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Views;

// One clashing name of an import (ADR 0968): skip it, add it as a new version of the entry already there, or keep both.
// ShowDialog<PasswordImportResolution?> answers the choice, or null when the whole import is cancelled.
public partial class PasswordConflictDialog : Window
{
    public PasswordConflictDialog()
    {
        InitializeComponent();
    }

    public PasswordConflictDialog(PasswordImportConflict conflict) : this() =>
        MessageBlock.Text = string.Format(CultureInfo.CurrentCulture, Strings.Get("PwImportDuplicate"), conflict.Existing.Name, conflict.Draft.Content.UserName ?? string.Empty);

    private void OnSkip(object? sender, RoutedEventArgs e) => Close(PasswordImportResolution.Skip);

    private void OnNewVersion(object? sender, RoutedEventArgs e) => Close(PasswordImportResolution.AddAsNewVersion);

    private void OnKeepBoth(object? sender, RoutedEventArgs e) => Close(PasswordImportResolution.KeepBoth);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
