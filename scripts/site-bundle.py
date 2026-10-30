#!/usr/bin/env python3
"""The site bundle (ADR 1010, #2004): what one product hands the website, read out of its own manual.

    scripts/site-bundle.py build    --out DIR --product-id core --product-name Alcifra --lang en
                                    --typ manual/manual.typ --pdf <manual.pdf> [--version main] [--commit <sha>]
    scripts/site-bundle.py stamp    --zip site-bundle.zip --version 0.56.0 [--notes notes.md] [--lang en]
    scripts/site-bundle.py validate <site-bundle.zip | DIR>

`build` asks the manual (`typst eval`) for its `<site-figure>` and `<site-feature>` markers (manual/template.typ),
copies the figures and the PDF, and writes DIR plus DIR.zip. A second `build` with another `--lang` into the same DIR
adds that language. `stamp` sets the version and the release notes of a built bundle; the release does that, because
the notes are written after the build. `validate` checks a bundle against the contract below and exits 1 on any
breach, naming each.

The contract, version 1:
    manifest.json   {"schema": "alcifra-site-bundle", "schemaVersion": 1,
                     "product": {"id", "name"}, "version", "commit",
                     "languages": {"<lang>": {"manual": {"pdf", "html"}, "notes",
                                              "features": [{"id", "title", "summary", "figure"}],
                                              "figures": [{"id", "files", "caption"}]}}}
    <lang>/manual.pdf, <lang>/figures/<file>, <lang>/notes.md   (paths in the manifest are relative to the bundle)
A feature's `figure` names a figure by its id (the file stem of its first image). `html` and `notes` may be null.
A consumer refuses a schemaVersion it does not know; a new FIELD is not a new version, a changed one is.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile

SCHEMA = "alcifra-site-bundle"
SCHEMA_VERSION = 1
LANGUAGES = {"en", "de"}
# A fixed timestamp for every zip entry, so the same content always makes the same zip (as SOURCE_DATE_EPOCH does
# for the manual, ADR 0510).
ZIP_TIME = (2026, 6, 1, 0, 0, 0)
ID = re.compile(r"^[a-z0-9][a-z0-9-]*$")


def squash(text):
    return re.sub(r"\s+", " ", text or "").strip()


def typst_query(typ, label, product_name, root):
    out = subprocess.run(
        ["typst", "eval", f"query(<{label}>).map(it => it.value)", "--in", typ, "--root", root,
         "--font-path", os.path.join(root, "brand", "assets", "fonts"), "--input", f"product={product_name}"],
        check=True, capture_output=True, text=True).stdout
    return json.loads(out)


def stem(path):
    return os.path.splitext(os.path.basename(path))[0]


def build(args):
    root = os.path.abspath(args.root)
    typ = os.path.abspath(args.typ)
    out = os.path.abspath(args.out)
    lang = args.lang
    os.makedirs(os.path.join(out, lang, "figures"), exist_ok=True)

    figures = []
    for entry in typst_query(typ, "site-figure", args.product_name, root):
        files = []
        for rel in entry["files"]:
            source = os.path.join(os.path.dirname(typ), rel)
            target = f"{lang}/figures/{os.path.basename(rel)}"
            shutil.copyfile(source, os.path.join(out, target))
            files.append(target)
        figures.append({"id": stem(entry["files"][0]), "files": files, "caption": squash(entry["caption"])})

    features = [{"id": f["id"], "title": f["title"], "summary": squash(f["summary"]), "figure": f.get("figure")}
                for f in typst_query(typ, "site-feature", args.product_name, root)]

    shutil.copyfile(args.pdf, os.path.join(out, lang, "manual.pdf"))

    manifest_path = os.path.join(out, "manifest.json")
    manifest = json.load(open(manifest_path)) if os.path.exists(manifest_path) else {
        "schema": SCHEMA, "schemaVersion": SCHEMA_VERSION,
        "product": {"id": args.product_id, "name": args.product_name},
        "version": args.version,
        "commit": args.commit or subprocess.run(["git", "rev-parse", "HEAD"], check=True, capture_output=True,
                                                 text=True, cwd=root).stdout.strip(),
        "languages": {},
    }
    manifest["languages"][lang] = {
        "manual": {"pdf": f"{lang}/manual.pdf", "html": None},
        "notes": None,
        "features": features,
        "figures": figures,
    }
    write_manifest(out, manifest)
    problems = check(out, manifest)
    if problems:
        sys.exit("The bundle breaks its contract:\n  " + "\n  ".join(problems))
    zip_dir(out, f"{out}.zip")
    print(f"{out}.zip: {args.product_id} {manifest['version']}, {lang}: {len(features)} features, {len(figures)} figures")


def write_manifest(directory, manifest):
    with open(os.path.join(directory, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
        f.write("\n")


def zip_dir(directory, target):
    with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
        for base, dirs, files in os.walk(directory):
            dirs.sort()
            for name in sorted(files):
                full = os.path.join(base, name)
                info = zipfile.ZipInfo(os.path.relpath(full, directory).replace(os.sep, "/"), ZIP_TIME)
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0o644 << 16
                z.writestr(info, open(full, "rb").read())


def unpacked(path):
    """A bundle as a directory: the directory itself, or the zip extracted beside it."""
    if os.path.isdir(path):
        return path
    target = f"{path}.d"
    shutil.rmtree(target, ignore_errors=True)
    with zipfile.ZipFile(path) as z:
        z.extractall(target)
    return target


def stamp(args):
    directory = unpacked(args.zip)
    manifest = json.load(open(os.path.join(directory, "manifest.json")))
    manifest["version"] = args.version
    if args.notes:
        target = f"{args.lang}/notes.md"
        shutil.copyfile(args.notes, os.path.join(directory, target))
        manifest["languages"][args.lang]["notes"] = target
    write_manifest(directory, manifest)
    problems = check(directory, manifest)
    if problems:
        sys.exit("The stamped bundle breaks its contract:\n  " + "\n  ".join(problems))
    zip_dir(directory, args.zip)
    shutil.rmtree(directory)
    print(f"{args.zip}: version {args.version}{', notes ' + args.lang if args.notes else ''}")


def check(directory, manifest):
    problems = []

    def need(condition, message):
        if not condition:
            problems.append(message)

    def exists(rel, what):
        need(isinstance(rel, str) and os.path.isfile(os.path.join(directory, rel)), f"{what}: {rel!r} is not in the bundle")

    need(manifest.get("schema") == SCHEMA, f"schema is {manifest.get('schema')!r}, not {SCHEMA!r}")
    need(manifest.get("schemaVersion") == SCHEMA_VERSION,
         f"schemaVersion {manifest.get('schemaVersion')!r} is not one this reader knows ({SCHEMA_VERSION})")
    product = manifest.get("product") or {}
    need(isinstance(product.get("id"), str) and ID.match(product["id"]), f"product.id {product.get('id')!r} is not a lower-case id")
    need(isinstance(product.get("name"), str) and product["name"].strip(), "product.name is empty")
    need(isinstance(manifest.get("version"), str) and manifest["version"].strip(), "version is empty")
    need(isinstance(manifest.get("commit"), str) and re.fullmatch(r"[0-9a-f]{7,40}", manifest["commit"] or ""),
         f"commit {manifest.get('commit')!r} is not a commit id")
    languages = manifest.get("languages") or {}
    need(len(languages) > 0, "no language")
    for lang, content in languages.items():
        need(lang in LANGUAGES, f"language {lang!r} is not one of {sorted(LANGUAGES)}")
        exists((content.get("manual") or {}).get("pdf"), f"{lang} manual.pdf")
        if (content.get("manual") or {}).get("html") is not None:
            exists(content["manual"]["html"], f"{lang} manual.html")
        if content.get("notes") is not None:
            exists(content["notes"], f"{lang} notes")
        figure_ids = set()
        for figure in content.get("figures") or []:
            need(figure.get("id") not in figure_ids, f"{lang} figure id {figure.get('id')!r} appears twice")
            figure_ids.add(figure.get("id"))
            need(bool(squash(figure.get("caption"))), f"{lang} figure {figure.get('id')!r} has no caption")
            need(len(figure.get("files") or []) > 0, f"{lang} figure {figure.get('id')!r} has no file")
            for rel in figure.get("files") or []:
                exists(rel, f"{lang} figure {figure.get('id')!r}")
        feature_ids = set()
        for feature in content.get("features") or []:
            fid = feature.get("id")
            need(isinstance(fid, str) and ID.match(fid), f"{lang} feature id {fid!r} is not a lower-case id")
            need(fid not in feature_ids, f"{lang} feature id {fid!r} appears twice")
            feature_ids.add(fid)
            need(bool(squash(feature.get("title"))), f"{lang} feature {fid!r} has no title")
            need(bool(squash(feature.get("summary"))), f"{lang} feature {fid!r} has no summary")
            if feature.get("figure") is not None:
                need(feature["figure"] in figure_ids,
                     f"{lang} feature {fid!r} names figure {feature['figure']!r}, which the bundle does not carry")
    return problems


def validate(args):
    directory = unpacked(args.path)
    try:
        manifest = json.load(open(os.path.join(directory, "manifest.json")))
        problems = check(directory, manifest)
    finally:
        if directory != args.path:
            shutil.rmtree(directory)
    if problems:
        sys.exit(f"{args.path} breaks the site-bundle contract:\n  " + "\n  ".join(problems))
    print(f"{args.path}: a valid site bundle ({manifest['product']['id']} {manifest['version']}, "
          f"languages {', '.join(sorted(manifest['languages']))})")


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    sub = parser.add_subparsers(dest="command", required=True)
    b = sub.add_parser("build")
    b.add_argument("--out", required=True)
    b.add_argument("--product-id", required=True)
    b.add_argument("--product-name", required=True)
    b.add_argument("--lang", required=True, choices=sorted(LANGUAGES))
    b.add_argument("--typ", required=True)
    b.add_argument("--pdf", required=True)
    b.add_argument("--version", default="main")
    b.add_argument("--commit")
    b.add_argument("--root", default=".")
    s = sub.add_parser("stamp")
    s.add_argument("--zip", required=True)
    s.add_argument("--version", required=True)
    s.add_argument("--notes")
    s.add_argument("--lang", default="en", choices=sorted(LANGUAGES))
    v = sub.add_parser("validate")
    v.add_argument("path")
    args = parser.parse_args()
    {"build": build, "stamp": stamp, "validate": validate}[args.command](args)


if __name__ == "__main__":
    main()
