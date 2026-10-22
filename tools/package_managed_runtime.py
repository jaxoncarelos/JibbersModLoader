"""Package the tested player runtime for old bootstrap-compatible catalog paths."""
import argparse
import hashlib
import json
from pathlib import Path
from zipfile import ZipFile, ZIP_DEFLATED

parser = argparse.ArgumentParser()
parser.add_argument("--player", type=Path, required=True)
parser.add_argument("--out", type=Path, required=True)
args = parser.parse_args()
args.out.parent.mkdir(parents=True, exist_ok=True)
manifest = {}
with ZipFile(args.player) as player, ZipFile(args.out, "w", ZIP_DEFLATED) as bundle:
    for entry in player.infolist():
        name = entry.filename.replace("\\", "/")
        prefix = "JibbersMods/dotnet/"
        if entry.is_dir() or not name.startswith(prefix):
            continue
        target = "dotnet/" + name[len(prefix):]
        if any(part in ("", ".", "..") for part in target.split("/")):
            raise ValueError("Unsafe runtime path")
        contents = player.read(entry)
        manifest[target] = hashlib.sha256(contents).hexdigest()
        bundle.writestr(target, contents)
    for required in ("dotnet/Jibbers.Sdk.dll", "dotnet/Jibbers.Sdk.runtimeconfig.json"):
        assert required in manifest, required
    assert any(name.endswith("/hostfxr.dll") for name in manifest), "Missing bundled hostfxr"
    assert any(name.endswith("/coreclr.dll") for name in manifest), "Missing bundled CoreCLR"
    bundle.writestr("manifest.json", json.dumps(manifest, sort_keys=True))
print(f"Packaged {len(manifest)} authenticated runtime files")
