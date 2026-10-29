"""Finds where a mod may clash with the catalog's other mods, from their C# source. Standard library only.

    python tools/conflicts.py                 every pair of catalog mods
    python tools/conflicts.py --mod Folder    one catalog mod against the others

Reads source, never runs it, so findings are hints for reviewers, not proof. It looks for:
  keys         the same default key (a rebindable setting, or a key the code reads directly)
  hook points  two mods wrapping or detouring the same game method, where one can skip the other
               (Before hooks only add work, so they never clash)
  game vars    two mods setting the same GameVars value
  declared     a mod naming the other in its Conflicts list (the SDK then loads only one)
submission.py adds this report to every submission's pull request.
"""
import argparse
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
REGISTRY = ROOT / "tools/public-mods.json"

KEY = r"Key\.(\w+)"
REBINDABLE = [
    re.compile(r"\[KeyBinding\(\s*\"(?:[^\"\\]|\\.)*\"\s*,\s*" + KEY),
    re.compile(r"Setting\.KeyBinding\(\s*\"[^\"]*\"\s*,\s*\"(?:[^\"\\]|\\.)*\"\s*,\s*\(int\)\s*" + KEY),
]
PRESSED = re.compile(r"Input\.OnPressed\(\s*" + KEY + r"(?:[^;]*?,\s*" + KEY + r"\s*\))?")
FIXED = re.compile(r"\b(?:Input\.IsDown|IsKeyDown|Input\.WasPressed)\(\s*" + KEY)
MODIFIERS = {"Control", "Shift", "Alt", "LeftControl", "RightControl", "LeftShift", "RightShift", "LeftAlt", "RightAlt"}

# Scene.FixedUpdateMethod.Around(...), Follow.LateUpdateMethod.Detour(...)
SYMBOL_HOOK = re.compile(r"\b((?:\w+\.)*\w+Method)\.(Around|Detour)\b")
# Raw detours: the method is found by name on a class looked up by name, earlier in the same file.
CLASS_LOOKUP = re.compile(r"(?:RequireClass|Il2Cpp\.Class|Unity\.Class)\(\s*(?:\"[^\"]*\"\s*,\s*)*\"([\w.]+)\"\s*\)")
METHOD_LOOKUP = re.compile(r"\.Method\(\s*\"([\w.]+)\"")
RAW_DETOUR = re.compile(r"\bDetour(?:<[^>]*>)?\.Create\(|\.Detour(?:<[^>]*>)?\(|\bMethodHook\.Create\(")
GAMEVAR_SET = re.compile(r"\bGameVars\.Set\(\s*\"([^\"]+)\"")
CONFLICTS = re.compile(r"override\s+IReadOnlyList<string>\s+Conflicts\s*=>\s*new\s*(?:string)?\s*\[\s*\]\s*\{([^}]*)\}")
MOD_ID = re.compile(r'override\s+string\s+Id\s*=>\s*"([^"]*)"')


def strip_comments(text):
    """C# source with comments blanked, keeping strings (and line numbers) intact."""
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("\n" * text.count("\n", i, j))
            i = j
        elif c == '"' or text.startswith('@"', i) or text.startswith('$"', i) or text.startswith('$@"', i) or text.startswith('@$"', i):
            verbatim = "@" in text[i:text.find('"', i)]
            j = text.find('"', i) + 1
            while j < n:
                if verbatim and text.startswith('""', j):
                    j += 2
                elif not verbatim and text[j] == "\\":
                    j += 2
                elif text[j] == '"':
                    j += 1
                    break
                else:
                    j += 1
            out.append(text[i:j])
            i = j
        elif c == "'":
            j = i + 1
            while j < n and text[j] != "'":
                j += 2 if text[j] == "\\" else 1
            out.append(text[i:j + 1])
            i = j + 1
        else:
            out.append(c)
            i += 1
    return "".join(out)


def line_of(text, index):
    return text.count("\n", 0, index) + 1


def footprint(directory, files=None, label=None):
    """What a mod's source touches: {"id", "conflicts", "keys", "hooks", "vars"}, each item with where it was found."""
    directory = pathlib.Path(directory)
    files = sorted(p.relative_to(directory) for p in directory.rglob("*.cs")) if files is None else [pathlib.Path(f) for f in files if str(f).endswith(".cs")]
    prefix = label or directory.as_posix()
    result = {"id": None, "conflicts": [], "keys": [], "hooks": [], "vars": []}
    for rel in files:
        if any(part in ("bin", "obj") for part in rel.parts):
            continue
        text = strip_comments((directory / rel).read_text(encoding="utf-8", errors="replace"))
        where = lambda m: f"{prefix}/{rel.as_posix()}:{line_of(text, m.start())}"  # noqa: E731
        if result["id"] is None and (m := MOD_ID.search(text)):
            result["id"] = m.group(1)
        if m := CONFLICTS.search(text):
            result["conflicts"] += re.findall(r'"([^"]+)"', m.group(1))
        for pattern in REBINDABLE:
            for m in pattern.finditer(text):
                result["keys"].append({"key": m.group(1), "kind": "default", "at": where(m)})
        for m in PRESSED.finditer(text):
            key = m.group(1) if not m.group(2) else f"{m.group(2)}+{m.group(1)}"
            result["keys"].append({"key": key, "kind": "fixed", "at": where(m)})
        for m in FIXED.finditer(text):
            if m.group(1) not in MODIFIERS:
                result["keys"].append({"key": m.group(1), "kind": "fixed", "at": where(m)})
        for m in SYMBOL_HOOK.finditer(text):
            result["hooks"].append({"target": m.group(1), "kind": m.group(2).lower(), "at": where(m)})
        if RAW_DETOUR.search(text):
            classes = [(c.start(), c.group(1).split(".")[-1]) for c in CLASS_LOOKUP.finditer(text)]
            first_detour = RAW_DETOUR.search(text).start()
            for m in METHOD_LOOKUP.finditer(text):
                if m.start() > first_detour:
                    continue
                owner = next((name for start, name in reversed(classes) if start < m.start()), None)
                if owner:
                    result["hooks"].append({"target": f"{owner}.{m.group(1)}", "kind": "detour", "at": where(m)})
        for m in GAMEVAR_SET.finditer(text):
            result["vars"].append({"name": m.group(1), "at": where(m)})
    return result


