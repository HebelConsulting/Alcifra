#!/usr/bin/env bash
# Compiles the user manual, English and German, from manual/*.typ and the committed screenshots: the ONE compile
# every caller uses (generate-manual.sh, the manual workflow, the release image's build). Three copies of this line is
# how the title page's stamp went missing: the workflow's copy never passed it (#2022, ADR 1016).
#
#   scripts/compile-manual.sh [--release vX.Y.Z] [<english.pdf>]
#
# The title page reads "generated <UTC date>" ("erstellt am" in German), or with --release "vX.Y.Z · generated <date>". Only a release
# build names a version: main's manual is regenerated BEFORE the release is tagged, so a version read from git there
# would name the previous release (ADR 1016, correcting ADR 0817). The German manual lands beside the English one,
# under -de. The product's name is the brand file's (ADR 0989).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

release=""
if [[ "${1:-}" == "--release" ]]; then
  release="${2:?--release needs the tag, for example v0.56.0}"
  [[ "$release" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]] || { echo "not a release tag: $release" >&2; exit 1; }
  shift 2
fi

source scripts/brand.sh
pdf="${1:-src/Alcifra.Api/wwwroot/download/manual/$BRAND_FILE_STEM-Manual.pdf}"
mkdir -p "$(dirname "$pdf")"

# Date only, never the time of day: same-day regenerations stay byte-identical (ADR 0817). Each manual says it in its
# own language; the version and the date are the same in both.
today="$(date -u +%F)"
stamp_en="${release:+$release · }generated $today"
stamp_de="${release:+$release · }erstellt am $today"

# SOURCE_DATE_EPOCH pins the PDF's internal timestamps and the copyright year to the capture clock (ADR 0510).
for lang in en de; do
  source_typ="manual/manual.typ"; out="$pdf"; stamp="$stamp_en"
  [[ "$lang" == de ]] && { source_typ="manual/manual.de.typ"; out="${pdf%.pdf}-de.pdf"; stamp="$stamp_de"; }
  SOURCE_DATE_EPOCH=1780304400 typst compile "$source_typ" "$out" --root . --font-path brand/assets/fonts \
    --input generated="$stamp" --input product="$BRAND_NAME"
done

echo "Manual compiled ($stamp_en): $pdf and ${pdf%.pdf}-de.pdf"
