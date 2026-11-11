#!/usr/bin/env bash
# manual-variants.sh [surface…] — one manual per palette variant the brand carries (#2319): <fileStem>-Manual-<id>.pdf beside
# the default one, captured in that palette. A brand without colorVariants builds nothing here.
#
# Each variant starts from a copy of every committed figure (some are not captured by this harness), and its own
# captures, English and German, overwrite what they cover. The surfaces are the harness's flags, as the default capture
# was given them (default: --desktop --web); --desktop-live goes last, so its figures overwrite the fixture's.
# Prints the PDFs it wrote (English, then German), one per line, for a caller to collect. Expects the capture harness built.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
surfaces=("$@"); [[ ${#surfaces[@]} -gt 0 ]] || surfaces=(--desktop --web)
variants="$(python3 -c 'import json,sys; print(" ".join(json.load(open("brand/brand.json")).get("colorVariants", {}).keys()))')"
source scripts/brand.sh
for variant in $variants; do
  work="$(mktemp -d)"
  cp -R manual/screenshots "$work/screenshots"
  for lang in en de; do
    out="$work/screenshots"; [[ "$lang" == de ]] && out="$work/screenshots/de"
    for surface in "${surfaces[@]}"; do
      dotnet run --project tests/Alcifra.ManualCapture --no-build -- "$surface" --lang "$lang" --variant "$variant" --out "$out" >&2
    done
  done
  pdf="src/Alcifra.Api/wwwroot/download/manual/$BRAND_FILE_STEM-Manual-$variant.pdf"
  scripts/compile-manual.sh --variant "$variant" --screenshots "$work/screenshots" "$pdf" >&2
  rm -rf "$work"
  echo "$pdf"
  echo "${pdf%.pdf}-de.pdf"
done
