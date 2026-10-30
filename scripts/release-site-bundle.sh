#!/usr/bin/env bash
#
# release-site-bundle.sh <tag> --from-manual-build | --from-release — a site bundle stamped for <tag>; prints its path.
#
# The website's input for a released version (ADR 1010, #2004):
#   --from-manual-build  at the release: the bundle the last successful manual build on main uploaded (manual.yml),
#                        stamped with the version. Its notes stay empty: the release body is written after the tag.
#   --from-release       when the release is edited (site-bundle-release.yml): the bundle already attached, stamped
#                        again with the version AND the release body as its notes, so the curated notes reach the site.
#
# Needs GH_TOKEN and GITHUB_REPOSITORY (as in a workflow), and python3.
set -euo pipefail

tag="${1:?usage: release-site-bundle.sh <tag> --from-manual-build | --from-release}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
bundle="$work/site-bundle.zip"

case "${2:-}" in
  --from-manual-build)
    run=$(gh run list --repo "$GITHUB_REPOSITORY" --workflow manual.yml --branch main --status success --limit 1 \
      --json databaseId --jq '.[0].databaseId')
    [ -n "$run" ] || { echo "::error::No successful manual build on main to take the site bundle from." >&2; exit 1; }
    gh run download "$run" --repo "$GITHUB_REPOSITORY" --name site-bundle --dir "$work" >&2
    python3 -I "$repo_root/scripts/site-bundle.py" stamp --zip "$bundle" --version "${tag#v}" >&2
    ;;
  --from-release)
    gh release download "$tag" --repo "$GITHUB_REPOSITORY" --pattern site-bundle.zip --dir "$work" >&2
    gh release view "$tag" --repo "$GITHUB_REPOSITORY" --json body --jq .body > "$work/notes.md"
    python3 -I "$repo_root/scripts/site-bundle.py" stamp --zip "$bundle" --version "${tag#v}" --notes "$work/notes.md" >&2
    ;;
  *)
    echo "usage: release-site-bundle.sh <tag> --from-manual-build | --from-release" >&2
    exit 2
    ;;
esac

python3 -I "$repo_root/scripts/site-bundle.py" validate "$bundle" >&2
echo "$bundle"
