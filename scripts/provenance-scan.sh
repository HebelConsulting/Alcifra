#!/usr/bin/env bash
#
# provenance-scan.sh — every NuGet package this repository resolves, direct or transitive, either sits under an ID
# prefix its owner reserved on nuget.org ("verified"), or its owners have been REVIEWED against the project it claims
# to be (build/licenses/reviewed-owners.json) (#1794). Run daily beside vulnerability-scan.sh (ADR 0926), from the
# repository root, after a restore.
#
# Why: the PDF parser was an unofficial upload of PdfPig for over a year — owned by an account that is not one of its
# maintainers, with no project or licence metadata — and nothing noticed, because a vulnerability scan asks whether a
# version has a known CVE, never who published it. A NEW unverified package, or a reviewed one whose OWNERS changed
# (an account takeover or a hand-over looks exactly like that), is a finding a person has to look at.
#
# Prints the findings and exits 1; prints "All packages verified or reviewed." and exits 0 otherwise. A package the
# registry cannot be asked about is a finding too: silence must not read as "checked".
set -euo pipefail

target="${1:-}"
reviewed="build/licenses/reviewed-owners.json"
dotnet list $target package --include-transitive --format json > packages.json
ids=$(jq -r '[.projects[].frameworks[]? | ((.topLevelPackages // []) + (.transitivePackages // []))[] | .id] | unique[]' packages.json)

: > provenance-findings.txt
for id in $ids; do
  # Our own feed's packages are not on nuget.org; they are published by this organisation's workflows.
  case "$id" in SimplArchive.*) continue ;; esac
  answer=$(curl -fsS --retry 3 "https://azuresearch-usnc.nuget.org/query?q=packageid:${id}&prerelease=true&semVerLevel=2.0.0" || echo '')
  if [ -z "$answer" ]; then
    echo "$id  (the registry did not answer)" >> provenance-findings.txt
    continue
  fi
  entry=$(jq -c '.data[0] // empty' <<<"$answer")
  if [ -z "$entry" ]; then
    echo "$id  (not on nuget.org)" >> provenance-findings.txt
    continue
  fi
  [ "$(jq -r '.verified' <<<"$entry")" = "true" ] && continue
  owners=$(jq -c '[.owners // [] | .[] | ascii_downcase] | sort' <<<"$entry")
  known=$(jq -c --arg id "$id" '[.[] | select((.Id | ascii_downcase) == ($id | ascii_downcase)) | .Owners | map(ascii_downcase) | sort] | first // empty' "$reviewed")
  project=$(jq -r '.projectUrl // "-"' <<<"$entry")
  if [ -z "$known" ]; then
    echo "$id  owners $(jq -r 'join(",")' <<<"$owners")  project $project  (unverified and not reviewed)" >> provenance-findings.txt
  elif [ "$known" != "$owners" ]; then
    echo "$id  owners now $(jq -r 'join(",")' <<<"$owners"), reviewed as $(jq -r 'join(",")' <<<"$known")  project $project  (OWNERS CHANGED)" >> provenance-findings.txt
  fi
done

if [ -s provenance-findings.txt ]; then
  echo "::error::Packages whose publisher nobody has reviewed — check each owner against its project, then add it to $reviewed:"
  sort -u provenance-findings.txt | tee provenance-findings.sorted && mv provenance-findings.sorted provenance-findings.txt
  exit 1
fi
echo "All packages verified or reviewed."
