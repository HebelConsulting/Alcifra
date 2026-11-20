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
/// A SAMPLE report, because this path has no server: the manual's six invented keys (fixed, so the figure is
/// byte-stable between regenerations) held by the demo tenant's people, two of them marked as no longer in the
/// audience, and Re-seal offered. The first recipient is expanded, so the details level shows too. The live capture
/// (#2314) overwrites this figure on main with the server's own answer.
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

        var window = new SealInspectionDialog(
            new SealInspectionViewModel(Sample(), mayReseal: true, SealKeyTrier.Of([])), "Seal of Board minutes 2026", "Board minutes 2026");
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

    // The six invented keys, ordered as the live envelope names them — the same cast the live figure shows, with the
    // two keys whose holder left the audience. Details mirror the server's own spellings, and dates are FIXED (the
    // certificates' validity), never "now": this figure is committed, and a moving date would churn it on every build.
    private static SealReport Sample() => new(
        SealedKind.Enveloped, "CMS EnvelopedData", SmimeWrapped: false, Size: 2_387, "pkcs7-data (1.2.840.113549.1.7.1)", "aes256 (2.16.840.1.101.3.4.1.42)",
        [
            Recipient("Lukas Brunner", "lukas@alcifra.local", "YubiKey 5C · 31 418 027", "CN=YubiKey 5C 31 418 027", "39D1F931D79D972A", inAudience: true),
            Recipient("Sofia Meier", "sofia@alcifra.local", "YubiKey 5Ci · 29 773 506", "CN=YubiKey 5Ci 29 773 506", "6F1D5267BEAEF486", inAudience: false),
            Recipient("Lukas Brunner", "lukas@alcifra.local", "YubiKey 5 NFC · 88 010 431", "CN=YubiKey 5 NFC 88 010 431", "311AE273B493587B", inAudience: true),
            Recipient("Sofia Meier", "sofia@alcifra.local", "YubiKey 5C NFC · 67 240 118", "CN=YubiKey 5C NFC 67 240 118", "3AA89DF3545860EA", inAudience: false),
            Recipient("Demo Admin", "admin@alcifra.local", "YubiKey 5 Nano · 12 805 662", "CN=YubiKey 5 Nano 12 805 662", "88F96F18E0ED41BD", inAudience: true),
            Recipient("Demo Admin", "admin@alcifra.local", "YubiKey 5C Nano · 44 193 907", "CN=YubiKey 5C Nano 44 193 907", "BAB5E4FCC432A7EB", inAudience: true),
        ],
        [], [],
        new SealReportAudience(false, []),
        [
            new Asn1Node(0, 0, 4, 2_383, true, "SEQUENCE", null),
            new Asn1Node(4, 1, 2, 9, false, "OBJECT IDENTIFIER", "1.2.840.113549.1.7.3 (envelopedData)"),
            new Asn1Node(15, 1, 4, 2_368, true, "[0]", null),
        ]);

    private static SealReportRecipient Recipient(
        string holder, string email, string label, string issuer, string serial, bool inAudience) => new(
        holder, email, label, RevokedAt: null, Attestation: "notAttested", NotAfter: new DateTimeOffset(2036, 6, 30, 0, 0, 0, TimeSpan.Zero),
        new CmsRecipient(issuer, serial, null), "key transport", "RSA (1.2.840.113549.1.1.1)", null, inAudience);
}
