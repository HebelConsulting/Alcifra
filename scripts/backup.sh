#!/usr/bin/env bash
# Backs up Alcifra's three stateful stores (Postgres, object storage, OpenBao) from the local
# docker-compose stack into a timestamped directory. First slice of the backup/DR story
# (docs/adr/0344-backup-and-disaster-recovery.md); see docs/deploy/backup-dr.md for the runbook and how to
# adapt these commands for a managed/production deployment (point the same tools at the real endpoints).
#
# Usage: scripts/backup.sh [output_dir]
set -euo pipefail

# --- config (override via env) ---------------------------------------------------------------------------
PG_USER="${PG_USER:-postgres}"
PG_DB="${PG_DB:-alcifra}"
# The S3 endpoint and the identity scripts/seaweedfs-s3.json gives the dev stack.
S3_URL="${S3_URL:-http://seaweedfs:8333}"
S3_ACCESS_KEY="${S3_ACCESS_KEY:-storageadmin}"
S3_SECRET_KEY="${S3_SECRET_KEY:-storageadmin}"

cd "$(dirname "$0")/.."
# The compose project's network, named after the project (the checkout's directory unless COMPOSE_PROJECT_NAME says).
SA_NETWORK="${SA_NETWORK:-$(docker compose config --format json | python3 -c 'import json,sys; print(json.load(sys.stdin)["name"])')_default}"
OUT="${1:-backups/$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUT/buckets"
OUT_ABS="$(cd "$OUT" && pwd)"
echo "Backing up to $OUT_ABS"

# --- Postgres: a custom-format dump (pg_restore-friendly) ------------------------------------------------
echo "==> Postgres ($PG_DB)"
docker compose exec -T db pg_dump -U "$PG_USER" -Fc "$PG_DB" > "$OUT/db.dump"
echo "    $(du -h "$OUT/db.dump" | cut -f1) -> db.dump"

# --- Object storage: EVERY bucket, each into buckets/<name>/ ---------------------------------------------
# Each tenant's objects live in a bucket of their own, <prefix>-<tenant> (ADR 0372), beside the base bucket; until
# #1987 this mirrored one bucket, through a MinIO client the stack no longer runs. The key pair travels as
# environment to the helper container, never as arguments.
echo "==> Object storage (every bucket)"
AWS_ACCESS_KEY_ID="$S3_ACCESS_KEY" AWS_SECRET_ACCESS_KEY="$S3_SECRET_KEY" \
  docker run --rm --network "$SA_NETWORK" -e AWS_ACCESS_KEY_ID -e AWS_SECRET_ACCESS_KEY -e AWS_DEFAULT_REGION=us-east-1 \
    -e S3_URL="$S3_URL" -v "$OUT_ABS/buckets:/backup" --entrypoint sh amazon/aws-cli:2.36.45 -c '
      set -eu
      for bucket in $(aws --endpoint-url "$S3_URL" s3api list-buckets --query "Buckets[].Name" --output text); do
        aws --endpoint-url "$S3_URL" s3 sync --only-show-errors "s3://$bucket" "/backup/$bucket"
        echo "    $bucket: $(find "/backup/$bucket" -type f 2>/dev/null | wc -l) objects"
      done' 

# --- OpenBao: a raft snapshot (production/raft only; the dev -dev server is in-memory) -------------------
echo "==> OpenBao"
if docker compose exec -T openbao sh -c "BAO_ADDR=http://127.0.0.1:8200 BAO_TOKEN=root bao operator raft snapshot save /tmp/bao.snap" >/dev/null 2>&1; then
  docker compose exec -T openbao cat /tmp/bao.snap > "$OUT/openbao.snap"
  echo "    $(du -h "$OUT/openbao.snap" | cut -f1) -> openbao.snap"
else
  echo "    skipped: OpenBao is not running or is a dev (-dev, in-memory) server with no raft storage."
  echo "    In production run against a raft-backed OpenBao; in dev, re-provision via 'docker compose up openbao-init'."
fi

echo "Done. Backup at $OUT_ABS"
