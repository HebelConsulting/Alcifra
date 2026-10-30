#!/bin/sh
# Combined in-pod OpenBao entrypoint for the kiosk (ADR 0485): start the server on the file backend, init once +
# auto-unseal on every start, then PROVISION everything the app needs (KV secrets, the Postgres dynamic + static
# credential engine, transit for MFA-secret encryption, the AppRole, and the OpenIddict PKI certs) — idempotently.
# Merges scripts/openbao-entrypoint.sh + the compose openbao-init provisioning into ONE script, since a k8s RWO PVC
# can't be shared with a separate init Job. Included via `.Files.Get` so its literal OpenBao `{{...}}` template vars
# survive Helm. NOT production-hardened: single unseal key + root token are persisted to the data volume.
set -e
export BAO_ADDR="http://127.0.0.1:8200"
KEYS="/openbao/init.json"
PG_HOST="${PG_HOST:-postgres}"

bao server -config=/etc/openbao/openbao.hcl &
PID=$!

# Wait until the API responds (`bao status` exits 0=unsealed or 2=sealed once listening).
while true; do
  if bao status >/dev/null 2>&1; then code=0; else code=$?; fi
  { [ "$code" -eq 0 ] || [ "$code" -eq 2 ]; } && break
  sleep 1
done

# Initialize once (persisted). With the awskms seal (ADR 0677) init yields RECOVERY keys — KMS unseals on
# every start and no unseal key exists; the installer moves init.json into the cloud secret store and wipes
# it from this volume. Without it (kiosk), the single unseal key + root token stay on the data volume.
if ! bao operator init -status >/dev/null 2>&1; then
  if [ -n "${SEAL_AWSKMS:-}" ]; then
    bao operator init -recovery-shares=1 -recovery-threshold=1 -format=json > "$KEYS"
  else
    bao operator init -key-shares=1 -key-threshold=1 -format=json > "$KEYS"
  fi
  chmod 644 "$KEYS" 2>/dev/null || true
fi

# Unseal: awskms does it itself (wait for it); otherwise unseal from the saved key (every restart).
if [ -n "${SEAL_AWSKMS:-}" ]; then
  until bao status >/dev/null 2>&1; do echo 'waiting for KMS auto-unseal...'; sleep 1; done
else
  if bao status >/dev/null 2>&1; then rc=0; else rc=$?; fi
  if [ "$rc" -eq 2 ]; then
    UNSEAL_KEY=$(sed -n '/"unseal_keys_b64"/{n;p;}' "$KEYS" | tr -d ' ",')
    bao operator unseal "$UNSEAL_KEY" >/dev/null
  fi
fi
# After the installer has moved init.json off the volume (awskms mode), a restart cannot re-provision — and
# does not need to: everything below is already provisioned and persisted. Serve and stop here.
if [ ! -f "$KEYS" ]; then
  echo 'openbao ready (bootstrap material externalised; skipping provisioning)'
  wait "$PID"
  exit 0
fi
export BAO_TOKEN=$(sed -n 's/.*"root_token": *"\([^"]*\)".*/\1/p' "$KEYS")

# --- Provision (idempotent) ------------------------------------------------------------------------------------
bao secrets enable -path=secret -version=2 kv 2>/dev/null || true
bao kv put secret/alcifra/objectstorage accessKey="${S3_ACCESS_KEY:-storageadmin}" secretKey="${S3_SECRET_KEY:-storageadmin}"
bao kv put secret/alcifra/smtp user="${SMTP_USER:-}" password="${SMTP_PASSWORD:-}"
bao kv put secret/alcifra/bootstrap clientSecret="${BOOTSTRAP_CLIENT_SECRET:-dev-bootstrap-secret}"

# Postgres credential engine — connects as alcifra_vault (created by db-init). Retry until that role exists.
bao secrets enable database 2>/dev/null || true
until bao write database/config/alcifra \
  plugin_name=postgresql-database-plugin \
  allowed_roles=alcifra,alcifra-owner,alcifra-runtime \
  connection_url="postgresql://{{username}}:{{password}}@${PG_HOST}:5432/alcifra?sslmode=${PG_SSLMODE:-disable}" \
  username=alcifra_vault password=alcifra_vault_bootstrap 2>/dev/null; do
  echo 'waiting for the alcifra_vault role (db-init)...'; sleep 2;
done
bao write -force database/rotate-root/alcifra
bao write database/roles/alcifra db_name=alcifra \
  creation_statements="CREATE ROLE \"{{name}}\" WITH LOGIN PASSWORD '{{password}}' VALID UNTIL '{{expiration}}' IN ROLE alcifra_app;" \
  revocation_statements="DROP OWNED BY \"{{name}}\"; DROP ROLE IF EXISTS \"{{name}}\";" \
  default_ttl=24h max_ttl=72h
