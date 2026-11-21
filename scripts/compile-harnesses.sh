#!/usr/bin/env bash
#
# compile-harnesses.sh [--site <out.pdf>] — the harness landscape (#2397) from docs/harnesses/harnesses.typ.
#
# Without arguments: the canonical document, docs/harnesses/harnesses.pdf, committed. With --site: the WEBSITE
# EXTRACT — the same source compiled with `--input site=true`, which leaves out the section marked not-portable (the
# private tools; tools/ never reaches the public mirror, ADR 0484). One source, so the figure and the prose cannot
# drift between the two.
#
# Reproducible: dated by the last commit touching the source, never by the clock (SOURCE_DATE_EPOCH), and typeset in
# the brand's document typeface from brand/assets/fonts, as the user manual is (ADR 0991).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
source scripts/brand.sh

src=docs/harnesses/harnesses.typ
SOURCE_DATE_EPOCH="$(git log -1 --format=%ct -- "$src" 2>/dev/null || true)"
export SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-0}"

common=(--root . --font-path brand/assets/fonts --input "product=$BRAND_NAME")

if [[ "${1:-}" == --site ]]; then
  out="${2:?--site needs the output PDF path}"
  mkdir -p "$(dirname "$out")"
  typst compile "${common[@]}" --input site=true "$src" "$out" >&2
  # What the extract actually rendered, asked of the same source: site-bundle.py refuses to attach an extract that
  # carries a non-portable section, and a bundle carrying one fails validation.
  typst eval 'query(<harness-section>).map(it => it.value.id)' --in "$src" --root . \
    --font-path brand/assets/fonts --input "product=$BRAND_NAME" --input site=true > "$out.sections.json"
else
  out=docs/harnesses/harnesses.pdf
  typst compile "${common[@]}" "$src" "$out" >&2
fi

echo "$out"
