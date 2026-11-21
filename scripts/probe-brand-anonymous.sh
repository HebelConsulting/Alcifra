#!/usr/bin/env bash
#
# probe-brand-anonymous.sh — the anonymous-surface brand probe (ADR 1103, #2408).
#
# The kiosk mounts tenant brand packs, and the legal rule is absolute: NO tenant brand may appear over an anonymous
# connection — no name, no logo, no wordmark — and the brand endpoint must refuse without a token. This fetches the
# anonymous surfaces of the deployed installation and fails when a forbidden token appears (findings go to
# brand-probe-findings.txt, which the workflow turns into an issue).
#
#   PROBE_BASE_URL      the installation to probe (default https://demo.alcifra.dev)
#   BRAND_PROBE_TOKENS  comma-separated brand names/pack ids that must never appear anonymously
#                       (default: easyname,bund — set it where the packs are)
#
# Deliberately NOT a browser test: it proves the wire, which is what the rule is about. Colours are not checked —
# the external-link landing may legitimately take a tenant's colours; logos never.
set -euo pipefail

base="${PROBE_BASE_URL:-https://demo.alcifra.dev}"
tokens="${BRAND_PROBE_TOKENS:-easyname,bund}"
findings="brand-probe-findings.txt"
: > "$findings"

fail() { echo "$1" >> "$findings"; }

# 1. The brand endpoints must REFUSE without a token — the legal rule made structural.
for path in /api/brand /api/brand/assets/wordmarkLight; do
  status="$(curl -s -o /dev/null -w '%{http_code}' "$base$path" || printf '000')"
  [ "$status" = "401" ] || fail "GET $path answered $status anonymously — expected 401 (no tenant brand without a token)."
done

# 2. The anonymous pages must carry no tenant brand token.
for path in / /Account/Login; do
  body="$(mktemp)"
  # -L: the root redirects to the sign-in page; a redirect chain still paints only anonymous content.
  status="$(curl -sL -o "$body" -w '%{http_code}' "$base$path" || printf '000')"
  if [ "$status" != "200" ]; then
    fail "GET $path answered $status anonymously — expected 200."
    rm -f "$body"
    continue
  fi
  IFS=',' read -r -a list <<< "$tokens"
  for token in "${list[@]}"; do
    [ -n "$token" ] || continue
    if grep -qi -- "$token" "$body"; then
      fail "GET $path carries the tenant brand token '$token' on an anonymous page."
    fi
  done
  rm -f "$body"
done

if [ -s "$findings" ]; then
  cat "$findings"
  exit 1
fi

echo "The anonymous surfaces carry no tenant brand token ($tokens) and the brand endpoints refuse without a token."
