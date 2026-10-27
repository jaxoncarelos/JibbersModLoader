"""Stage a catalog update that changes one public mod and nothing else.

Downloads the published catalog's files, builds and tests the mod against the SDK players already
have (the one inside the published runtime), and writes a spec plus payload for jibbers-publish.
Every other package stays byte-for-byte as published. Usage:

    python tools/publish_mod.py --mod SlowMo --previous catalog.json --out out/mod-channel

The mod must not need anything newer than the published SDK; to ship SDK changes, run the full
channel (mod-channel.yml).
"""
import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess
import sys
import time
import urllib.request
import zipfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import channel_versions  # noqa: E402

ROOT = pathlib.Path(__file__).resolve().parent.parent


def download(url, size, sha256, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    for attempt in range(4):
        try:
            with urllib.request.urlopen(url, timeout=120) as response:
                data = response.read()
            break
        except OSError:
            if attempt == 3:
                raise
            time.sleep(2 ** attempt)
    if len(data) != size or hashlib.sha256(data).hexdigest() != sha256:
        raise SystemExit(f"published file {url} does not match the catalog")
    destination.write_bytes(data)


def run(*command):
    print("+", " ".join(command), flush=True)
    subprocess.run(command, cwd=ROOT, check=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mod", required=True, help="folder under mods/cs, e.g. SlowMo")
    parser.add_argument("--previous", type=pathlib.Path, required=True, help="the published catalog.json")
    parser.add_argument("--out", type=pathlib.Path, required=True)
    parser.add_argument("--skip-tests", action="store_true")
    args = parser.parse_args()

    mods = json.loads((ROOT / "tools/public-mods.json").read_text(encoding="utf-8"))["mods"]
    entry = next((m for m in mods if m["folder"] == args.mod), None)
    if entry is None:
        raise SystemExit(f"{args.mod} is not a public mod; add it to tools/public-mods.json")
    previous = json.loads(args.previous.read_text(encoding="utf-8-sig"))
    out = args.out.resolve()
    if out.exists():
        shutil.rmtree(out)
    payload = out / "payload"

    # Everything already published, verified against the signed catalog's hashes.
    others = [p for p in previous["packages"] if p["id"] != entry["id"]]
    for package in others:
        for f in package["files"]:
            download(f["url"], f["size"], f["sha256"], payload / f["path"])
    print(f"Downloaded {sum(len(p['files']) for p in others)} published files")

    # Build against the SDK players run, not the repository's.
    sdk = out / "sdk"
    sdk.mkdir(parents=True)
    with zipfile.ZipFile(payload / "JibbersMods/packages/jibbers_dotnet/runtime.zip") as runtime:
        for name in ("Jibbers.Sdk.dll", "Jibbers.Sdk.xml"):
            if f"dotnet/{name}" in runtime.namelist():
                (sdk / name).write_bytes(runtime.read(f"dotnet/{name}"))
    sdk_dll = str(sdk / "Jibbers.Sdk.dll")
    properties = ["-c", "Release", "-p:JibbersNoInstall=true", f"-p:JibbersSdk={sdk_dll}"]
    folder = entry["folder"]
    if not args.skip_tests:
        run("dotnet", "run", "--project", f"mods/cs/{folder}.Tests", *properties)
    build = out / "build"
    run("dotnet", "build", f"mods/cs/{folder}", *properties, "-nologo", "-v", "q", "-o", str(build))

    dll = f"JibbersMods/{entry['id']}.dll" if entry["legacy"] else f"JibbersMods/cs/{entry['id']}.dll"
    (payload / dll).parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(build / f"{folder}.dll", payload / dll)
    files = [dll]
    for asset in entry["assets"]:
        (payload / asset["path"]).parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / asset["repo"], payload / asset["path"])
        files.append(asset["path"])

    published = next((p for p in previous["packages"] if p["id"] == entry["id"]), None)
    package = {
        "id": entry["id"],
        "name": entry["name"],
        "description": entry["description"],
        "version": entry.get("version") or (published["version"] if published else "0.1.0"),
        "dependencies": ["jibbers_dotnet"],
        "files": files,
    }
    packages = [
        {k: p[k] for k in ("id", "name", "description", "version")} | {"dependencies": p.get("dependencies", []), "files": [f["path"] for f in p["files"]]}
        for p in previous["packages"]
    ]
    index = next((i for i, p in enumerate(packages) if p["id"] == entry["id"]), None)
    if index is None:
        packages.append(package)
    else:
        packages[index] = package
    base_url = previous["packages"][0]["files"][0]["url"].rsplit("/", 1)[0]
    spec = {"sequence": previous["sequence"] + 1, "baseUrl": base_url, "packages": packages}
    report = channel_versions.settle(spec, payload, previous)
    others_changed = [i for i, (_, _, changed) in report.items() if changed and i != entry["id"]]
    if others_changed:
        raise SystemExit(f"only {entry['id']} may change, but {others_changed} differ from the published files")
    if not report[entry["id"]][2]:
        raise SystemExit(f"{entry['name']} is identical to the published version; nothing to publish")
    (out / "spec.json").write_text(json.dumps(spec, indent=2), encoding="utf-8")
    print(f"Staged {entry['name']} for catalog sequence {spec['sequence']}:")
    channel_versions.print_report(report)


if __name__ == "__main__":
    main()
