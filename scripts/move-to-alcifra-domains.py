#!/usr/bin/env python3
"""Step 2 of the domain cutover (#1117, ADR 1009): the stacks, defaults and docs say the alcifra domains.

Run from the repository root on a clean tree: `python3 scripts/move-to-alcifra-domains.py`. Idempotent; re-run it after
a rebase. The edge's Caddyfile is edited by hand, because there the old names stay on purpose, as redirects and as
proxies for presigned and push hosts. Tests keep `demo.simplarchive.dev` where it is only a sample host.

The map (owner decisions 2026-10-28 and 2026-10-29): the demo at demo.alcifra.dev, with its logins and inbound mail
(every kiosk tenant, Crypto's included); the licence and delivery environment at get.alcifra.com; sender and contact
addresses at alcifra.com.
"""
import os
import re
import subprocess
import sys

SCOPE = ("tools/kiosk/", "tools/vendor/", "tools/edge/README.md", "brand/", "publish/", "README.md", "docs/deploy/",
         "scripts/", "charts/", "docker-compose.yaml", ".github/workflows/", "src/", "modules.env", "CLAUDE.md",
         "tools/aws-install/", "tools/aws-install-single/", "tools/aws-scratch/")
KEPT = ("scripts/move-to-alcifra-domains.py", "scripts/rename-")

# Longest names first, so push.packages… is not caught by packages….
MAP = [
    ("push.packages.simplarchive.dev", "push.get.alcifra.com"),
    ("packages-s3.simplarchive.dev", "s3.get.alcifra.com"),
    ("packages.simplarchive.dev", "get.alcifra.com"),
    ("push.demo.simplarchive.dev", "push.demo.alcifra.dev"),
    ("mail.demo.simplarchive.dev", "mail.demo.alcifra.dev"),
    ("_dmarc.demo.simplarchive.dev", "_dmarc.demo.alcifra.dev"),
    ("s3.simplarchive.dev", "s3.demo.alcifra.dev"),
    ("demo.simplarchive.dev", "demo.alcifra.dev"),
    ("manual.simplarchive.dev", "manual.alcifra.dev"),
]
SENDERS = re.compile(r"\b(licensing|admin|contact|support|notifications|postmaster)@simplarchive\.dev\b")


def tracked():
    out = subprocess.run(["git", "ls-files", "-z"], check=True, capture_output=True).stdout
    return [p for p in out.decode().split("\0") if p and p.startswith(SCOPE) and not p.startswith(KEPT)]


# Kept on purpose: the Crypto tenant's addresses, which its readers' hardware-key certificates carry (owner, 2026-10-29;
# moves with #1941 or re-issued certificates).
KEPT_TEXT = re.compile(r"crypt@demo\.simplarchive\.dev")


def rename_text(text):
    kept = []
    text = KEPT_TEXT.sub(lambda m: (kept.append(m.group(0)), f"\x00{len(kept) - 1}\x00")[1], text)
    text = _rename(text)
    return re.sub("\x00(\\d+)\x00", lambda m: kept[int(m.group(1))], text)


def _rename(text):
    for old, new in MAP:
        text = re.sub(rf"(?<![\w.-]){re.escape(old)}\b", new, text)
    return SENDERS.sub(r"\1@alcifra.com", text)


def main():
    if subprocess.run(["git", "diff", "--quiet"]).returncode != 0:
        sys.exit("The tree has changes; commit or stash them first, so the sweep is its own diff.")
    changed = 0
    for path in tracked():
        if not os.path.isfile(path) or path.endswith((".png", ".pdf", ".tgz", ".dll", ".ico")):
            continue
        try:
            text = open(path, encoding="utf-8").read()
        except UnicodeDecodeError:
            continue
        renamed = rename_text(text)
        if renamed != text:
            open(path, "w", encoding="utf-8", newline="").write(renamed)
            changed += 1
    print(f"{changed} files rewritten. Next: the suites, then commit.")


if __name__ == "__main__":
    main()
