using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Alcifra.ApiClient.Inspection;
using Alcifra.DesktopClient.Services;
using Alcifra.DesktopClient.ViewModels;
using Alcifra.SealedArchive;

namespace Alcifra.DesktopClient.Views;

/// <summary>
/// Renders <i>Inspect seal…</i> headlessly to PNG (#2291): <c>--seal-screenshot &lt;out.png&gt; [--dark] [--lang &lt;code&gt;]</c>.
/// </summary>
/// <remarks>
/// A SAMPLE report, because a screenshot run has no server: a known recipient with a revoked key, one that left the audience
/// and can still open this version, one nobody here knows, a member missing from the envelope, and Re-seal offered. The
/// first recipient is expanded, so the details level shows too.
/// </remarks>
public static class SealInspectionShot
{
    public static bool TryRun(string[] args)
    {
        var at = Array.IndexOf(args, "--seal-screenshot");
        if (at < 0 || at + 1 >= args.Length)
        {
            return false;
        }

        var lang = Array.IndexOf(args, "--lang");
        if (lang >= 0 && lang + 1 < args.Length)
        {
            Alcifra.Localization.Culture.Apply(args[lang + 1]);
        }

        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithBrandFont()
            .SetupWithoutStarting();
        if (args.Contains("--dark") && Application.Current is { } app)
        {
            app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        }

        var window = new SealInspectionDialog(new SealInspectionViewModel(Sample(), mayReseal: true, SealKeyTrier.Of([])), "Seal of Board pack", "Board pack");
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        if (window.GetVisualDescendants().OfType<Avalonia.Controls.Expander>().FirstOrDefault() is { } first)
        {
            first.IsExpanded = true;
            // The expander opens through a transition: the render timer is advanced so the frame shows it open.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(60);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        var rendered = window.CaptureRenderedFrame();
        rendered?.Save(args[at + 1]);
        DesktopLog.Verdict(rendered is not null);
        return true;
    }

    private static SealReport Sample() => new(
        SealedKind.Enveloped, "CMS EnvelopedData", SmimeWrapped: true, Size: 18_734, "1.2.840.113549.1.7.1 (data)", "2.16.840.1.101.3.4.1.42 (aes256-CBC)",
        [
            new SealReportRecipient("Anna Muster", "anna@example.test", "YubiKey 5C", DateTimeOffset.UtcNow.AddDays(-3), "attested", DateTimeOffset.UtcNow.AddYears(2),
                new CmsRecipient("CN=Example Issuing CA", "4F1A9C", null), "key agreement", "1.3.132.1.11.1 (dhSinglePass-stdDH-sha256kdf)", "id-ecPublicKey", true),
            new SealReportRecipient("Bob Keller", "bob@example.test", "Office key", null, "notAttested", DateTimeOffset.UtcNow.AddYears(1),
                new CmsRecipient("CN=Example Issuing CA", "5B22D0", null), "key transport", "1.2.840.113549.1.1.7 (RSAES-OAEP)", null, false),
            new SealReportRecipient(null, null, null, null, null, null,
                new CmsRecipient(null, null, "9F3C51A2B7"), "key transport", "1.2.840.113549.1.1.1 (rsaEncryption)", null, true),
        ],
        [], [],
        new SealReportAudience(false, [new SealReportMissing("Clara Weiss", "A1B2C3")]),
        [
            new Asn1Node(0, 0, 4, 18_730, true, "SEQUENCE", null),
            new Asn1Node(4, 1, 2, 9, false, "OBJECT IDENTIFIER", "1.2.840.113549.1.7.3 (envelopedData)"),
            new Asn1Node(15, 1, 4, 18_715, true, "[0]", null),
        ]);
}
