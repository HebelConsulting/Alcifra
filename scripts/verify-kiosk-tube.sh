#!/usr/bin/env bash
# Verify that the kiosk's Tube demo holds what tools/kiosk/tube-seed.sh files, and that every video plays.
#
# WHY: on 2026-11-12 the nightly seed stopped before filing a single video (an `acl revoke` the API refused), and the
# Tube host served an empty catalogue for hours. Every other rollout check passed: logins, mail and populate know nothing
# of the Tube host, and an empty catalogue is a valid answer. So this check asks the public video host, anonymously, as a
# PeerTube client does, and compares against what the seed promises rather than against a number typed here.
#
# WHAT: from the seed it reads each `video "$<parent>" <slug> "<title>" …` call and each `alcifra document reference`.
# A video filed in Workshop is listed only through its references, so the expected catalogue size is the videos filed
# in a channel plus the references. Then, against the host:
#   - the catalogue lists exactly that many entries, and every seeded title;
#   - each video's file and thumbnail address answer 302 to storage, and storage serves the first byte of each;
#   - each video's embed page answers 200, the page other sites frame (Tube ADR 0005).
#
#   scripts/verify-kiosk-tube.sh [video host] [seed script]
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
host="${1:-tube.demo.alcifra.dev}"
seed="${2:-tools/kiosk/tube-seed.sh}"

python3 -I - "$host" "$seed" <<'PY'
import json, re, sys, urllib.error, urllib.request
host, seed = sys.argv[1], sys.argv[2]
text = open(seed, encoding="utf-8").read()

videos = re.findall(r'^(?:\w+="\$\()?(?:\w+=\S+ )*video "\$(\w+)" [\w-]+ "([^"]+)"', text, re.M)
references = re.findall(r'^alcifra document reference ', text, re.M)
if not videos:
    sys.exit(f"FAIL  read no video from {seed}: the seed's shape changed, so this check must change with it")
titles = {title for _, title in videos}
expected = sum(1 for parent, _ in videos if parent != "workshop") + len(references)

failed = []
def ok(line): print(f"  ok    {line}")
def fail(line): print(f"  FAIL  {line}"); failed.append(line)

class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs): return None
plain = urllib.request.build_opener(NoRedirect)

def get(url, first_byte=False, follow=True):
    req = urllib.request.Request(url, headers={"Range": "bytes=0-0"} if first_byte else {})
    try:
        return (urllib.request.urlopen if follow else plain.open)(req, timeout=30)
    except urllib.error.HTTPError as e:
        return e

with get(f"https://{host}/api/v1/videos?count=100") as r:
    catalogue = json.load(r)
listed = catalogue.get("data", [])
names = {v["name"] for v in listed}
(ok if catalogue.get("total") == expected else fail)(f"catalogue lists {catalogue.get('total')} entries, the seed promises {expected}")
for title in sorted(titles):
    (ok if title in names else fail)(f"listed: {title}")

seen = set()
for video in listed:
    uuid = video["uuid"]
    if uuid in seen:
        continue
    seen.add(uuid)
    name = video["name"]
    with get(f"https://{host}/api/v1/videos/{uuid}") as r:
        detail = json.load(r)
    files = [f["fileUrl"] for f in detail.get("files", []) if f.get("fileUrl")]
    if not files:
        fail(f"{name}: the video names no file")
        continue
    for label, url in (("file", files[0]), ("thumbnail", f"https://{host}/files/{uuid}/thumbnail")):
        r = get(url, follow=False)
        location = r.headers.get("Location", "")
        if r.status != 302 or not location.startswith("https://"):
            fail(f"{name}: {label} address answered {r.status}, not a redirect to storage")
            continue
        with get(location, first_byte=True) as s:
            (ok if s.status in (200, 206) else fail)(f"{name}: {label} served from storage ({s.status})")
    with get(f"https://{host}/videos/embed/{uuid}") as r:
        (ok if r.status == 200 else fail)(f"{name}: embed page ({r.status})")

if failed:
    sys.exit(f"{len(failed)} check(s) failed: the Tube demo is not what the seed files. See /var/log/kiosk-tube-seed.log on the kiosk.")
print("Every seeded video is listed, plays from storage and embeds.")
PY
