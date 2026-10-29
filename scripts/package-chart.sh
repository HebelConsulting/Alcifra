#!/usr/bin/env bash
# Packages the Helm chart under the brand's name, pointing at the brand's images (ADR 0994).
#
#   scripts/package-chart.sh <version>                          ->  dist/<chartName>-<version>.tgz
#   scripts/package-chart.sh <version> <chartName> <imageName>  ->  the same chart under other names
#
# The source stays charts/alcifra; what changes per brand is what a cluster sees: the chart's name, which names
# the release's resources, and the default image repositories of the application and its two sidecars
# (<imageRegistry>/<imageName>, -ocr, -postfix). The second form publishes the chart under the names before the
# rename for one more release (ADR 1004), so an installation upgrading with it keeps its resources' names.
set -euo pipefail
cd "$(dirname "$0")/.."
source scripts/brand.sh

version="${1:?usage: scripts/package-chart.sh <version>}"
version="${version#v}"
chart_name="${2:-$BRAND_CHART_NAME}"
image_name="${3:-$BRAND_IMAGE_NAME}"
shipped_images="ghcr.io/hebelconsulting/alcifra"   # the chart's own defaults, which the brand's replace

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
chart="$stage/$chart_name"
cp -R charts/alcifra "$chart"

# The chart's name: the one top-level `name:` line.
sed -i.bak -E "s|^name: .*$|name: $chart_name|" "$chart/Chart.yaml"
# The images: a prefix replace, so the two sidecars' suffixes carry over.
sed -i.bak "s|$shipped_images|$BRAND_IMAGE_REGISTRY/$image_name|g" "$chart/values.yaml" "$chart/values-production.example.yaml"
rm -f "$chart"/*.bak

grep -q "^name: $chart_name$" "$chart/Chart.yaml" || { echo "error: the chart's name was not rewritten" >&2; exit 1; }
helm lint "$chart" >/dev/null
mkdir -p dist
helm package "$chart" --version "$version" --app-version "$version" --destination dist
