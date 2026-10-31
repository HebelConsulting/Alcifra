#!/usr/bin/env bash
#
# Regenerate the Alcifra user manual (ADR 0502): capture fresh screenshots from the real app, then compile the
# Typst sources to the served PDF.
#
#   scripts/generate-manual.sh [--desktop-only]
#
#     (default)        capture BOTH the desktop (Avalonia, headless — cheap) and web (Blazor, via Testcontainers +
#                      Chrome — needs Docker + a system Google Chrome) screens, then compile.
#     --desktop-only   capture only the desktop screens, then compile against the committed web screenshots. This
#                      is the cheap PR-gate path (no Docker/Chrome).
#
# Outputs:
#   manual/screenshots/{desktop,web}-*.png          (regenerated screenshots)
#   src/Alcifra.Api/wwwroot/download/manual/<fileStem>-Manual.pdf   (the served manual, ADR 0997)
#
# Requires: the .NET SDK, `typst` on PATH, and (for the web capture) Docker + Google Chrome.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

mode="--desktop --web"
if [[ "${1:-}" == "--desktop-only" ]]; then
  mode="--desktop"
fi

if ! command -v typst >/dev/null 2>&1; then
  echo "error: 'typst' not found on PATH — install it (e.g. 'brew install typst' or see typst.app)." >&2
  exit 1
fi

source scripts/brand.sh
pdf_out="src/Alcifra.Api/wwwroot/download/manual/$BRAND_FILE_STEM-Manual.pdf"
mkdir -p manual/screenshots "$(dirname "$pdf_out")"

echo "==> Building the capture harness (and the app projects it drives)…"
dotnet build tests/Alcifra.ManualCapture/Alcifra.ManualCapture.csproj -clp:ErrorsOnly

echo "==> Capturing screenshots ($mode)…"
# shellcheck disable=SC2086
dotnet run --project tests/Alcifra.ManualCapture --no-build -- $mode --out manual/screenshots
# And in German, for the German manual's figures (ADR 1011).
dotnet run --project tests/Alcifra.ManualCapture --no-build -- $mode --lang de --out manual/screenshots/de

# A regenerated PNG whose every pixel is within ±1 of the committed one is raster noise, not a change (#832).
# Runs BEFORE the compile below, so neither the PNG nor the PDF that embeds it churns over nothing.
python3 scripts/keep-unchanged-screenshots.py manual/screenshots
python3 scripts/keep-unchanged-screenshots.py manual/screenshots/de

echo "==> Compiling the Typst manual → $pdf_out"
# The one compile (#2022, ADR 1016): "generated <date>"; only a release build names a version.
scripts/compile-manual.sh "$pdf_out"

echo "==> Done. Served at /download/manual/ (browse /download or open $pdf_out)."
