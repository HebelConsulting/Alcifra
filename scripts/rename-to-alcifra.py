#!/usr/bin/env python3
"""The phase-5 code sweep (#1117): SimplArchive -> Alcifra in every tracked path and text file, case-sensitively.

Run from the repository root on a clean tree: `python3 scripts/rename-to-alcifra.py`. Idempotent: a second run finds
nothing. Re-run it after a rebase instead of resolving the sweep's conflicts by hand.

What it does NOT touch, each for its reason (owner decisions on #1117, 2026-10-29):
- the module ABI's assembly and namespaces, `SimplArchive.ModuleAbi` (frozen; renaming is ABI 2.0, #1981);
- `SimplArchive.Messenger`, the module PACKAGE the feed still serves at the pinned version (modules.env, chart pins);
  its repository renamed its packages to `Alcifra.*` (SimplArchiveMessenger#36), and the pin moves with a rollout;
- `SimplArchive.FlightSchool`/`.Encryption`/`.Licensing`: module packages the feeds publish under these ids; renaming
  the pins asked the feed for packages that do not exist and took the kiosk down at the 2026-10-30 reset;
- `X-SimplArchive-` header names: only the legacy webhook header still spells it (WireNames.Legacy, retired at v0.56.0);
- Localization/PersistedNames.cs and its pin test: names with data behind them (#1982);
- earlier names a swept file mentions on purpose (KEPT_MENTIONS): published package ids, the app's old name;
- docs/adr/: decision history, which says what was true when written;
- lower-case `simplarchive` (images, chart, roles, OpenBao paths, legacy wire names): moved by their own steps.
"""
import os
import re
import subprocess
import sys

OLD, NEW = "SimplArchive", "Alcifra"

# What keeps the old spelling wherever it appears; masked before the replace and restored after.
PROTECTED = [re.compile(p) for p in (
    r"SimplArchive\.ModuleAbi",                     # the frozen module ABI (#1981)
    r"SimplArchive\.Messenger(?![\w.])",            # the Messenger MODULE package the pins name (the client is Alcifra.* now)
    r"SimplArchive\.(FlightSchool|Encryption|Licensing)\b",  # module PACKAGES on the feeds (modules.env, chart pins)
    r"X-SimplArchive-",                              # the legacy webhook header (WireNames.Legacy)
    r"HebelConsulting/SimplArchive[A-Za-z]*",        # GitHub repositories: renamed by their own step, with the repos
    r"\bSimplArchive(Private|Mobile|EncryptionService|Encryption|Licensing|Messenger|Deck)\b",
)]

# Paths (prefixes) whose CONTENT is never rewritten. They still move with their folder unless also in PATH_KEPT.
CONTENT_KEPT = (
    "docs/adr/",
    "src/SimplArchive.ModuleAbi/",
    "scripts/rename-to-alcifra.py",
)

# Files whose string literals keep the old spelling (names with data behind them, #1982) but whose `namespace` and
# `using` lines move with the code around them.
NAMESPACE_ONLY = (
    "src/SimplArchive.Localization/PersistedNames.cs",
    "tests/SimplArchive.UnitTests/PersistedNamesTests.cs",
)
# Under their swept names too: a re-run finds them there, and rewrote their literals until they were listed (#1997).
NAMESPACE_ONLY += tuple(p.replace(OLD, NEW) for p in NAMESPACE_ONLY)

# Mentions of EARLIER names that a swept file keeps on purpose (#1997): what was published under them, and the working
# name. Keyed by the SWEPT path only. A file still at its old path predates the sweep, so its mentions are code to rename.
KEPT_MENTIONS = {
    ".github/workflows/abi-publish.yml": [r"artifacts/SimplArchive\.\*\.nupkg"],
    ".github/workflows/saconsole-publish.yml": [r"artifacts/SimplArchive\.\*\.nupkg", r"SimplArchive\.(Cli|Sapass)\b"],
    "README.md": [r"working name: SimplArchive\b"],
    "publish/README.public.md": [r"working name: SimplArchive\b"],
    "docs/deploy/release.md": [r"SimplArchive\.(Cli|Sapass|app)\b"],
    "src/Alcifra.Cli/Alcifra.Cli.csproj": [r"SimplArchive\.Cli\b"],
    "src/Alcifra.Cli/README.md": [r"SimplArchive\.Cli\b"],
    "src/Alcifra.Sapass/Alcifra.Sapass.csproj": [r"SimplArchive\.Sapass\b"],
}
KEPT_MENTIONS = {path: [re.compile(p) for p in patterns] for path, patterns in KEPT_MENTIONS.items()}

