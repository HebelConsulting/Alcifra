#!/usr/bin/env bash
# The brand's values for shell scripts and workflows (ADR 0994): source it, then use the BRAND_* variables.
#
#   source scripts/brand.sh
#   echo "$BRAND_FILE_STEM-$version-x64.dmg"
#
# One reader of brand/brand.json for every script, so no script parses the file its own way. Fails loudly on a
# missing field: a package named "-1.2.0-x64.dmg" is worse than a failed build.
_brand_file="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/brand/brand.json"
# Captured BEFORE it is evaluated: `eval "$(reader)"` evaluates an empty string successfully when the reader fails,
# which is how a missing python3 once surfaced only as "BRAND_FILE_STEM: unbound variable" a line later.
if ! command -v python3 >/dev/null 2>&1; then
  echo "error: scripts/brand.sh needs python3 to read $_brand_file (install it with the host's package manager)" >&2
  return 1 2>/dev/null || exit 1
fi
if ! _brand_values="$(python3 -I - "$_brand_file" <<'PY'
import json, shlex, sys
brand = json.load(open(sys.argv[1]))
distribution = brand["distribution"]
values = {
    "BRAND_NAME": brand["name"],
    "BRAND_FILE_STEM": brand["fileStem"],
    "BRAND_PUBLISHER": brand["publisher"],
    "BRAND_BUNDLE_ID": distribution["bundleId"],
    "BRAND_IMAGE_REGISTRY": distribution["imageRegistry"],
    "BRAND_IMAGE_NAME": distribution["imageName"],
    "BRAND_CHART_NAME": distribution["chartName"],
    "BRAND_RELEASE_REPOSITORY": distribution["releaseRepository"],
    "BRAND_DEMO_DOMAIN": distribution["domains"]["demo"],
}
for key, value in values.items():
    if not value:
        sys.exit(f"brand/brand.json: {key} is empty")
    print(f"{key}={shlex.quote(value)}")
PY
)"; then
  echo "error: could not read the brand from $_brand_file" >&2
  return 1 2>/dev/null || exit 1
fi
eval "$_brand_values"
unset _brand_file _brand_values
