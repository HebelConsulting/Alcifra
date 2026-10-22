using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using SimplArchive.ApiClient.KeyTest;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// Renders <i>Test the hardware key</i> headlessly to PNG (ADR 0955):
/// <c>--key-test-screenshot &lt;out.png&gt; [--dark] [--failed] [--signed-out] [--before] [--expanded] [--lang &lt;code&gt;]</c>.
/// </summary>
/// <remarks>
/// A SAMPLE report, because a screenshot run has no card: this checks how the window RENDERS a finished test — the
/// verdict, the steps, the report and its switch — not what a key answers. <c>--failed</c> shows a refused PIN, the
/// case a support conversation starts from; <c>--signed-out</c> is the logon window's form, with no archive switch;
/// <c>--before</c> is the window before any test (instructions only, no verdict); <c>--expanded</c> opens the report,
/// which is collapsed by default (ADR 0962).
/// </remarks>
public static class KeyTestShot
{
    public static bool TryRun(string[] args)
    {
        var at = Array.IndexOf(args, "--key-test-screenshot");
        if (at < 0 || at + 1 >= args.Length)
        {
            return false;
        }

        var lang = Array.IndexOf(args, "--lang");
        if (lang >= 0 && lang + 1 < args.Length)
        {
            SimplArchive.Localization.Culture.Apply(args[lang + 1]);
        }

        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();
        if (args.Contains("--dark") && Application.Current is { } app)
        {
            app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        }

        var signedIn = !args.Contains("--signed-out");
        var test = new HardwareKeyTest(new NoKeyPort(), _ => Task.FromResult<string?>(null), null, ClientUpdate.RunningVersion);
        var model = new KeyTestViewModel(test, signedIn);
        if (!args.Contains("--before"))
        {
            model.Show(Sample(failed: args.Contains("--failed"), signedIn));
        }

        model.IsReportExpanded = args.Contains("--expanded");

        var window = new KeyTestWindow(model);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(args[at + 1]);
        return true;
    }

    private static KeyTestReport Sample(bool failed, bool signedIn)
    {
        const string key = "YubiKey PIV #36771502, serial 36771502";
        var report = new KeyTestReportBuilder();
        report.Environment(KeyTestEnvironment.ThisMachine(
            HardwareKeyTest.AppName, ClientUpdate.RunningVersion,
            new KeyTestLibrary("/opt/homebrew/lib/libykcs11.dylib", "2.7", "Yubico (www.yubico.com)",
                "PKCS#11 PIV Library (SP-800-73)", "2.40"),
            [new KeyTestReader("Yubico YubiKey OTP+FIDO+CCID", true)]));
        report.Key(new KeyTestKey("YubiKey PIV #36771502", "Yubico (www.yubico.com)", "YubiKey YK5", "5.7 (hardware 1.0)",
            "36771502", ["Public key for PIV Authentication", "Public key for Key Management"],
            [new KeyTestCertificate("X.509 Certificate for Key Management", "CN=Anna Example", "CN=Example Issuing CA",
                "EC P-256", DateTimeOffset.UtcNow.AddMonths(-2), DateTimeOffset.UtcNow.AddYears(2),
                "3f0c9d2e7a51b8c4d6e9f0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5", Tested: true)]));
        report.Step(KeyTestStepKind.FindLibrary, KeyTestOutcome.Passed, "/opt/homebrew/lib/libykcs11.dylib");
        report.Step(KeyTestStepKind.ListReaders, KeyTestOutcome.Passed, "1 reader(s), 1 with a key in it.");
        report.Step(KeyTestStepKind.ReadKey, KeyTestOutcome.Passed, "1 certificate(s) on the key.", key);
        report.Step(KeyTestStepKind.ChooseCertificate, KeyTestOutcome.Passed, "\"X.509 Certificate for Key Management\", EC P-256.", key);
        report.Step(KeyTestStepKind.Seal, KeyTestOutcome.Passed, "Elliptic-curve key agreement, 32 random bytes.", key);
        if (failed)
        {
            report.Step(KeyTestStepKind.Unlock, KeyTestOutcome.Failed,
                "The PIN was not accepted. Mind the retry counter: a few wrong entries block the PIN.", key,
                "CKR_PIN_INCORRECT (0x000000A0) in C_Login");
        }
        else
        {
            report.Step(KeyTestStepKind.Unlock, KeyTestOutcome.Passed, "The PIN was accepted.", key);
            report.Step(KeyTestStepKind.Open, KeyTestOutcome.Passed, "32 bytes opened.", key);
            report.Step(KeyTestStepKind.Compare, KeyTestOutcome.Passed, "What the key opened is exactly what was sealed.", key);
        }

        if (signedIn)
        {
            report.Archive(new KeyTestArchiveSide("https://demo.simplarchive.dev", "0.48.0", "module", null,
                [new KeyTestEnrolment("3f0c9d2e7a51b8c4d6e9f0a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5", true)],
                new KeyTestEncryption("Strict", true, true, true, true), false, "Document", []));
        }

        return report.Build();
    }

    /// <summary>A port that is never asked: the screenshot shows a finished sample, it does not run the test.</summary>
    private sealed class NoKeyPort : IHardwareKeyPort
    {
        public Task<KeyTestLibrary?> FindLibraryAsync() => Task.FromResult<KeyTestLibrary?>(null);

        public Task<KeyPortSurvey> SurveyAsync() => Task.FromResult(new KeyPortSurvey([], [], []));

        public Task<byte[]> OpenAsync(KeyPortKey key, CardCertificates.Found certificate, byte[] sealedMessage, string pin) =>
            Task.FromResult(Array.Empty<byte>());
    }
}
