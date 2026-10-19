"""Collect dependency copyright/license texts for the redistributable binaries."""
import argparse
import json
import pathlib
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("--out", required=True)
parser.add_argument("manifests", nargs="+")
args = parser.parse_args()
packages = {}
for manifest in args.manifests:
    metadata = json.loads(subprocess.check_output([
        "cargo", "metadata", "--locked", "--format-version", "1", "--manifest-path", manifest
    ], text=True))
    for package in metadata["packages"]:
        if package.get("source"):
            packages[package["id"]] = package
parts = ["Third-party Rust dependencies retain their own licenses.\n"
         "Original Jibbers loader/mod source: GPL-3.0-only (see LICENSE).\n"]
for package in sorted(packages.values(), key=lambda p: (p["name"], p["version"])):
    folder = pathlib.Path(package["manifest_path"]).parent
    texts = sorted(p for p in folder.iterdir() if p.is_file()
                   and p.name.upper().startswith(("LICENSE", "LICENCE", "COPYING", "NOTICE")))
    if package.get("license_file"):
        texts.append(folder / package["license_file"])
    parts.append(f"\n{'=' * 72}\n{package['name']} {package['version']}\n"
                 f"License: {package.get('license') or 'see below'}\n"
                 f"Source: {package.get('repository') or package['source']}\n")
    for path in dict.fromkeys(texts):
        parts.append(f"\n--- {path.name} ---\n{path.read_text(encoding='utf-8', errors='replace')}\n")
pathlib.Path(args.out).write_text("".join(parts), encoding="utf-8")