def compare(a, b):
    """Findings between two footprints: a list of (category, message)."""
    found = []
    if a["id"] and b["id"]:
        if b["id"] in a["conflicts"] or a["id"] in b["conflicts"]:
            found.append(("declared", "declared as conflicting: the SDK loads only one of them"))
    keys_b = {}
    for k in b["keys"]:
        keys_b.setdefault(k["key"], []).append(k)
    for k in a["keys"]:
        for other in keys_b.get(k["key"], []):
            if k["kind"] == "default" and other["kind"] == "default":
                note = "both are rebindable defaults; players must rebind one"
            elif k["kind"] == "default" or other["kind"] == "default":
                note = "one is a rebindable default, the other is read directly"
            else:
                note = "both read the key directly; it does both things at once"
            found.append(("keys", f"`{k['key']}`: `{k['at']}` and `{other['at']}` ({note})"))
    hooks_b = {}
    for h in b["hooks"]:
        hooks_b.setdefault(h["target"], []).append(h)
    for h in a["hooks"]:
        for other in hooks_b.get(h["target"], []):
            found.append(("hooks", f"`{h['target']}` is wrapped by both (`{h['at']}` {h['kind']}, `{other['at']}` {other['kind']}): "
                                   "if either skips the original, the other's code or the game's stops running"))
    vars_b = {}
    for v in b["vars"]:
        vars_b.setdefault(v["name"], []).append(v)
    for v in a["vars"]:
        for other in vars_b.get(v["name"], []):
            found.append(("vars", f"`{v['name']}` is set by both (`{v['at']}`, `{other['at']}`): the last write wins"))
    seen, unique = set(), []
    for item in found:
        if item not in seen:
            seen.add(item)
            unique.append(item)
    return unique


def catalog_footprints(registry, repo_root, skip=()):
    """(entry, footprint) for every catalog mod with source in the repository."""
    out = []
    for entry in registry["mods"]:
        folder = pathlib.Path(repo_root) / "mods/cs" / entry["folder"]
        if entry["folder"] in skip or not folder.is_dir():
            continue
        out.append((entry, footprint(folder, label=f"mods/cs/{entry['folder']}")))
    return out


def report(name, mine, others):
    """Markdown lines for one mod checked against the catalog. Empty footprint lists mean nothing to compare."""
    lines = ["**Conflicts with catalog mods** (advisory: read from source, so check each one)", ""]
    any_found = False
    for entry, theirs in others:
        if theirs["id"] and theirs["id"] == mine["id"]:
            continue
        findings = compare(mine, theirs)
        if not findings:
            continue
        any_found = True
        lines.append(f"- **{entry['name']}** (`{entry['folder']}`)")
        lines += [f"  - {category}: {message}" for category, message in findings]
    if not any_found:
        lines.append(f"- None found against {len(others)} catalog mods.")
    return lines


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--mod", help="folder under mods/cs to check against the others (default: every pair)")
    parser.add_argument("--registry", default=str(REGISTRY))
    parser.add_argument("--root", default=str(ROOT))
    parser.add_argument("--json", action="store_true", help="print footprints as JSON")
    args = parser.parse_args(argv)
    registry = json.loads(pathlib.Path(args.registry).read_text(encoding="utf-8"))
    mods = catalog_footprints(registry, args.root)
    if args.json:
        print(json.dumps({e["folder"]: f for e, f in mods}, indent=2))
        return 0
    if args.mod:
        mine = next(((e, f) for e, f in mods if e["folder"] == args.mod), None)
        if mine is None:
            print(f"No catalog mod in mods/cs/{args.mod}", file=sys.stderr)
            return 1
        print("\n".join(report(mine[0]["name"], mine[1], [m for m in mods if m is not mine])))
        return 0
    for i, (ea, fa) in enumerate(mods):
        for eb, fb in mods[i + 1:]:
            for category, message in compare(fa, fb):
                print(f"{ea['folder']} / {eb['folder']}  {category}: {message}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
