#!/usr/bin/env bash
# What this repository does with a release, from the brand (ADR 0996). Run in a workflow's gate job; writes to
# $GITHUB_OUTPUT (or prints, outside Actions):
#
#   publishes=true|false     this is the brand's release repository: it publishes images and desktop packages.
#                            Alcifra's is the public mirror, HebelConsulting/Alcifra, so nothing changed for
#                            us; a white-label fork names itself.
#   white_label=true|false   this edition credits the product it is made from (brand.json's credit, ADR 0995): it
#                            builds its own manual and never commits it, so its diff to upstream stays brand/ only.
set -euo pipefail
source "$(dirname "$0")/brand.sh"
repository="${GITHUB_REPOSITORY:?GITHUB_REPOSITORY is not set}"
publishes=false; [ "$repository" = "$BRAND_RELEASE_REPOSITORY" ] && publishes=true
white_label=false; [ -n "$BRAND_CREDIT" ] && white_label=true
{
  echo "publishes=$publishes"
  echo "white_label=$white_label"
} >> "${GITHUB_OUTPUT:-/dev/stdout}"
