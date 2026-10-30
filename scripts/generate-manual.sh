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
# SOURCE_DATE_EPOCH still pins the PDF's internal CreationDate/ModDate and the copyright year (ADR 0510's
# metadata half). The VISIBLE stamp below is the release version (the private repo carries the v* tags at
# the mirrored commits — see docs/deploy/release.md) plus the generation DATE only (ADR 0817, corrected):
# date-only means same-day regenerations stay byte-identical, so unchanged content commits at most once a
# day, and the version changes only at releases.
VERSION="$(git describe --tags --abbrev=0 2>/dev/null || true)"
STAMP="${VERSION:+$VERSION · }generated $(date -u +%F)"
# The product's name is ONE value, the brand file's (ADR 0989): the manual reads the same one as the build.
PRODUCT="$(python3 -c 'import json; print(json.load(open("brand/brand.json"))["name"])')"
[ -n "$PRODUCT" ] || { echo "no name in brand/brand.json" >&2; exit 1; }
SOURCE_DATE_EPOCH=1780304400 typst compile manual/manual.typ "$pdf_out" --root . --font-path brand/assets/fonts --input generated="$STAMP" --input product="$PRODUCT"
# The German manual (manual.de.typ, ADR 1011), beside it under -de. Its figures are the English ones until the capture
# also runs in German.
SOURCE_DATE_EPOCH=1780304400 typst compile manual/manual.de.typ "${pdf_out%.pdf}-de.pdf" --root . --font-path brand/assets/fonts --input generated="$STAMP" --input product="$PRODUCT"

echo "==> Done. Served at /download/manual/ (browse /download or open $pdf_out)."
