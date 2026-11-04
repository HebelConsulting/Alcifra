using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;
using Alcifra.Localization;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// The headless check behind <c>--folder-picker-test &lt;apiBaseUrl&gt; &lt;token&gt;</c> (#2146): the real dialog, opened the
/// way Move opens it, with nobody loading it first, must show folders. A view model cannot see this: the defect was in who
/// loads the roots, and four callers forgot, so Move, Place reference and the certificate folder opened an empty picker.
/// </summary>
public static class FolderPickerCheck
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>Runs the check if the argument is ours; false means it belonged to somebody else.</summary>
    public static bool TryRun(string[] args)
    {
        var index = Array.IndexOf(args, "--folder-picker-test");
        if (index < 0 || index + 2 >= args.Length)
        {
            return false;
        }

        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithBrandFont()
            .SetupWithoutStarting();

        DesktopClientOptions.ApiBaseUrl = args[index + 1];
        var picker = new FolderPickerViewModel(new AlcifraApiClient(args[index + 2]), null, bulk: true, FolderPickerPurpose.Move);
        var owner = new LogonWindow();
        owner.Show();
        var dialog = new FolderPickerDialog { DataContext = picker };
        _ = dialog.ShowDialog<FilingResult?>(owner);

        var deadline = DateTime.UtcNow + Patience;
        while (picker.Roots.Count == 0 && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }

        DesktopLog.Info("The picker opened with {Count} root(s), unloaded by its caller", picker.Roots.Count);

        // Opened for a Move it says so: no "File" button, no comment nobody would post (#2146).
        var commit = dialog.FindControl<Button>("CommitButton")?.Content as string;
        var comments = dialog.GetVisualDescendants().OfType<TextBox>().Count(box => box.IsVisible);
        DesktopLog.Info("Titled {Title}, its button says {Commit}, {Comments} comment box(es) showing", dialog.Title, commit, comments);
        DesktopLog.Verdict(picker.Roots.Count > 0 && dialog.Title == picker.Title && commit == Strings.Get("FpCommitMove")
            && commit != Strings.Get("FpFile") && comments == 0);
        return true;
    }
}
