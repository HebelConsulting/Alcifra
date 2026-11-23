#!/usr/bin/env bash
#
# attach-harness-extract.sh <tag> [--run <id>] [--dry-run] — the harness landscape's extract onto a release's site bundle.
#
# A public release's site-bundle.zip cannot carry developers/harnesses.pdf by itself (#2435, ADR 1102): the extract is
# built only from docs/, which the public mirror withholds (ADR 0484), and at tag time the release job stamps the last
# successful manual build OF ITS OWN repository — one without docs/. This takes the canonical repository's manual
# bundle (the one WITH the extract), stamps it with the tag's version and the release's own notes, checks it, and
# replaces the asset in ONE upload. No body edit, no second run: a later body edit re-stamps the attached bundle
# through site-bundle-release.yml, which keeps the extract because stamping only touches version and notes.
#
#   ALCIFRA_SOURCE_REPO=<owner/name> scripts/attach-harness-extract.sh v0.62.8
#   ALCIFRA_SOURCE_REPO=<owner/name> scripts/attach-harness-extract.sh v0.62.8 --dry-run
#
# Run by hand from a canonical checkout, with gh authenticated for both repositories, AFTER the release's body is
# final. ALCIFRA_RELEASE_REPO defaults to HebelConsulting/Alcifra; ALCIFRA_SOURCE_REPO has no default — the canonical
# repository must be named, never guessed. Idempotent: a release whose attached bundle already carries the extract at
# this version is left alone, and --dry-run does everything but the upload.
set -euo pipefail

die() { echo "attach-harness-extract: $*" >&2; exit 1; }

tag="${1:-}"
[ -n "$tag" ] || die "usage: scripts/attach-harness-extract.sh <tag> [--run <id>] [--dry-run]"
shift
run=""
dry_run=false
while [ $# -gt 0 ]; do
  case "$1" in
    --run)    run="${2:?--run needs a manual-run id}"; shift 2 ;;
    --dry-run) dry_run=true; shift ;;
    *) die "unknown argument '$1'" ;;
  esac
done
[[ "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "'$tag' is not vMAJOR.MINOR.PATCH"

source_repo="${ALCIFRA_SOURCE_REPO:-}"
release_repo="${ALCIFRA_RELEASE_REPO:-HebelConsulting/Alcifra}"
[ -n "$source_repo" ] || die "set ALCIFRA_SOURCE_REPO to the canonical repository whose manual build carries docs/"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
version="${tag#v}"

gh release view "$tag" --repo "$release_repo" --json tagName >/dev/null 2>&1 \
  || die "the release '$tag' does not exist on $release_repo yet — attach after it is published"

# Already done? Ask the attached asset's manifest, not a guess: version and the developers list.
mkdir -p "$work/current"
if gh release download "$tag" --repo "$release_repo" --pattern site-bundle.zip --dir "$work/current" 2>/dev/null; then
  have="$(python3 -I - "$work/current/site-bundle.zip" <<'PY'
import json, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
    manifest = json.loads(z.read("manifest.json"))
print(manifest.get("version", "") + (" extract" if manifest.get("developers") else ""))
PY
)"
  case "$have" in
    "$version extract") echo "attach-harness-extract: $tag already carries the extract at $version — nothing to do" >&2; exit 0 ;;
  esac
fi

# The bundle, from the canonical repository's manual build — the only one that has the extract.
if [ -z "$run" ]; then
  run="$(gh run list --repo "$source_repo" --workflow manual.yml --branch main --status success --limit 1 \
    --json databaseId --jq '.[0].databaseId')" || true
fi
[ -n "$run" ] || die "no successful manual build on $source_repo's main — pass --run <id>"
echo "attach-harness-extract: the bundle from $source_repo's manual run $run" >&2
gh run download "$run" --repo "$source_repo" --name site-bundle --dir "$work"

bundle="$work/site-bundle.zip"
python3 -I - "$bundle" <<'PY' || die "that manual build's bundle carries no harness extract — take the run that followed the merge"
import json, sys, zipfile
with zipfile.ZipFile(sys.argv[1]) as z:
    manifest = json.loads(z.read("manifest.json"))
    developers = manifest.get("developers") or []
    missing = [d.get("pdf") for d in developers if d.get("pdf") not in z.namelist()]
    assert developers and not missing, (developers, missing)
print("attach-harness-extract: the extract: " + ", ".join(d["pdf"] for d in developers), file=sys.stderr)
PY

gh release view "$tag" --repo "$release_repo" --json body --jq '.body' > "$work/notes.md"
python3 -I "$repo_root/scripts/site-bundle.py" stamp --zip "$bundle" --version "$version" --notes "$work/notes.md" >&2
python3 -I "$repo_root/scripts/site-bundle.py" validate "$bundle" >&2

if $dry_run; then
  echo "attach-harness-extract: dry run — $tag would be replaced with this bundle; the asset is untouched" >&2
  exit 0
fi

gh release upload "$tag" "$bundle" --repo "$release_repo" --clobber
echo "attach-harness-extract: $tag's site-bundle.zip now carries the extract at $version" >&2
