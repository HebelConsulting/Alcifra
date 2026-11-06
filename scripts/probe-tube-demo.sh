#!/usr/bin/env bash
# Probe the deployed video host (core #2142): what the self-contained client check in the module's public repository
# cannot see. The edge, the certificate and the deployed version are only there, so this asks the live host the four
# things a client needs: the reported version, one video's details, its file's redirect, and a range on the file.
#
#   scripts/probe-tube-demo.sh [https://tube.demo.alcifra.dev]
#
# Video ids are discovered through the API, never typed: the demo's nightly reset changes them. Run outside the reset
# window (04:00 UTC). Exit 1 on the first finding, named on stderr; the findings file carries them for the workflow.
set -euo pipefail
HOST=${1:-https://tube.demo.alcifra.dev}
MIN_VERSION=7.2.0
FINDINGS=${PROBE_FINDINGS:-tube-probe-findings.txt}
: > "$FINDINGS"

finding() { echo "$*" | tee -a "$FINDINGS" >&2; exit 1; }
api() { curl -fsS --max-time 20 "$HOST/api/v1/$1" || finding "GET $HOST/api/v1/$1 did not answer 200"; }

version=$(api config | python3 -c 'import json,sys; print(json.load(sys.stdin)["serverVersion"])')
newest=$(printf '%s\n%s\n' "$MIN_VERSION" "${version%%-*}" | sort -V | tail -1)
[ "$newest" = "${version%%-*}" ] || finding "the host reports $version, below the app's minimum $MIN_VERSION"
echo "ok   version $version (app minimum $MIN_VERSION)"

uuid=$(api "videos?sort=-publishedAt&start=0&count=1" | python3 -c 'import json,sys; d=json.load(sys.stdin)["data"]; print(d[0]["uuid"] if d else "")')
[ -n "$uuid" ] || finding "the host lists no video"
file_url=$(api "videos/$uuid" | python3 -c 'import json,sys; d=json.load(sys.stdin); print(d["files"][0]["fileUrl"])')
echo "ok   details of $uuid, file at ${file_url%%\?*}"

status=$(curl -sS -o /dev/null --max-time 20 -w '%{http_code}' -I "$file_url")
[ "$status" = 302 ] || finding "HEAD on the file answered $status (expected 302)"
echo "ok   the file door answers 302"

status=$(curl -sS -o /dev/null --max-time 60 -L -w '%{http_code}' -H 'Range: bytes=0-99' "$file_url")
[ "$status" = 206 ] || finding "a range on the file answered $status after the redirect (expected 206)"
echo "ok   a range on the file answers 206"
echo "The deployed video host answers every probe."
