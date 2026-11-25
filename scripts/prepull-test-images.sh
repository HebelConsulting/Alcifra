#!/usr/bin/env bash
# Pre-pull the five third-party images the end-to-end fixtures start, BEFORE the heavy tier runs its suites.
#
# WHY THIS EXISTS (2026-11-25). A pool VM is a fresh clone of the template, and the template is sealed WITHOUT
# test images (`docker image prune -af`, RunnerPool's template README) — so every heavy job pulls the fleet through
# the pool's registry mirror. That pull normally takes seconds. On 2026-11-25 one run lost three UI legs at fixture
# init with `DockerImageNotFoundException: No such image` — a DIFFERENT image on each VM (gotenberg 8.37 on one,
# opensearch 2.19.6 on another) — while sibling legs on the same pool, and a full matrix minutes earlier, pulled
# the same pins fine. Sixty-one opaque test failures per leg, and no docker error anywhere to say why.
#
# Pulling here first changes nothing about what the tests do, and turns that class of failure into ONE failing
# step carrying docker's own error message. One retry absorbs a mirror blip; a second failure fails the job by
# name, before any test runs.
#
# THE LIST mirrors the containers the fixtures start — tests/Alcifra.SelfHosting/SelfHostedApp.cs and
# tests/Alcifra.EndToEndTests/E2EApiFactory.cs, both reading images.env through ImagePins. A sixth image added
# there is still pulled by Testcontainers on demand; add it here only to keep its failure legible. The versions
# are NEVER written here: they come from images.env, the one file a bump edits.
#
#   scripts/prepull-test-images.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# images.env is KEY=value lines with comments — the same file the fixtures and the pin-lockstep check read.
# shellcheck disable=SC1091
. ./images.env

images=(
  "postgres:${POSTGRES_TAG}"
  "opensearchproject/opensearch:${OPENSEARCH_TAG}"
  "apache/tika:${TIKA_TAG}"
  "gotenberg/gotenberg:${GOTENBERG_TAG}"
  "chrislusf/seaweedfs@${SEAWEEDFS_DIGEST}"
)

for image in "${images[@]}"; do
  echo "prepull: $image"
  if ! docker pull --quiet "$image" >/dev/null; then
    echo "prepull: $image did not pull — retrying once in 10 s" >&2
    sleep 10
    docker pull --quiet "$image" >/dev/null
  fi
done

echo "prepull: ${#images[@]} images ready"
