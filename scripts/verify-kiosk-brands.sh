#!/usr/bin/env bash
# Verify the kiosk's SHOWCASE BRANDS after a rollout (ADR 1103/1104), in the family of verify-kiosk-*.sh:
#
#   1. every showcase login signs in — <brand>.admin@<domain>, created by tools/kiosk/brands-seed.sh after a reset;
#   2. the ANONYMOUS surfaces carry no tenant brand — the brand endpoints refuse without a token and no pack id
#      appears on the sign-in page or the root.
#
# Unlike verify-kiosk-logins.sh, its input is NOT the public README: these logins are deliberately not advertised
# there — the kiosk's operators hand them out. The passwords come from the kiosk's own .env, by the same key
# brands-seed.sh reads.
#
# Usage:  scripts/verify-kiosk-brands.sh [base-url] [env-file]
#   default base-url: https://demo.alcifra.dev
#   default env-file: $BRANDS_ENV_FILE, else /opt/alcifra/.env when readable (run it ON the kiosk)
#   or export SHOWCASE_BUND_PASSWORD / SHOWCASE_EASYNAME_PASSWORD / SHOWCASE_PFEIFFER_PASSWORD instead.
#
# Exit:   0 = every showcase login signed in and the anonymous surfaces are clean
#         1 = a login was rejected, or a tenant brand token appeared anonymously
#         2 = the site did not serve — nothing was learned

set -u

BASE_URL="${1:-https://demo.alcifra.dev}"
BASE_URL="${BASE_URL%/}"
ENV_FILE="${2:-${BRANDS_ENV_FILE:-/opt/alcifra/.env}}"
DOMAIN="${SHOWCASE_DOMAIN:-demo.alcifra.dev}"
PROBE="$(cd "$(dirname "$0")" && pwd)/probe-brand-anonymous.sh"

# local part : password key, matching tools/kiosk/brands-seed.sh's seed_brand lines.
ACCOUNTS=("bund:SHOWCASE_BUND_PASSWORD" "easyname:SHOWCASE_EASYNAME_PASSWORD" "pfeiffer:SHOWCASE_PFEIFFER_PASSWORD" "axpo:SHOWCASE_AXPO_PASSWORD")

# A value from .env as compose would read it (quotes stripped), like saconsole-session.sh's env_value.
env_value() {
  [ -r "$ENV_FILE" ] || return 0
  sed -n "s/^$1=//p" "$ENV_FILE" | head -1 | sed -e 's/^"\(.*\)"$/\1/' -e "s/^'\(.*\)'\$/\1/"
}

password_for() {
  local key=$1
  local from_env
  from_env="$(printenv "$key" 2>/dev/null || true)"
  if [ -n "$from_env" ]; then printf '%s' "$from_env"; else env_value "$key"; fi
}

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# PREFLIGHT — the same distinction verify-kiosk-logins.sh draws: a site that is not serving is not a bad password.
PRE_STATUS=$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 "$BASE_URL/Account/Login" 2>/dev/null)
case "$PRE_STATUS" in
  2*|3*) ;;
  *)
    echo "UNREACHABLE: $BASE_URL/Account/Login answered HTTP $PRE_STATUS, so no login was tested." >&2
    echo "A 502/503 here is the API container down or crash-looping, NOT a password problem." >&2
    exit 2
    ;;
esac

echo "Verifying the showcase logins (@$DOMAIN) against $BASE_URL"

FAILED=0
for account in "${ACCOUNTS[@]}"; do
  local_part="${account%%:*}"
  password_key="${account##*:}"
  email="${local_part}.admin@${DOMAIN}"
  password="$(password_for "$password_key")"
  if [ -z "$password" ]; then
    echo "  FAIL  $email — $password_key is not set (env or $ENV_FILE)" >&2
    FAILED=1
    continue
  fi

  JAR="$WORK/jar-$local_part.txt"
  rm -f "$JAR"

  if ! curl -fsS -c "$JAR" "$BASE_URL/Account/Login" -o "$WORK/login.html"; then
    echo "  ????  $email — could not fetch the login form; the site stopped responding mid-run" >&2
    exit 2
  fi

  TOKEN=$(grep -o '<input name="__RequestVerificationToken"[^>]*value="[^"]*"' "$WORK/login.html" \
    | head -1 | sed 's/.*value="//; s/"$//')
  if [ -z "$TOKEN" ]; then
    echo "  FAIL  $email — no antiforgery token on the login page" >&2
    FAILED=1
    continue
  fi

  curl -sS -b "$JAR" -D "$WORK/headers.txt" -o /dev/null -X POST "$BASE_URL/Account/Login" \
    -H "Referer: $BASE_URL/Account/Login" \
    --data-urlencode "Input.Email=$email" \
    --data-urlencode "Input.Password=$password" \
    --data-urlencode "__RequestVerificationToken=$TOKEN" >/dev/null 2>&1

  # Success is a 302 that SETS THE AUTH COOKIE — a re-rendered form and a localised error both look like a 200.
  if grep -qi '^HTTP/[0-9.]* 302' "$WORK/headers.txt" && grep -qi 'set-cookie: *\.AspNetCore\.Cookies=' "$WORK/headers.txt"; then
    echo "  ok    $email"
  else
    STATUS=$(head -1 "$WORK/headers.txt" | tr -d '\r')
    echo "  FAIL  $email — the showcase login did not sign in ($STATUS)" >&2
    echo "        Usual causes: the reset's brands seed did not run (check /var/log/kiosk-brands-seed.log)," >&2
    echo "        or SHOWCASE_*_PASSWORD in .env differs from the one here." >&2
    FAILED=1
  fi
done

# The legal rule, made structural: no tenant brand over an anonymous connection. The probe is the SAME one the
# weekly workflow runs; its tokens are the pack ids (specific enough not to match ordinary German prose).
if [ -x "$PROBE" ]; then
  if ! PROBE_BASE_URL="$BASE_URL" BRAND_PROBE_TOKENS="easyname,bund,pfeiffer-textil,axpo" "$PROBE"; then
    FAILED=1
  fi
else
  echo "  ????  $PROBE is missing — the anonymous-brand line was NOT checked" >&2
  exit 2
fi

if [ "$FAILED" != "0" ]; then
  exit 1
fi

echo "Every showcase login signs in, and the anonymous surfaces carry no tenant brand."
