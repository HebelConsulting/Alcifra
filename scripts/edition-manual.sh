#!/usr/bin/env bash
#
# edition-manual.sh [--white-label | --stand-in] — make the served manual directory hold exactly this edition's
# manual, named <fileStem>-Manual.pdf (ADR 0997), and print its path.
#
# Upstream commits its manual, so the file is already there and only checked. A white-label edition does not commit
# its own (ADR 0996): it takes the PDF the last successful manual build on main uploaded, and removes the upstream
# manual its tree still carries, so the image and the release hold one manual — its own.
#
# --stand-in is for a white-label edition's TESTS (ADR 0998): its real manual is a build artifact, so its tree has
# only the upstream PDF. That PDF is copied to the edition's name, because the tests check serving and filing, not
# the PDF's content. Never committed, never in an image: the image build uses --white-label.
#
# Needs GH_TOKEN and GITHUB_REPOSITORY for --white-label (as in a workflow).
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
source scripts/brand.sh

dir="src/SimplArchive.Api/wwwroot/download/manual"
manual="$dir/$BRAND_FILE_STEM-Manual.pdf"

if [[ "${1:-}" == "--white-label" ]]; then
  run=$(gh run list --repo "$GITHUB_REPOSITORY" --workflow manual.yml --branch main --status success --limit 1 \
    --json databaseId --jq '.[0].databaseId')
  [ -n "$run" ] || { echo "::error::No successful manual build on main to take this edition's manual from." >&2; exit 1; }
  fetched="$(mktemp -d)"
  gh run download "$run" --repo "$GITHUB_REPOSITORY" --name manual --dir "$fetched" >&2
  find "$dir" -maxdepth 1 -name '*-Manual.pdf' -delete
  cp "$fetched"/*.pdf "$manual"
elif [[ "${1:-}" == "--stand-in" && ! -f "$manual" ]]; then
  committed=$(find "$dir" -maxdepth 1 -name '*-Manual.pdf' | head -1)
  [ -n "$committed" ] || { echo "::error::No committed manual in $dir to stand in for $manual." >&2; exit 1; }
  cp "$committed" "$manual"
fi

[ -f "$manual" ] || { echo "::error::$manual is missing." >&2; exit 1; }
echo "$manual"