bao write database/static-roles/alcifra-owner db_name=alcifra username=alcifra \
  rotation_period=86400 rotation_statements="ALTER ROLE \"{{name}}\" WITH PASSWORD '{{password}}';"
bao write -f database/rotate-role/alcifra-owner

# The RUNTIME role the app actually connects as (ADR 0721). A STATIC role, not a dynamic one, and the reason is
# the whole point: a dynamic credential's creation_statements is CREATE ROLE "{{name}}", so every lease mints a
# new USERNAME — and a username cannot be swapped underneath a live connection pool. The app therefore read one
# dynamic credential at startup and kept it, and at default_ttl (24h) Postgres revoked the role and every new
# connection failed 28P01 until a restart. A static role rotates the PASSWORD of a FIXED username, which Npgsql
# picks up for new physical connections without the app noticing.
#
# The chart never had this: db-init did not create the role, allowed_roles did not permit it, the policy did not
# grant it and the deployment did not set OpenBao__DatabaseRuntimeStaticRole — four omissions that agreed with
# each other, so nothing failed and every chart install quietly ran the model 0721 replaced (#1249).
bao write database/static-roles/alcifra-runtime db_name=alcifra username=alcifra_runtime \
  rotation_period=43200 rotation_statements="ALTER ROLE \"{{name}}\" WITH PASSWORD '{{password}}';"
# Forced rotation on EVERY provision: writing a static-role config only rotates on first creation, so on a
# re-run against an existing OpenBao the stored credential and the database password drift apart (ADR 0213's
# static-role drift note). An explicit rotate makes the two re-agree every time.
bao write -f database/rotate-role/alcifra-runtime

# Transit — encrypts the TOTP secret at rest (the key never leaves OpenBao).
bao secrets enable transit 2>/dev/null || true
bao write -f transit/keys/alcifra-mfa

# AppRole machine auth for the Api (fixed ids for the kiosk).
bao auth enable approle 2>/dev/null || true
printf 'path "secret/data/alcifra/*" { capabilities = ["read"] }\npath "database/creds/alcifra" { capabilities = ["read"] }\npath "database/static-creds/alcifra-owner" { capabilities = ["read"] }\npath "database/static-creds/alcifra-runtime" { capabilities = ["read"] }\npath "transit/encrypt/alcifra-mfa" { capabilities = ["update"] }\npath "transit/decrypt/alcifra-mfa" { capabilities = ["update"] }\n' | bao policy write alcifra -
bao write auth/approle/role/alcifra token_policies=alcifra token_ttl=1h token_max_ttl=4h
bao write auth/approle/role/alcifra/role-id role_id="${APPROLE_ROLE_ID:-alcifra-role}"
bao write auth/approle/role/alcifra/custom-secret-id secret_id="${APPROLE_SECRET_ID:-alcifra-secret}" 2>/dev/null || true

# OpenIddict signing + encryption certs via PKI — issued ONCE (re-issuing would invalidate live tokens).
bao secrets enable pki 2>/dev/null || true
bao secrets tune -max-lease-ttl=87600h pki 2>/dev/null || true
if ! bao read pki/cert/ca >/dev/null 2>&1; then
  bao write -field=certificate pki/root/generate/internal common_name='Alcifra Dev CA' ttl=87600h >/dev/null
  bao write pki/roles/openiddict allow_any_name=true max_ttl=8760h key_type=rsa key_bits=2048
fi
if ! bao kv get secret/alcifra/openiddict >/dev/null 2>&1; then
  bao write -format=json pki/issue/openiddict common_name=alcifra-signing ttl=8760h > /tmp/signing.json
  bao write -format=json pki/issue/openiddict common_name=alcifra-encryption ttl=8760h > /tmp/encryption.json
  bao kv put secret/alcifra/openiddict signing=@/tmp/signing.json encryption=@/tmp/encryption.json
fi

# The offline-archive signing key (ADR 0961) — issued ONCE, like the OpenIddict certificates. A rotation moves the old
# certificate into `previous` (kept published, so archives it signed keep verifying) and issues a new `current`.
if ! bao kv get secret/alcifra/archive-signing >/dev/null 2>&1; then
  bao write -format=json pki/issue/openiddict common_name=alcifra-archive-signing ttl=8760h > /tmp/archive-signing.json
  bao kv put secret/alcifra/archive-signing current=@/tmp/archive-signing.json previous=
fi

echo 'openbao ready + provisioned'
wait "$PID"