# Paths (prefixes) that keep their NAME too.
PATH_KEPT = ("docs/adr/", "src/SimplArchive.ModuleAbi/")

# A namespace written RELATIVELY (`ModuleAbi.IIndustryModule`) resolved only while the caller was itself inside
# `SimplArchive.*`; once the caller is `Alcifra.*`, a reference into a frozen namespace has to be spelled out.
RELATIVE_INTO_FROZEN = [
    (re.compile(r"(?<![\w.])ModuleAbi\.(?=[A-Z])"), "SimplArchive.ModuleAbi."),
]
CODE = re.compile(r"\.(cs|razor|axaml|cshtml)$")

BINARY = re.compile(r"\.(png|jpe?g|gif|ico|icns|pdf|woff2?|ttf|otf|zip|dll|exe|so|dylib|p12|pfx|der|gz|tgz|dmg|svgz|bin)$", re.I)


def tracked():
    out = subprocess.run(["git", "ls-files", "-z"], check=True, capture_output=True).stdout
    return [p for p in out.decode().split("\0") if p]


def content_kept(path):
    return path.startswith(CONTENT_KEPT)


def path_kept(path):
    return path.startswith(PATH_KEPT)


def rename_text(text, also_kept=()):
    kept = []

    def mask(match):
        kept.append(match.group(0))
        return f"\x00{len(kept) - 1}\x00"

    for pattern in [*PROTECTED, *also_kept]:
        text = pattern.sub(mask, text)
    text = text.replace(OLD, NEW)
    return re.sub("\x00(\\d+)\x00", lambda m: kept[int(m.group(1))], text)


def main():
    if subprocess.run(["git", "diff", "--quiet"]).returncode != 0:
        sys.exit("The tree has changes; commit or stash them first, so the sweep is its own diff.")

    # 1. Contents.
    changed = 0
    for path in tracked():
        if content_kept(path) or BINARY.search(path) or not os.path.isfile(path):
            continue
        try:
            text = open(path, encoding="utf-8").read()
        except UnicodeDecodeError:
            continue
        if path in NAMESPACE_ONLY:
            renamed = "".join(rename_text(l) if l.lstrip().startswith(("namespace ", "using ")) else l
                              for l in text.splitlines(keepends=True))
        else:
            renamed = rename_text(text, KEPT_MENTIONS.get(path, ()))
        if CODE.search(path) and path not in NAMESPACE_ONLY:
            for pattern, full in RELATIVE_INTO_FROZEN:
                renamed = pattern.sub(full, renamed)
        if renamed != text:
            open(path, "w", encoding="utf-8", newline="").write(renamed)
            changed += 1

    # 2. Paths, deepest first so a directory moves after what it contains has been renamed inside it.
    moves = 0
    for path in sorted(tracked(), key=lambda p: p.count("/"), reverse=True):
        if path_kept(path) or OLD not in path:
            continue
        parts = path.split("/")
        target = "/".join(rename_text(p) for p in parts)
        if target != path:
            os.makedirs(os.path.dirname(target) or ".", exist_ok=True)
            subprocess.run(["git", "mv", path, target], check=True)
            moves += 1

    # Directories left empty by the moves.
    for root, dirs, files in sorted(os.walk(".", topdown=False), key=lambda w: -w[0].count(os.sep)):
        if OLD in root and not os.listdir(root) and ".git" not in root:
            os.rmdir(root)

    print(f"{changed} files rewritten, {moves} paths moved. Next: dotnet build, the full suite, then commit.")


if __name__ == "__main__":
    main()
