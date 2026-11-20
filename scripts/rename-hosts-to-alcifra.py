#!/usr/bin/env python3
"""The host sweep (#1117 slice 4, ADR 1008): what lives on a running host's disk says alcifra.

Run from the repository root on a clean tree: `python3 scripts/rename-hosts-to-alcifra.py`. Idempotent; re-run it after
a rebase. It completes scripts/rename-infra-to-alcifra.py, which protected these names because they cannot change in
the repository before the host they describe has moved (tools/kiosk/MOVE-TO-ALCIFRA.md, its own maintenance window):
- the host directories `/opt/simplarchive`, `-src`, `-edge`, `-vendor`, and `/var/log/…`, `/var/lib/…` beneath them;
- the shared network `simplarchive-edge` and the edge's pinned project name;
- the compose projects those directories name (`simplarchive_default`, `simplarchive-api-1`, `simplarchive_*-data`);
- the single-machine host's update-check units.
Domains stay for the domain cutover; the names the move itself must still find keep theirs in the runbook, which
this script never touches.
"""
import os
import re
import subprocess
import sys

KEPT = ("docs/adr/", "scripts/rename-", "tools/kiosk/MOVE-TO-ALCIFRA.md")

RULES = [
    (re.compile(r"/opt/simplarchive(?=[\w./-]|\b)"), "/opt/alcifra"),
    (re.compile(r"(/var/(?:log|lib)/[\w./-]*?)simplarchive"), r"\1alcifra"),
    (re.compile(r"\.simplarchive-bootstrapped"), ".alcifra-bootstrapped"),
    (re.compile(r"\bsimplarchive-(edge|vendor|src|update-check)\b"), r"alcifra-\1"),
    (re.compile(r"\bsimplarchive_default\b"), "alcifra_default"),
    (re.compile(r"\bsimplarchive-([a-z-]+-\d+)\b"), r"alcifra-\1"),
    (re.compile(r"\bsimplarchive_([\w-]+-data)\b"), r"alcifra_\1"),
]


def tracked():
    out = subprocess.run(["git", "ls-files", "-z"], check=True, capture_output=True).stdout
    return [p for p in out.decode().split("\0") if p and not p.startswith(KEPT)]


def rename_text(text):
    for pattern, replacement in RULES:
        text = pattern.sub(replacement, text)
    return text


def main():
    if subprocess.run(["git", "diff", "--quiet"]).returncode != 0:
        sys.exit("The tree has changes; commit or stash them first, so the sweep is its own diff.")
    changed = 0
    for path in tracked():
        if not os.path.isfile(path) or path.endswith((".png", ".pdf", ".tgz", ".dll")):
            continue
        try:
            text = open(path, encoding="utf-8").read()
        except UnicodeDecodeError:
            continue
        renamed = rename_text(text)
        if renamed != text:
            open(path, "w", encoding="utf-8", newline="").write(renamed)
            changed += 1
    for unit in ("service", "timer"):
        old = f"docs/deploy/simplarchive-update-check.{unit}"
        if os.path.exists(old):
            subprocess.run(["git", "mv", old, f"docs/deploy/alcifra-update-check.{unit}"], check=True)
    print(f"{changed} files rewritten. Next: the full suite, then commit.")


if __name__ == "__main__":
    main()
