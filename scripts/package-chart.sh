#!/usr/bin/env bash
# Packages the Helm chart under the brand's name, pointing at the brand's images (ADR 0994).
#
#   scripts/package-chart.sh <version>     ->  dist/<chartName>-<version>.tgz
#
# The source stays charts/simplarchive; what changes per brand is what a cluster sees: the chart's name, which names
# the release's resources, and the default image repositories of the application and its two sidecars
# (<imageRegistry>/<imageName>, -ocr, -postfix). Alcifra's chartName is "simplarchive" until the code rebrand
# (#1117 phase 5), so its existing installations upgrade in place.
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/brand.sh

version="${1:?usage: scripts/package-chart.sh <version>}"
version="${version#v}"
shipped_images="ghcr.io/hebelconsulting/simplarchive"   # the chart's own defaults, which the brand's replace

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
chart="$stage/$BRAND_CHART_NAME"
cp -R charts/simplarchive "$chart"

# The chart's name: the one top-level `name:` line.
sed -i.bak -E "s|^name: .*$|name: $BRAND_CHART_NAME|" "$chart/Chart.yaml"
# The images: a prefix replace, so the two sidecars' suffixes carry over.
sed -i.bak "s|$shipped_images|$BRAND_IMAGE_REGISTRY/$BRAND_IMAGE_NAME|g" "$chart/values.yaml" "$chart/values-production.example.yaml"
rm -f "$chart"/*.bak

grep -q "^name: $BRAND_CHART_NAME$" "$chart/Chart.yaml" || { echo "error: the chart's name was not rewritten" >&2; exit 1; }
helm lint "$chart" >/dev/null
mkdir -p dist
helm package "$chart" --version "$version" --app-version "$version" --destination dist
