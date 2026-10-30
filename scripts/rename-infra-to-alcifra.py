#!/usr/bin/env python3
"""The lower-case infrastructure sweep (#1117, ADR 1007): `simplarchive` -> `alcifra` in the database, OpenBao, chart,
compose and installer names.

Run from the repository root on a clean tree: `python3 scripts/rename-infra-to-alcifra.py`. Idempotent. Re-run it
after a rebase instead of resolving its conflicts by hand.

Scope is the files below, where a lower-case `simplarchive` names infrastructure: roles, the database, OpenBao paths,
policies and keys, Kubernetes resources, images. What it does NOT touch, each for its reason (owner decisions on
#1117, 2026-10-29):
- domains (`*.simplarchive.dev`, `*.simplarchive.local`): the domain cutover moves them;
- what lives on a running host's disk (`/opt/simplarchive*`, `/var/...`, the compose project's network, the host
  services): moved by runbook at the v0.55.0 rollout (slice 4);
- the Encryption Service's image, which its own repository names;
- package ids (`simplarchive.moduleabi` is frozen, #1981) and the names before the rename that are still accepted on
  the wire (`simplarchive://`, `vnd.simplarchive`).
"""
import os
import re
import subprocess
import sys

SCOPE = (
    "scripts/db-init.sql", "scripts/backup.sh", "scripts/restore.sh", "scripts/rolling-update.sh",
    "scripts/openbao", "scripts/check-pinned-images.sh",
    "charts/", "docker-compose.yaml", "images.env",
    "tools/kiosk/", "tools/vendor/", "tools/aws-install/", "tools/aws-install-single/", "tools/aws-modules/",
    "tools/aws-scratch/",
    "src/Alcifra.Api/Configuration/", "src/Alcifra.Api/appsettings", "src/Alcifra.Worker/appsettings",
    "src/Alcifra.Infrastructure/DependencyInjection.cs", "src/Alcifra.Infrastructure/Persistence/AlcifraDbContextFactory.cs",
    "src/Alcifra.Api/OfflineArchives/",
    "tests/Alcifra.EndToEndTests/OpenBaoSecretsTests.cs", "tests/Alcifra.IntegrationTests/OpenBaoSecretsReaderTests.cs",
    "tests/Alcifra.UnitTests/DbInitCopyLockstepTests.cs", "tests/Alcifra.UnitTests/DatabaseCredentialRefreshTests.cs",
    "tests/Alcifra.EndToEndTests/Infrastructure/", "tests/Alcifra.SelfHosting/",
    "docs/deploy/", "CLAUDE.md", "README.md", "publish/README.public.md",
)

# Kept wherever they appear: masked before the replace, restored after.
PROTECTED = [re.compile(p) for p in (
    r"[\w.@-]*\bsimplarchive\.(dev|local|chat)\b",                 # domains and addresses at them
    r"/opt/simplarchive[\w./-]*", r"/var/[\w./-]*simplarchive[\w./-]*",  # a running host's disk (slice 4)
    r"\bsimplarchive_default\b",                                   # the compose project's network on a host
    r"\bsimplarchive-[a-z-]+-\d+\b", r"\bsimplarchive_[\w-]+-data\b",  # its containers and named volumes on a host
    r"dev\.simplarchive\.mobile",                                  # the phone's identifier (Mobile #187)
    r"\bsimplarchive-(edge|vendor|update-check)[\w.-]*",           # host services and directories (slice 4)
    r"hebelconsulting/simplarchive-encryption",                    # the Encryption Service's image
    r"\bsimplarchive\.(moduleabi|licensing|messenger|cli|sapass)[\w.:/-]*",  # package ids
    r"simplarchive://", r"x-scheme-handler/simplarchive", r"vnd\.simplarchive",  # wire names still accepted
    r"\.simplarchive\b(?![-_/])",                                  # the sealed archive's earlier extension
    # The names before the rename, named ON PURPOSE (restored by hand after the first run, kept by every re-run):
    r"\bsimplarchive_owner\b",                                     # db-init's transition from an even earlier owner
    r"simplarchive-mfa",                                           # the vendor's transit key keeps its name (ADR 1007)
    r"nameOverride[:=] ?simplarchive\b", r"[Ll]egacy[A-Za-z_]*[:=] ?\"?simplarchive\b",
    r"LEGACY_BUCKET_PREFIX=simplarchive\b", r"simplarchive-\{tenant\}", r"simplarchive-…",
    r"`simplarchive` chart", r"<release>-simplarchive-", r"charts/simplarchive\b(?=\s+# v0\.55\.0 only)",
    r"under `simplarchive`", r"\(\"simplarchive\" for an installation from before the rename\)",
    r"\[`simplarchive`\]", r"`simplarchive-\*`",
)]


# Files that ARE about the move from the old names: they name both sides throughout.
KEPT = ("tools/vendor/rename-to-alcifra.sh",)


def tracked():
    out = subprocess.run(["git", "ls-files", "-z"], check=True, capture_output=True).stdout
    return [p for p in out.decode().split("\0") if p and p.startswith(SCOPE) and not p.startswith(KEPT)]


def rename_text(text):
    kept = []

    def mask(match):
        kept.append(match.group(0))
        return f"\x00{len(kept) - 1}\x00"

    for pattern in PROTECTED:
        text = pattern.sub(mask, text)
    # `_` is a word character, so `\b` does not end a name before it: roles like simplarchive_app need their own rule.
    text = re.sub(r"\bsimplarchive(?=_)", "alcifra", text)
    text = re.sub(r"\bsimplarchive\b", "alcifra", text)
    return re.sub("\x00(\\d+)\x00", lambda m: kept[int(m.group(1))], text)


def main():
    if subprocess.run(["git", "diff", "--quiet"]).returncode != 0:
        sys.exit("The tree has changes; commit or stash them first, so the sweep is its own diff.")
    changed = 0
    for path in tracked():
        if path.endswith((".png", ".pdf", ".tgz")) or not os.path.isfile(path):
            continue
        try:
            text = open(path, encoding="utf-8").read()
        except UnicodeDecodeError:
            continue
        renamed = rename_text(text)
        if renamed != text:
            open(path, "w", encoding="utf-8", newline="").write(renamed)
            changed += 1
    print(f"{changed} files rewritten. Next: dotnet build, the full suite, helm lint, then commit.")


if __name__ == "__main__":
    main()
