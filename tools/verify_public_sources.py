"""Fail CI if local-only mods would enter GitHub's automatically generated source archives."""
import json
import pathlib
import subprocess

# Unity asset projects of the public mods.
public_roots = {"JibbersAlpineVision", "JibbersAtmosphere", "JibbersFPV"}
registry = json.loads((pathlib.Path(__file__).resolve().parent / "public-mods.json").read_text(encoding="utf-8"))
public_cs_ports = {m["folder"] for m in registry["mods"]} | set(registry.get("sourceOnly", []))
public_cs_ports |= {p + ".Tests" for p in public_cs_ports}
paths = subprocess.check_output(["git", "ls-files", "-z"], text=True).split("\0")
blocked = []
for path in paths:
    parts = path.split("/")
    if len(parts) < 3 or parts[0] != "mods":
        continue
    if parts[1] == "cs":
        if len(parts) >= 4 and parts[2] not in public_cs_ports:
            blocked.append(path)
    elif parts[1] not in public_roots:
        blocked.append(path)
if blocked:
    raise SystemExit("Local-only mods must stay out of public source releases:\n" + "\n".join(blocked))
print("Public mod sources: " + ", ".join(sorted(public_cs_ports)))
