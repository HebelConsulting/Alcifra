using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// The headless check behind <c>--checkin-comment-test &lt;apiBaseUrl&gt; &lt;token&gt; &lt;documentId&gt;
/// &lt;comment | --cancel&gt;</c> (ADR 0952): the REAL check-in path — the window's handler opens
/// <see cref="CheckInCommentDialog"/>, the comment is typed into it and its button clicked — against a running Api.
/// </summary>
/// <remarks>
/// A view-model test cannot see this: the dialog is opened by the window, so only a real window proves the
/// prompt appears, that its answer reaches the request, and that Cancel aborts. The caller (the desktop UI
/// suite) asserts the stored outcome through the API; this reports OK when the window side behaved.
/// </remarks>
public static class CheckInCommentCheck
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>Runs the check if the argument is ours; false means it belonged to somebody else.</summary>
    public static bool TryRun(string[] args)
    {
        var index = Array.IndexOf(args, "--checkin-comment-test");
        if (index < 0 || index + 4 >= args.Length)
        {
            return false;
        }

        var (baseUrl, token, documentId, answer) = (args[index + 1], args[index + 2], Guid.Parse(args[index + 3]), args[index + 4]);
        var cancel = answer == "--cancel";

        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithBrandFont()
            .SetupWithoutStarting();

        DesktopClientOptions.ApiBaseUrl = baseUrl;
        var vm = new MainWindowViewModel();
        vm.Checkout.SetApi(new AlcifraApiClient(token));
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Pump(vm.Checkout.LoadAsync());
        if (vm.Checkout.Items.FirstOrDefault(i => i.Id == documentId) is not { } row)
        {
            DesktopLog.Warn("The document {DocumentId} is not in the Check-out tab", documentId);
            DesktopLog.Verdict(false);
            return true;
        }

        // The row menu's own route: the window's handler, handed THIS row as the sender's Tag.
        window.OnCheckoutCheckIn(new Border { Tag = row }, new RoutedEventArgs());
        var dialog = Until(() => window.OwnedWindows.OfType<CheckInCommentDialog>().FirstOrDefault());
        if (dialog is null)
        {
            DesktopLog.Warn("Check in opened no comment dialog");
            DesktopLog.Verdict(false);
            return true;
        }

        DesktopLog.Info("The comment dialog opened");
        if (cancel)
        {
            dialog.CommentBox.Text = "never sent";
            dialog.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var closed = Until(() => window.OwnedWindows.Contains(dialog) ? null : dialog) is not null;

            // Give an (unwanted) check-in the time it would need, then check the row is still held.
            Pump(Task.Delay(TimeSpan.FromSeconds(2)));
            Pump(vm.Checkout.LoadAsync());
            var stillHeld = vm.Checkout.Items.Any(i => i.Id == documentId);
            DesktopLog.Info("Cancel closed the dialog: {Closed}; still checked out: {StillHeld}", closed, stillHeld);
            DesktopLog.Verdict(closed && stillHeld);
            return true;
        }

        dialog.CommentBox.Text = answer;
        dialog.CheckInButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // The check-in reloads the tab when it is done; the row leaving is the sign it went through.
        var released = Until(() => vm.Checkout.Items.Any(i => i.Id == documentId) ? null : row) is not null;
        DesktopLog.Info("Checked in through the dialog: {Released}", released);
        DesktopLog.Verdict(released);
        return true;
    }

    // Runs the dispatcher until the task completes (or patience runs out).
    private static void Pump(Task task)
    {
        Until(() => task.IsCompleted ? task : null);
    }

    // Runs the dispatcher until the probe answers something, or patience runs out (null).
    private static T? Until<T>(Func<T?> probe)
        where T : class
    {
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (probe() is { } found)
            {
                return found;
            }

            Thread.Sleep(20);
        }

        return null;
    }
}
