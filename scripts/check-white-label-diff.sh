#!/usr/bin/env bash
# A white-label edition differs from the product it is made from ONLY in brand/ (#1117 round 4, ADR 0996).
#
#   scripts/check-white-label-diff.sh <upstream ref>     e.g. origin/upstream
#
# The fork keeps the upstream main it last merged as its own branch `upstream`, pushed at every sync:
#
#   git fetch upstream && git push origin upstream/main:upstream && git merge upstream/main
#
# so CI compares without a credential for the private upstream. Exits 1, naming each file, when anything the
# edition changed on top of that lies outside brand/.
#
# One change outside brand/ is allowed: DELETING .github/dependabot.yml (ADR 0997). An edition takes its updates by
# merging upstream; its own update PRs would only fail this check.
set -euo pipefail
base="${1:?usage: scripts/check-white-label-diff.sh <upstream ref>}"
git rev-parse --verify --quiet "$base^{commit}" >/dev/null \
  || { echo "error: $base does not exist. Push the merged upstream main as the branch 'upstream' (see this script's header)." >&2; exit 1; }

outside=$(git diff --name-status "$base...HEAD" -- . ':(exclude)brand/**' \
  | { grep -vxF "$(printf 'D\t.github/dependabot.yml')" || true; } | cut -f2-)
if [ -n "$outside" ]; then
  echo "This edition changes files outside brand/ (ADR 0996); make the change upstream instead:" >&2
  printf '  %s\n' $outside >&2
  exit 1
fi
echo "Only brand/ differs from $base."
