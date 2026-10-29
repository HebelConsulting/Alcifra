#!/usr/bin/env bash
# Restores a Alcifra backup (produced by scripts/backup.sh) into the local docker-compose stack. The
# Postgres/object-storage targets are configurable so a restore can be validated into a THROWAWAY database +
# bucket without clobbering live data (that's how the round-trip is verified). See docs/deploy/backup-dr.md
# for the DR runbook, restore order, and WORM/Object-Lock caveats.
#
# Usage: scripts/restore.sh <input_dir> [target_db] [bucket_suffix]
#   bucket_suffix: appended to every bucket's name, so a validation restore lands beside the live buckets
#   (e.g. "-restore-test") instead of into them.
set -euo pipefail

if [ $# -lt 1 ]; then
  echo "Usage: $0 <input_dir> [target_db] [bucket_suffix]" >&2
  exit 1
fi

# --- config (override via env) ---------------------------------------------------------------------------
PG_USER="${PG_USER:-postgres}"
S3_URL="${S3_URL:-http://seaweedfs:8333}"
S3_ACCESS_KEY="${S3_ACCESS_KEY:-storageadmin}"
S3_SECRET_KEY="${S3_SECRET_KEY:-storageadmin}"

cd "$(dirname "$0")/.."
SA_NETWORK="${SA_NETWORK:-$(docker compose config --format json | python3 -c 'import json,sys; print(json.load(sys.stdin)["name"])')_default}"
IN="$1"
TARGET_DB="${2:-simplarchive}"
BUCKET_SUFFIX="${3:-}"
IN_ABS="$(cd "$IN" && pwd)"
echo "Restoring from $IN_ABS  ->  db=$TARGET_DB buckets=<name>$BUCKET_SUFFIX"

# --- Postgres: (re)create the target DB, then restore ---------------------------------------------------
echo "==> Postgres ($TARGET_DB)"
# Separate -c invocations: DROP/CREATE DATABASE can't run inside a transaction block (a single -c with two
# statements would wrap them in one).
docker compose exec -T db psql -U "$PG_USER" -v ON_ERROR_STOP=1 \
  -c "DROP DATABASE IF EXISTS \"$TARGET_DB\" WITH (FORCE);" \
  -c "CREATE DATABASE \"$TARGET_DB\";" >/dev/null
docker compose exec -T db pg_restore -U "$PG_USER" -d "$TARGET_DB" --no-owner < "$IN/db.dump"
echo "    restored"

# --- Object storage: every bucket back, each recreated WITH object lock (it cannot be added afterwards) ---
# A backup from before #1987 holds one bucket in minio/; it is restored into the base bucket.
echo "==> Object storage"
AWS_ACCESS_KEY_ID="$S3_ACCESS_KEY" AWS_SECRET_ACCESS_KEY="$S3_SECRET_KEY" \
  docker run --rm --network "$SA_NETWORK" -e AWS_ACCESS_KEY_ID -e AWS_SECRET_ACCESS_KEY -e AWS_DEFAULT_REGION=us-east-1 \
    -e S3_URL="$S3_URL" -e SUFFIX="$BUCKET_SUFFIX" -v "$IN_ABS:/backup:ro" --entrypoint sh amazon/aws-cli:2.36.45 -c '
      set -eu
      s3() { aws --endpoint-url "$S3_URL" "$@"; }
      restore() {  # $1 = source directory, $2 = bucket
        s3 s3api head-bucket --bucket "$2" 2>/dev/null || s3 s3api create-bucket --bucket "$2" --object-lock-enabled-for-bucket >/dev/null
        s3 s3 sync --only-show-errors "$1" "s3://$2"
        echo "    $2: restored"
      }
      if [ -d /backup/buckets ]; then
        for dir in /backup/buckets/*/; do restore "$dir" "$(basename "$dir")$SUFFIX"; done
      else
        restore /backup/minio "simplarchive$SUFFIX"
      fi' 

# --- OpenBao: raft snapshot restore (production only; not the dev -dev server) ---------------------------
if [ -f "$IN/openbao.snap" ]; then
  echo "==> OpenBao"
  echo "    a snapshot exists; restore it against a raft-backed OpenBao with:"
  echo "      bao operator raft snapshot restore openbao.snap   # then unseal"
  echo "    (not applicable to the dev -dev server)."
fi

echo "Done."
