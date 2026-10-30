using Microsoft.Extensions.Logging;
using Alcifra.ConsoleLogging;
using Alcifra.ManualCapture;

// The user-manual screenshot-capture harness (ADR 0502). Emits a deterministic set of PNGs the Typst manual
// references by stable name, so the manual is always in step with the real UI.
//
//   dotnet run --project tests/Alcifra.ManualCapture -- [--desktop] [--web] [--out <dir>]
//
//   --desktop        capture the Avalonia desktop screens from the FIXTURE (cheap, self-contained — the PR gate)
//   --desktop-live    capture the desktop screens classified Capture.Live against the real seeded app (#1358;
//                     Testcontainers, no Chrome; main only). What the PUBLISHED manual uses for those screens.
//   --web            capture the Blazor web screens (heavy — Testcontainers + Chrome; main only)
//   (neither)   capture both
//   --out <dir> output directory (default: manual/screenshots, or manual/screenshots/<lang> for another language)
//   --lang <code>  the app's language: en (default) or de, for the German manual's figures (ADR 1011)
//
// Its progress goes through the shared plain logger to stderr (ADRs 0906/0911); it has no data output — the
// result is the PNGs. The factory is disposed before exit so the console logger's background writer flushes.

using var logging = PlainConsoleLogging.CreateFactory();
var log = logging.CreateLogger("manual-capture");

var desktopLive = args.Contains("--desktop-live");
// `--desktop` is a prefix of `--desktop-live`, so ask for the exact flag: Contains on the array is an exact
// match per element, but reading it as "the desktop path" when only --desktop-live was given would run the
// fixture capture too and OVERWRITE the live figure with the fixture one — silently, and in that order.
var desktop = args.Contains("--desktop");
var web = args.Contains("--web");
if (!desktop && !web && !desktopLive)
{
    desktop = web = true;
}

var langIndex = Array.IndexOf(args, "--lang");
var lang = langIndex >= 0 && langIndex + 1 < args.Length ? args[langIndex + 1] : "en";
// The harness looks up the labels it clicks in the app's strings, in the language it captures.
Alcifra.Localization.Culture.Apply(lang);

var outIndex = Array.IndexOf(args, "--out");
var outDir = outIndex >= 0 && outIndex + 1 < args.Length
    ? Path.GetFullPath(args[outIndex + 1])
    : lang == "en"
        ? Path.Combine(Paths.RepoRoot(), "manual", "screenshots")
        : Path.Combine(Paths.RepoRoot(), "manual", "screenshots", lang);
Directory.CreateDirectory(outDir);
log.LogInformation("[manual-capture] {Lang} output → {OutDir}", lang, outDir);

if (desktop)
{
    await DesktopCapture.RunAsync(outDir, log, lang);
}

// AFTER the fixture pass, deliberately: where both run, the live figure is the one that must survive, and the
// published manual's screens are the live ones.
if (desktopLive)
{
    await LiveDesktopCapture.RunAsync(outDir, log, lang);
}

if (web)
{
    await WebCapture.RunAsync(outDir, log, lang);
}

log.LogInformation("[manual-capture] done.");
