"""Fail CI if local-only mods would enter GitHub's automatically generated source archives."""
import subprocess

public_roots = {"JibbersAlpineVision", "JibbersAtmosphere", "JibbersReshred", "JibbersFPV", "JibbersModLoader"}
public_cs_ports = {"AlpineVision", "AlpineVision.Tests", "Atmosphere", "Reshred", "Rewind", "FPV"}
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
print("Public mod sources: Alpine Vision, FPV, Rewind and legacy Atmosphere test adapter.")
