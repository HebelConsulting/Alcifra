#!/usr/bin/env bash
#
# compile-installation-manual.sh [--white-label <version>] — the installation manual from docs/deploy/installation-manual.typ,
# one source for two manuals (owner, 2026-11-08). Without arguments: the full manual, docs/deploy/installation-manual.pdf,
# committed. With --white-label: this edition's operator manual, named from the brand (scripts/brand.sh) as
# dist/<fileStem>-Installation-Manual.pdf, for its release; its name ends in -Manual.pdf, so the edition publish
# delivers it to the customer's download page beside the user manual. Prints the PDF's path.
#
# Reproducible: dated by the last commit touching the manual's sources, never by the clock. Uses the system's
# fonts, as the committed PDF always has (Helvetica on the macOS release runner and on a Mac).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
source scripts/brand.sh

src=docs/deploy/installation-manual.typ
SOURCE_DATE_EPOCH="$(git log -1 --format=%ct -- "$src" docs/deploy/installation 2>/dev/null || true)"
export SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-0}"

if [[ "${1:-}" == "--white-label" ]]; then
  version="${2:?the release version, e.g. 0.60.2}"
  version="${version#v}"
  mkdir -p dist
  out="dist/$BRAND_FILE_STEM-Installation-Manual.pdf"
  typst compile --input edition=white-label --input "product=$BRAND_NAME" --input "registry=$BRAND_DELIVERY_REGISTRY" \
    --input "chart=$BRAND_CHART_NAME" --input "image=$BRAND_IMAGE_NAME" --input "version=$version" "$src" "$out" >&2
else
  out=docs/deploy/installation-manual.pdf
  typst compile "$src" "$out" >&2
fi
echo "$out"
