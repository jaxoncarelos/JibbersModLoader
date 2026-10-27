"""Settle package versions against the published catalog before signing.

The publisher refuses a catalog that changes a package's files without raising its version, or
that lowers one. Both publish paths (the full channel and publish_mod.py) run this on their spec:
a package whose files match the published ones keeps the published version, and a changed package
gets a version above the published one. Usage:

    python tools/channel_versions.py --spec spec.json --root payload --previous catalog.json
"""
import argparse
import hashlib
import json
import pathlib


def version(text):
    parts = text.split(".")
    if len(parts) != 3 or not all(p.isdigit() for p in parts):
        raise ValueError(f"invalid version {text}")
    return tuple(int(p) for p in parts)


def file_hash(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def settle(spec, root, previous):
    """Adjusts spec['packages'][*]['version'] in place; returns {id: (old, new, changed)} for the log."""
    published = {p["id"]: p for p in previous["packages"]}
    report = {}
    for package in spec["packages"]:
        old = published.get(package["id"])
        if old is None:
            report[package["id"]] = (None, package["version"], True)
            continue
        hashes = {path: file_hash(root / path) for path in package["files"]}
        same = sorted(hashes) == sorted(f["path"] for f in old["files"]) and all(hashes[f["path"]] == f["sha256"] for f in old["files"])
        wanted = package["version"]
        if same:
            package["version"] = old["version"]
        elif version(wanted) <= version(old["version"]):
            major, minor, patch = version(old["version"])
            package["version"] = f"{major}.{minor}.{patch + 1}"
        report[package["id"]] = (old["version"], package["version"], not same)
    return report


def print_report(report):
    for package_id, (old, new, changed) in report.items():
        if old is None:
            print(f"  {package_id}: new at {new}")
        elif changed:
            print(f"  {package_id}: {old} -> {new}")
        else:
            print(f"  {package_id}: unchanged at {new}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--spec", type=pathlib.Path, required=True)
    parser.add_argument("--root", type=pathlib.Path, required=True)
    parser.add_argument("--previous", type=pathlib.Path, required=True)
    args = parser.parse_args()
    spec = json.loads(args.spec.read_text(encoding="utf-8-sig"))
    previous = json.loads(args.previous.read_text(encoding="utf-8-sig"))
    if spec["sequence"] <= previous["sequence"]:
        spec["sequence"] = previous["sequence"] + 1
    report = settle(spec, args.root, previous)
    args.spec.write_text(json.dumps(spec, indent=2), encoding="utf-8")
    print(f"Catalog sequence {spec['sequence']}:")
    print_report(report)


if __name__ == "__main__":
    main()
