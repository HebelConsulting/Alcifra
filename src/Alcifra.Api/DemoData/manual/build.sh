#!/usr/bin/env bash
# Builds every binary sample of the Manual tenant (ManualDemoSeeder) from its plain-text source, so each
# committed file is exactly what its source produces and a change to a sample is a reviewable text diff.
#   .typ  -> .pdf   typst, bundled fonts only, fixed SOURCE_DATE_EPOCH (byte-reproducible)
#   .html -> .docx/.odt, .csv -> .xlsx/.ods   headless office conversion (content-reproducible; the
#                                           containers carry a build timestamp)
#   .svg  -> .png   rsvg-convert
# .eml and .vcf are text already and are embedded as they are. Requires typst, soffice and rsvg-convert.
set -euo pipefail
cd "$(dirname "$0")"
export SOURCE_DATE_EPOCH=1772496000
out=.

typ() { typst compile --ignore-system-fonts --root . "$@"; }
typ alpenglas-invoice.typ "$out/alpenglas-invoice.pdf"
typ alpenglas-maintenance.typ "$out/alpenglas-maintenance.pdf"
typ --input rev=1 alpenglas-offer.typ "$out/alpenglas-offer-v1.pdf"
typ --input rev=2 alpenglas-offer.typ "$out/alpenglas-offer-v2.pdf"
typ seewelle-contract.typ "$out/seewelle-contract.pdf"
for m in 1 2 3; do typ --input month=$m seewelle-invoice.typ "$out/seewelle-invoice-$m.pdf"; done
typ --input rev=1 catering-invoice.typ "$out/catering-invoice-v1.pdf"
typ --input rev=2 catering-invoice.typ "$out/catering-invoice-v2.pdf"
typ delivery-note.typ "$out/delivery-note.pdf"
typ press-release-draft.typ "$out/press-release-draft.pdf"
typ price-list-2025.typ "$out/price-list-2025.pdf"

office() { soffice --headless --convert-to "$1" --outdir "$out" "$2" >/dev/null; }
office docx:"Office Open XML Text" lease-letter.html
office odt:writer8 letterhead.html
office xlsx bergkraft-forecast.csv
office ods invoice-template.csv

rsvg-convert -o "$out/site-plan.png" site-plan.svg
echo "built"
