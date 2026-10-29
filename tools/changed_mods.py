"""Which public mods a push changed, for publish-merged.yml.

Prints a JSON list of mod folders (from tools/public-mods.json) whose sources, tests or registry
entries differ between two commits. Prints [] and a notice when the push also changed the SDK or
loader: those changes ship through the full channel workflow instead. Usage:

    python tools/changed_mods.py <before-sha> <after-sha>
"""
import json
import subprocess
import sys

FULL_CHANNEL = ("sdk/", "crates/")


def registry_at(commit):
    try:
        text = subprocess.run(["git", "show", f"{commit}:tools/public-mods.json"], capture_output=True, text=True, check=True).stdout
    except subprocess.CalledProcessError:
        return {}
    return {m["folder"]: m for m in json.loads(text)["mods"]}


PACKAGE_FIELDS = ("id", "name", "description", "legacy", "version", "assets")


def package(entry):
    return None if entry is None else {k: entry.get(k) for k in PACKAGE_FIELDS}


def changed(paths, before, after):
    """Mod folders to publish, or None when the push needs the full channel."""
    if any(p.startswith(FULL_CHANNEL) for p in paths):
        return None
    folders = []
    for path in paths:
        parts = path.split("/")
        if len(parts) >= 4 and parts[:2] == ["mods", "cs"]:
            name = parts[2].removesuffix(".Tests")
            if name in after and name not in folders:
                folders.append(name)
    # New registry entries, and edits to what the package contains or shows, publish too
    # (authors and the in-game id don't change the package).
    for name, entry in after.items():
        if package(before.get(name)) != package(entry) and name not in folders:
            folders.append(name)
    return folders


def main():
    before, after = sys.argv[1], sys.argv[2]
    if set(before) == {"0"}:
        before = f"{after}~1"
    paths = subprocess.run(["git", "diff", "--name-only", before, after], capture_output=True, text=True, check=True).stdout.split()
    folders = changed(paths, registry_at(before), registry_at(after))
    if folders is None:
        print("::notice::This push changed the SDK or loader; publish it with \"Build and publish public mods\".", file=sys.stderr)
        folders = []
    print(json.dumps(folders))


if __name__ == "__main__":
    main()
