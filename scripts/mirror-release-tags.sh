#!/usr/bin/env bash
# Tags THIS repository at the commit each public release was cut from (#2022, ADR 1016).
#
#   scripts/mirror-release-tags.sh [--dry-run]
#
# Releases are tagged on the public repository, at a mirror commit whose subject names the private commit it carries
# ("Sync from private @ <sha>"). That mapping used to be a runbook line, and it was skipped for three of five releases,
# silently. This reads every public v* tag, and for each one missing here tags the named commit (lightweight, as the
# runbook did) and pushes it. Idempotent: an existing tag is never moved.
# Needs `gh` (signed in, or GH_TOKEN) to read the public commit's subject, and push rights here.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

dry_run=false
[[ "${1:-}" == "--dry-run" ]] && dry_run=true
public_repo="HebelConsulting/Alcifra"

git fetch --quiet --tags origin
created=0
refused=()
while read -r public_sha ref; do
  tag="${ref#refs/tags/}"
  # An existing tag is never moved, and not even looked up: the earliest releases were tagged here directly, before
  # the mirror existed, so theirs legitimately differ from the public commit's.
  git rev-parse --quiet --verify "refs/tags/$tag" >/dev/null && continue

  subject="$(gh api "repos/$public_repo/commits/$public_sha" --jq '.commit.message' | head -1)"
  private_short="$(sed -nE 's/^Sync from private @ ([0-9a-f]{7,40})$/\1/p' <<<"$subject")"
  if [[ -z "$private_short" ]]; then
    echo "::warning::$tag: the public commit $public_sha does not name a private commit ('$subject'); not tagged here."
    continue
  fi

  if ! private_sha="$(git rev-parse --quiet --verify "$private_short^{commit}")"; then
    echo "::warning::$tag: the private commit $private_short is not in this repository; not tagged."
    continue
  fi

  if $dry_run; then
    echo "would tag $tag at $private_sha"
  else
    git tag "$tag" "$private_sha"
    # One refused push must not stop the rest: v0.1.0's commit once failed every run from the oldest tag on, so no
    # release after v0.54.0 was tagged here for days. The run still fails at the end, naming what was refused.
    if ! git push --quiet origin "refs/tags/$tag"; then
      git tag -d "$tag" >/dev/null
      echo "::error::$tag: the push of $private_sha was refused; not tagged."
      refused+=("$tag")
      continue
    fi
    echo "tagged $tag at $private_sha"
  fi
  created=$((created + 1))
done < <(git ls-remote --tags --refs "https://github.com/$public_repo.git" 'v*' | sed 's#\t# #')

echo "$created tag(s) $($dry_run && echo would be || echo were) added."
if (( ${#refused[@]} > 0 )); then
  echo "::error::${#refused[@]} tag(s) refused: ${refused[*]}"
  exit 1
fi
