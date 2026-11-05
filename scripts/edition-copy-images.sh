#!/usr/bin/env bash
#
# edition-copy-images.sh VERSION OUT — copy a white-label edition's images and chart into OCI layouts under OUT, one per
# repository, named after it with __ for a slash: what `alcifra edition publish` reads (Licensing ADR 0022). Every platform
# and attestation comes along, digests unchanged so the signatures still verify, and each image's signature tags too.
#
# Needs GH_TOKEN with read access to the packages, and Docker for skopeo. The token reaches skopeo in an auth file that
# is deleted on exit, never on a command line.
set -euo pipefail

version="${1:?version}"
out="${2:?output folder}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repo_root/scripts/brand.sh"

mkdir -p "$out"
out="$(cd "$out" && pwd)"
auth="$(mktemp -d)"
trap 'rm -rf "$auth"' EXIT
python3 -I - > "$auth/auth.json" <<'PY'
import base64, json, os
registry = os.environ.get("BRAND_IMAGE_REGISTRY", "ghcr.io").split("/")[0]
print(json.dumps({"auths": {registry: {"auth": base64.b64encode(f"x:{os.environ['GH_TOKEN']}".encode()).decode()}}}))
PY

skopeo() {
  docker run --rm -v "$auth:/auth:ro" -v "$out:/layouts" quay.io/skopeo/stable:v1.20.0 "$@"
}

copy() {   # repository, tag
  skopeo copy --all --preserve-digests --src-authfile /auth/auth.json \
    "docker://$BRAND_IMAGE_REGISTRY/$1:$2" "oci:/layouts/${1//\//__}:$2"
}

for image in "$BRAND_IMAGE_NAME" "$BRAND_IMAGE_NAME-ocr" "$BRAND_IMAGE_NAME-postfix"; do
  echo "Copying $image:$version" >&2
  copy "$image" "$version"
  # The signature of this version's digest, which cosign finds under a tag derived from the digest.
  digest="$(skopeo inspect --authfile /auth/auth.json --format '{{.Digest}}' "docker://$BRAND_IMAGE_REGISTRY/$image:$version")"
  copy "$image" "${digest/:/-}.sig"
done

echo "Copying charts/$BRAND_CHART_NAME:$version" >&2
copy "charts/$BRAND_CHART_NAME" "$version"
