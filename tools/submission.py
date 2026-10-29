"""Turns a "Submit a mod" issue into reviewed source under mods/cs. Standard library only.

    python tools/submission.py parse --event $GITHUB_EVENT_PATH --out fields.json
    python tools/submission.py stage --fields fields.json --out out/submission [--source DIR]
    python tools/submission.py sdk --out out/sdk
    python tools/submission.py apply --staged out/submission --author LOGIN

`parse` reads the issue form. `stage` (untrusted job: read-only token, no secrets) fetches the source
at the pinned commit or the attached zip, enforces the file rules, regenerates both project files from
the repository's template so nothing runs at build time, and writes the staged files, the registry
entry and a review report. `sdk` extracts the SDK players run from the published catalog, to build
against. `apply` (trusted job, never runs submitted code) re-validates the staged files completely,
copies them into mods/cs and adds or updates the entry in tools/public-mods.json.
"""
import argparse
import hashlib
import io
import json
import os
import pathlib
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
REGISTRY = ROOT / "tools/public-mods.json"
CATALOG_URL = "https://github.com/jaxoncarelos/JibbersModLoader/releases/download/mod-channel-stable/catalog.json"

# What a submission may contain. Project files are regenerated, never copied.
CODE = {".cs", ".md", ".txt", ".json"}
IMAGES = {".png", ".jpg", ".jpeg", ".gif"}
BUNDLE = ".bundle"
ALLOWED = CODE | IMAGES | {BUNDLE}
MAX_CODE, MAX_IMAGE, MAX_BUNDLE = 1 << 20, 2 << 20, 25 << 20
MAX_TOTAL, MAX_FILES, MAX_BUNDLES = 40 << 20, 500, 3
MAX_ZIP, MAX_ZIP_EXPANDED, MAX_ZIP_ENTRIES = 30 << 20, 60 << 20, 2000
BENIGN_HIDDEN = {".gitignore", ".gitattributes", ".editorconfig"}
SKIPPED_DIRS = {"bin", "obj"}
RESERVED_FOLDERS = {"TestRunner", "Directory"}

FOLDER = re.compile(r"[A-Z][A-Za-z0-9]{1,39}")
MOD_ID = re.compile(r"[a-z0-9_]{2,40}")
VERSION = re.compile(r"\d{1,4}\.\d{1,5}\.\d{1,6}")
LOGIN = re.compile(r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})")
REPO = re.compile(r"https://github\.com/([A-Za-z0-9-]{1,39})/([A-Za-z0-9._-]{1,100}?)(?:\.git)?/?")
COMMIT = re.compile(r"[0-9a-f]{40}")
ZIP_LINK = re.compile(r"https://github\.com/(?:user-attachments/files/\d+|[A-Za-z0-9-]+/[A-Za-z0-9._-]+/files/\d+)/[^\s)\]\"'<>]+?\.zip")

# Spots reviewers should read closely. Advisory: none of these is forbidden.
SCAN = [
    (re.compile(r"\[(DllImport|LibraryImport)\b|\bNativeLibrary\.|GameNativeLibrary\b"), "native code"),
    (re.compile(r"\bProcess\.Start\b|\bProcessStartInfo\b"), "starts a process"),
    (re.compile(r"\b(File|Directory)\.(Delete|Move)\b|\.Delete\(\s*(true|recursive)"), "deletes or moves files"),
    (re.compile(r"\bHttpClient\b|\bWebClient\b|\bWebRequest\b|\bSocket\b|\bTcpClient\b|\bUdpClient\b|\bWebSocket\b"), "networking"),
    (re.compile(r"\bAssembly\.(Load|LoadFrom|LoadFile|UnsafeLoadFrom)\b|\bAssemblyLoadContext\b"), "loads assemblies"),
    (re.compile(r"\bRegistry\b|\bRegistryKey\b"), "Windows registry"),
    (re.compile(r"\bSystem\.Reflection\.Emit\b|\bILGenerator\b|\bDynamicMethod\b"), "generates code at runtime"),
    (re.compile(r"\bMarshal\.\w+|\bunsafe\b|\bstackalloc\b"), "raw memory"),
    (re.compile(r"\bEnvironment\.(SetEnvironmentVariable|Exit|FailFast)\b"), "process environment"),
]

LABELS = {
    "Mod name": "name", "Folder": "folder", "Description": "description", "Source repository": "repo",
    "Commit": "commit", "Path in the repository": "path", "Source zip": "zip", "Update": "update",
    "License": "license", "Notes for reviewers": "notes",
}


class Rejected(Exception):
    """The submission can't be accepted; the message tells the submitter why."""


# ---- templates (the only project files that enter the repository) ----

def mod_project(folder, version):
    return (
        '<Project Sdk="Microsoft.NET.Sdk">\n'
        "  <PropertyGroup>\n"
        f"    <AssemblyName>{folder}</AssemblyName>\n"
        f"    <Version>{version}</Version>\n"
        "  </PropertyGroup>\n"
        f'  <ItemGroup><InternalsVisibleTo Include="{folder}.Tests" /></ItemGroup>\n'
        "</Project>\n"
    )


def test_project(folder):
    return (
        '<Project Sdk="Microsoft.NET.Sdk">\n'
        "  <PropertyGroup><OutputType>Exe</OutputType><EnableDynamicLoading>false</EnableDynamicLoading></PropertyGroup>\n"
        "  <ItemGroup>\n"
        '    <Compile Include="../TestRunner.cs" />\n'
        f'    <ProjectReference Include="../{folder}/{folder}.csproj" />\n'
        '    <Reference Include="Jibbers.Sdk"><HintPath>$(JibbersSdk)</HintPath><Private>true</Private></Reference>\n'
        "  </ItemGroup>\n"
        "</Project>\n"
    )


def smoke_test(mod_class, namespace, mod_id):
    # Fully qualified: mods often share their namespace's name (namespace MyMod; class MyMod).
    mod_class = f"global::{namespace + '.' if namespace else ''}{mod_class}"
    return (
        "using System.Runtime.CompilerServices;\n"
        "\n"
        "// Written by the submission bot because the mod came without tests: it checks the mod can be\n"
        "// loaded and describes itself. Replace it with real tests of the mod's behaviour.\n"
        "static class SubmissionSmokeTests\n"
        "{\n"
        "    [Test]\n"
        "    static void TheModDescribesItself()\n"
        "    {\n"
        "        // Without running the constructor, which may expect the game.\n"
        f"        var mod = ({mod_class})RuntimeHelpers.GetUninitializedObject(typeof({mod_class}));\n"
        f'        Assert.Equal("{mod_id}", mod.Id);\n'
        '        Assert.True(mod.Name.Length > 0, "the mod has a name");\n'
        "    }\n"
        "}\n"
    )


# ---- the registry ----

def load_registry(path=REGISTRY):
    return json.loads(pathlib.Path(path).read_text(encoding="utf-8"))


def dump_registry(registry):
    """The registry in its hand-written layout, so automated edits leave small diffs."""
    def value(v):
        return json.dumps(v, ensure_ascii=False)

    def inline(obj):
        return "{ " + ", ".join(f"{value(k)}: {value(v)}" for k, v in obj.items()) + " }"

    out = ["{"]
    top = [(k, v) for k, v in registry.items() if k != "mods"]
    for k, v in top:
        out.append(f"  {value(k)}: {value(v)},")
    out.append('  "mods": [')
    for i, mod in enumerate(registry["mods"]):
        out.append("    {")
        items = list(mod.items())
        for j, (k, v) in enumerate(items):
            comma = "," if j < len(items) - 1 else ""
            if k == "assets" and v:
                out.append('      "assets": [')
                for a, asset in enumerate(v):
                    out.append(f"        {inline(asset)}{',' if a < len(v) - 1 else ''}")
                out.append(f"      ]{comma}")
            else:
                out.append(f"      {value(k)}: {value(v)}{comma}")
        out.append("    }" + ("," if i < len(registry["mods"]) - 1 else ""))
    out.append("  ]")
    out.append("}")
    return "\n".join(out) + "\n"


def entry_for(registry, folder):
    return next((m for m in registry["mods"] if m["folder"] == folder), None)


# ---- parse ----

def parse_issue(body):
    """The issue form's answers, keyed by field id. Empty answers are ''."""
    fields = {key: "" for key in LABELS.values()}
    for block in re.split(r"^### ", body or "", flags=re.M)[1:]:
        label, _, answer = block.partition("\n")
        key = LABELS.get(label.strip())
        if key is None:
            continue
        answer = answer.strip()
        if answer == "_No response_":
            answer = ""
        if key in ("update", "license"):
            fields[key] = bool(re.search(r"^- \[[xX]\]", answer, flags=re.M))
        else:
            fields[key] = answer
    return fields


def cmd_parse(args):
    event = json.loads(pathlib.Path(args.event).read_text(encoding="utf-8"))
    issue = event["issue"]
    fields = parse_issue(issue.get("body") or "")
    fields["issue"] = int(issue["number"])
    fields["author"] = issue["user"]["login"]
    pathlib.Path(args.out).write_text(json.dumps(fields, indent=2), encoding="utf-8")


# ---- fetching the source ----

def fetch_commit(repo, commit, into):
    m = REPO.fullmatch(repo.strip())
    if not m:
        raise Rejected("The source repository must be a public GitHub repository URL, like `https://github.com/you/your-mod`.")
    if not COMMIT.fullmatch(commit.strip().lower()):
        raise Rejected("The commit must be the full 40-character hash (copy it from the commit page on GitHub).")
    url = f"https://github.com/{m.group(1)}/{m.group(2)}.git"
    env = dict(os.environ, GIT_TERMINAL_PROMPT="0", GIT_LFS_SKIP_SMUDGE="1")
    git = ["git", "-c", "protocol.allow=never", "-c", "protocol.https.allow=always", "-c", "core.symlinks=false", "-c", "core.hooksPath=/dev/null"]
    into.mkdir(parents=True, exist_ok=True)
    try:
        subprocess.run(git + ["init", "-q", str(into)], check=True, env=env, capture_output=True)
        subprocess.run(git + ["-C", str(into), "fetch", "-q", "--depth", "1", url, commit.strip().lower()], check=True, env=env, capture_output=True, timeout=300)
        subprocess.run(git + ["-C", str(into), "checkout", "-q", "FETCH_HEAD"], check=True, env=env, capture_output=True)
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
        raise Rejected(f"Couldn't fetch commit `{commit.strip()}` from {url}. Is the repository public, and is the hash right?")
    shutil.rmtree(into / ".git", ignore_errors=True)
    return f"{repo.strip().removesuffix('.git').rstrip('/')}/tree/{commit.strip().lower()}"


def zip_link(text):
    m = ZIP_LINK.search(text or "")
    return m.group(0) if m else None


def download_zip(url):
    host = urllib.parse.urlparse(url).hostname
    if host != "github.com" or not ZIP_LINK.fullmatch(url):
        raise Rejected("Attach the zip by dragging it into the issue's Source zip box, so it's hosted on GitHub.")
    request = urllib.request.Request(url, headers={"User-Agent": "jibbers-mod-submission"})
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            data = response.read(MAX_ZIP + 1)
    except OSError as e:
        raise Rejected(f"Couldn't download the attached zip ({e}).")
    if len(data) > MAX_ZIP:
        raise Rejected(f"The zip is larger than {MAX_ZIP >> 20} MB.")
    return data


def extract_zip(data, into):
    """Extracts a source zip, refusing anything that could land outside `into` or isn't a plain file."""
    try:
        archive = zipfile.ZipFile(io.BytesIO(data))
    except zipfile.BadZipFile:
        raise Rejected("The attachment isn't a valid zip file.")
    members = archive.infolist()
    if len(members) > MAX_ZIP_ENTRIES:
        raise Rejected(f"The zip has more than {MAX_ZIP_ENTRIES} entries.")
    if sum(m.file_size for m in members) > MAX_ZIP_EXPANDED:
        raise Rejected(f"The zip expands to more than {MAX_ZIP_EXPANDED >> 20} MB.")
    names = []
    for m in members:
        name = m.filename.replace("\\", "/")
        parts = [p for p in name.split("/") if p not in ("", ".")]
        if name.startswith("/") or re.match(r"^[A-Za-z]:", name) or ".." in parts:
            raise Rejected(f"The zip contains an unsafe path: `{m.filename}`.")
        mode = (m.external_attr >> 16) & 0xFFFF
        if stat.S_ISLNK(mode):
            raise Rejected(f"The zip contains a symbolic link: `{m.filename}`.")
        if m.is_dir() or not parts:
            continue
        names.append((m, parts))
    # A zip of a folder has that folder as its only top-level entry: unwrap it.
    tops = {parts[0] for _, parts in names}
    strip = len(tops) == 1 and all(len(parts) > 1 for _, parts in names)
    for m, parts in names:
        target = into.joinpath(*(parts[1:] if strip else parts))
        target.parent.mkdir(parents=True, exist_ok=True)
        with archive.open(m) as source:
            target.write_bytes(source.read(MAX_ZIP_EXPANDED + 1))


# ---- finding and checking the projects ----

def hidden(path):
    return any(part.startswith(".") for part in path.parts)


def project_files(base):
    found = []
    for p in sorted(base.rglob("*.csproj")):
        rel = p.relative_to(base)
        if any(part in SKIPPED_DIRS or part.startswith(".") for part in rel.parts[:-1]):
            continue
        found.append(p)
    return found


def locate(base, folder):
    """(mod project, tests project or None)."""
    projects = project_files(base)
    mods = [p for p in projects if not p.name.endswith(".Tests.csproj")]
    tests = [p for p in projects if p.name.endswith(".Tests.csproj")]
    if not mods:
        raise Rejected("No mod project (.csproj) was found. Point **Path in the repository** at the folder holding it.")
    named = [p for p in mods if p.stem == folder]
    if named:
        mod = named[0]
    elif len(mods) == 1:
        mod = mods[0]
    else:
        raise Rejected(f"Several projects were found ({', '.join(p.name for p in mods)}). Name the mod's project `{folder}.csproj` or set the path.")
    test = next((p for p in tests if p.stem == f"{mod.stem}.Tests"), None) or (tests[0] if len(tests) == 1 else None)
    if test is not None and test.parent == mod.parent:
        raise Rejected("The tests project must be in its own folder beside the mod's, not inside it.")
    return mod, test


def read_project(path, tests):
    """(version, notes) from a submitted project file. Raises Rejected for what the template can't express."""
    text = path.read_text(encoding="utf-8-sig", errors="replace")
    if len(text) > 64 << 10 or "<!DOCTYPE" in text or "<!ENTITY" in text:
        raise Rejected(f"`{path.name}` is too large or uses DTDs.")
    try:
        root = ET.fromstring(text)
    except ET.ParseError as e:
        raise Rejected(f"`{path.name}` isn't valid XML ({e}).")
    notes, version = [], None
    for el in root.iter():
        tag = el.tag.split("}")[-1]
        if tag == "PackageReference":
            raise Rejected(f"`{path.name}` uses the NuGet package `{el.get('Include')}`. Packages aren't accepted: "
                           "every line that runs in players' games must be in the submission. Include the code you need instead.")
        if tag == "Reference" and el.get("Include") != "Jibbers.Sdk":
            raise Rejected(f"`{path.name}` references `{el.get('Include')}`. Only the Jibbers SDK may be referenced.")
        if tag == "ProjectReference" and not tests:
            raise Rejected(f"`{path.name}` references another project. A mod is a single project.")
        if tag in ("Target", "UsingTask", "Import", "Exec"):
            notes.append(f"ignored `<{tag}>` in `{path.name}`: project files are replaced by the repository's template")
        if tag == "Version" and el.text and VERSION.fullmatch(el.text.strip()):
            version = el.text.strip()
        if tag == "TargetFramework" and el.text and el.text.strip() != "net8.0":
            notes.append(f"`{path.name}` targets {el.text.strip()}; mods build for net8.0")
    return version, notes


def collect(directory, exclude=()):
    """Every submitted file under `directory` (relative paths), checked against the file rules."""
    files, problems = [], []
    for p in sorted(directory.rglob("*")):
        rel = p.relative_to(directory)
        if any(rel.parts[: len(e.parts)] == e.parts for e in exclude):
            continue
        if any(part in SKIPPED_DIRS for part in rel.parts[:-1]) or (p.is_dir() and p.name in SKIPPED_DIRS):
            continue
        if p.is_symlink():
            problems.append(f"`{rel.as_posix()}` is a symbolic link")
            continue
        if p.is_dir():
            if p.name.startswith(".") and p.name != ".":
                problems.append(f"`{rel.as_posix()}/` is a hidden folder")
            continue
        if hidden(rel):
            if p.name in BENIGN_HIDDEN and len(rel.parts) == 1:
                continue
            problems.append(f"`{rel.as_posix()}` is hidden")
            continue
        if p.suffix.lower() == ".csproj":
            continue  # replaced by the template
        files.append(rel)
    return files, problems


def check_files(directory, files, allow_bundles):
    problems, flags, total = [], [], 0
    bundles = 0
    for rel in files:
        ext = rel.suffix.lower()
        size = (directory / rel).stat().st_size
        total += size
        name = rel.as_posix()
        if ext not in ALLOWED or (ext == BUNDLE and not allow_bundles):
            problems.append(f"`{name}`: {ext or 'files without an extension'} isn't accepted (only C# source, docs, JSON, images and Unity asset bundles)")
            continue
        if not all(re.fullmatch(r"[A-Za-z0-9._ -]{1,100}", part) for part in rel.parts) or name.lower().endswith(".settings.json"):
            problems.append(f"`{name}`: use letters, digits, dots, dashes and underscores in file names (and no saved settings)")
            continue
        limit = MAX_BUNDLE if ext == BUNDLE else MAX_IMAGE if ext in IMAGES else MAX_CODE
        if size > limit:
            problems.append(f"`{name}` is {size >> 10} KB; the limit for {ext} files is {limit >> 20} MB")
        if ext == BUNDLE:
            bundles += 1
            flags.append(f"`{name}` is a compiled Unity asset bundle ({size >> 10} KB). It can't contain code, but check it matches its source.")
        if ext in CODE:
            try:
                (directory / rel).read_text(encoding="utf-8")
            except UnicodeDecodeError:
                problems.append(f"`{name}` isn't UTF-8 text")
    if len(files) > MAX_FILES:
        problems.append(f"{len(files)} files; at most {MAX_FILES}")
    if total > MAX_TOTAL:
        problems.append(f"{total >> 20} MB in total; at most {MAX_TOTAL >> 20} MB")
    if bundles > MAX_BUNDLES:
        problems.append(f"{bundles} asset bundles; at most {MAX_BUNDLES}")
    return problems, flags


def find_mod(directory, files):
    """(class, namespace, mod id, file) of the mod's JibbersMod class."""
    found = []
    for rel in files:
        if rel.suffix != ".cs":
            continue
        text = (directory / rel).read_text(encoding="utf-8")
        for m in re.finditer(r"^[^\n/]*?\bclass\s+(\w+)\s*(?:<[^>]*>)?\s*:\s*(?:global::)?(?:Jibbers\.Sdk\.)?JibbersMod\b", text, flags=re.M):
            if re.search(r"\babstract\b", m.group(0)):
                continue
            ns = re.search(r"^\s*namespace\s+([\w.]+)\s*[;{]", text, flags=re.M)
            mid = re.search(r'override\s+string\s+Id\s*(?:=>\s*|\{\s*get\s*(?:=>\s*|\{\s*return\s+)|\{\s*get;\s*\}\s*=\s*)"([^"]*)"', text)
            found.append((m.group(1), ns.group(1) if ns else "", mid.group(1) if mid else None, rel))
    if not found:
        raise Rejected("No class deriving from `JibbersMod` was found.")
    if len(found) > 1:
        raise Rejected(f"Several `JibbersMod` classes were found ({', '.join(f[0] for f in found)}); a submission is one mod.")
    cls, ns, mod_id, rel = found[0]
    if mod_id is None or not MOD_ID.fullmatch(mod_id):
        raise Rejected(f"`{cls}` needs a constant id of 2-40 lowercase letters, digits or underscores, like "
                       '`public override string Id => "night_lights";`.')
    return cls, ns, mod_id, rel


def scan(directory, files, prefix):
    hits = []
    for rel in files:
        if rel.suffix != ".cs":
            continue
        for n, line in enumerate((directory / rel).read_text(encoding="utf-8").splitlines(), 1):
            for pattern, what in SCAN:
                if pattern.search(line):
                    hits.append(f"`{prefix}/{rel.as_posix()}:{n}` {what}: `{line.strip()[:120]}`")
                    break
    return hits


def plural(n, word):
    return f"{n} {word}{'' if n == 1 else 's'}"


def clean_text(text, limit, what):
    text = " ".join((text or "").split())
    if not text:
        raise Rejected(f"The {what} is empty.")
    if len(text) > limit:
        raise Rejected(f"The {what} is longer than {limit} characters.")
    if any(ord(c) < 32 for c in text) or "<" in text or ">" in text:
        raise Rejected(f"The {what} can't contain control characters or angle brackets.")
    return text


def check_identity(fields, registry, author, mod_id, repo_root):
    """The registry entry this submission adds or replaces. Raises Rejected on any clash or permission problem."""
    folder = fields["folder"].strip()
    if not FOLDER.fullmatch(folder) or folder.endswith("Tests") or folder in RESERVED_FOLDERS:
        raise Rejected("The folder must be 2-40 letters and digits starting with a capital (PascalCase), like `NightLights`.")
    if folder in registry.get("sourceOnly", []):
        raise Rejected(f"`{folder}` is reserved.")
    name = clean_text(fields["name"], 40, "mod name")
    description = clean_text(fields["description"], 300, "description")
    existing = entry_for(registry, folder)
    if not LOGIN.fullmatch(author or ""):
        raise Rejected("The issue author couldn't be read.")
    if existing:
        if not fields.get("update"):
            raise Rejected(f"A mod already uses the folder `{folder}`. Pick another, or tick **Update** if it's yours.")
        allowed = set(existing.get("authors", [])) | set(registry.get("maintainers", []))
        if author.lower() not in {a.lower() for a in allowed}:
            raise Rejected(f"Only {', '.join('@' + a for a in sorted(allowed))} can update `{folder}`.")
        if mod_id != existing["modId"]:
            raise Rejected(f"An update must keep the mod id `{existing['modId']}` (found `{mod_id}`), or players' settings would be lost.")
        entry = dict(existing)
        entry["name"], entry["description"] = name, description
        return entry, True
    if fields.get("update"):
        raise Rejected(f"There's no mod in `{folder}` to update. Untick **Update** to submit a new mod.")
    if (pathlib.Path(repo_root) / "mods/cs" / folder).exists() or any(m["folder"].lower() == folder.lower() for m in registry["mods"]):
        raise Rejected(f"The folder `{folder}` is already used in this repository.")
    package = f"jibbers_{mod_id}"
    if any(m["modId"] == mod_id or m["id"] == package for m in registry["mods"]):
        raise Rejected(f"The mod id `{mod_id}` is already taken. Change your mod's `Id`.")
    entry = {"folder": folder, "modId": mod_id, "authors": [author], "id": package, "name": name,
             "description": description, "legacy": False, "assets": []}
    return entry, False


# ---- stage ----

def stage(fields, registry, out, source_dir=None, repo_root=ROOT):
    """Writes out/files, out/entry.json and returns the report lines. Raises Rejected."""
    if not fields.get("license"):
        raise Rejected("The license box must be ticked: everything in this repository is GPL-3.0-only.")
    folder = fields["folder"].strip()
    work = pathlib.Path(tempfile.mkdtemp(prefix="submission-"))
    try:
        if source_dir is not None:
            base, source = pathlib.Path(source_dir), "local source"
        elif fields.get("repo"):
            base = work / "source"
            source = fetch_commit(fields["repo"], fields.get("commit", ""), base)
        elif (link := zip_link(fields.get("zip", ""))):
            base = work / "source"
            base.mkdir()
            extract_zip(download_zip(link), base)
            source = link
        else:
            raise Rejected("Give a source repository and commit, or attach a zip of the source.")
        sub = (fields.get("path") or "").strip().strip("/")
        if sub:
            parts = pathlib.PurePosixPath(sub).parts
            if ".." in parts or sub.startswith("/") or not all(re.fullmatch(r"[A-Za-z0-9._ -]+", p) for p in parts):
                raise Rejected("The path must be a folder inside the repository, like `src/NightLights`.")
            base = base.joinpath(*parts)
            if not base.is_dir():
                raise Rejected(f"`{sub}` isn't a folder in the source.")
        mod_project_file, test_project_file = locate(base, folder)
        mod_dir = mod_project_file.parent
        version, notes = read_project(mod_project_file, tests=False)
        exclude = []
        if test_project_file is not None and test_project_file.parent.is_relative_to(mod_dir):
            exclude.append(test_project_file.parent.relative_to(mod_dir))
        mod_files, problems = collect(mod_dir, exclude)
        p2, flags = check_files(mod_dir, mod_files, allow_bundles=True)
        problems += p2
        test_files = []
        if test_project_file is not None:
            _, test_notes = read_project(test_project_file, tests=True)
            notes += test_notes
            test_dir = test_project_file.parent
            test_files, p3 = collect(test_dir)
            test_files = [f for f in test_files if f.name != "TestRunner.cs"]
            p4, _ = check_files(test_dir, test_files, allow_bundles=False)
            problems += p3 + p4
        if problems:
            raise Rejected("Some files can't be accepted:\n" + "\n".join(f"- {p}" for p in problems))
        cls, namespace, mod_id, mod_file = find_mod(mod_dir, mod_files)
        entry, update = check_identity(fields, registry, fields.get("author", ""), mod_id, repo_root)
        version = version or "1.0.0"

        files = out / "files"
        if files.exists():
            shutil.rmtree(files)
        target = files / "mods/cs" / folder
        tests_target = files / "mods/cs" / f"{folder}.Tests"
        for rel in mod_files:
            (target / rel).parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(mod_dir / rel, target / rel)
        (target / f"{folder}.csproj").write_text(mod_project(folder, version), encoding="utf-8", newline="\n")
        tests_target.mkdir(parents=True, exist_ok=True)
        generated_tests = test_project_file is None
        if generated_tests:
            (tests_target / "SubmissionSmokeTests.cs").write_text(smoke_test(cls, namespace, mod_id), encoding="utf-8", newline="\n")
        else:
            for rel in test_files:
                (tests_target / rel).parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(test_project_file.parent / rel, tests_target / rel)
        (tests_target / f"{folder}.Tests.csproj").write_text(test_project(folder), encoding="utf-8", newline="\n")
        entry["assets"] = [
            {"path": f"JibbersMods/packages/{entry['id']}/{rel.name}", "zip": f"JibbersMods/packages/{entry['id']}/{rel.name}",
             "repo": f"mods/cs/{folder}/{rel.as_posix()}"}
            for rel in mod_files if rel.suffix.lower() == BUNDLE
        ]
        if len({a["path"] for a in entry["assets"]}) != len(entry["assets"]):
            raise Rejected("Two asset bundles share a file name.")
        (out / "entry.json").write_text(json.dumps(entry, indent=2), encoding="utf-8")

        hits = scan(mod_dir, mod_files, f"mods/cs/{folder}")
        lines = [
            f"## {'Update to' if update else 'New mod:'} {entry['name']}",
            "",
            f"| | |\n|---|---|\n| Folder | `mods/cs/{folder}` |\n| Mod id | `{mod_id}` (catalog package `{entry['id']}`) |"
            f"\n| Mod class | `{(namespace + '.') if namespace else ''}{cls}` in `{mod_file.as_posix()}` |\n| Version | {version} |"
            f"\n| Source | {source} |\n| Submitted by | @{fields.get('author', '?')} |",
            "",
            f"> {entry['description']}",
            "",
            f"**{plural(len(mod_files), 'file')}** in the mod, **{plural(len(test_files) if not generated_tests else 1, 'file')}** in its tests"
            + (" (the bot wrote a smoke test: the mod came without tests)." if generated_tests else "."),
            "",
            "Both project files were replaced by the repository's template, so nothing in the submission runs at build time.",
        ]
        if notes:
            lines += ["", "**Project file notes**", ""] + [f"- {n}" for n in notes]
        if flags:
            lines += ["", "**Binary assets to check**", ""] + [f"- {f}" for f in flags]
            lines += ["- The mod must load its bundles from `JibbersMods/packages/" + entry["id"] + "/`, where the catalog installs them."]
        lines += ["", "**Read closely** (advisory: spots that use native code, processes, networking, file deletion, reflection or raw memory)", ""]
        lines += [f"- {h}" for h in hits[:60]] or ["- Nothing flagged."]
        if len(hits) > 60:
            lines.append(f"- …and {len(hits) - 60} more.")
        return lines, entry, update
    finally:
        shutil.rmtree(work, ignore_errors=True)


def cmd_stage(args):
    fields = json.loads(pathlib.Path(args.fields).read_text(encoding="utf-8"))
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    result = {"ok": False, "issue": fields.get("issue"), "author": fields.get("author"), "folder": ""}
    try:
        lines, entry, update = stage(fields, load_registry(args.registry), out, args.source)
        result.update(ok=True, folder=entry["folder"], update=update, name=entry["name"])
    except Rejected as e:
        lines = ["## This submission can't be accepted yet", "", str(e), "", "Edit the issue to fix it; the check runs again."]
    (out / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    (out / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print("\n".join(lines))
    return 0 if result["ok"] else 1


# ---- sdk ----

def cmd_sdk(args):
    sys.path.insert(0, str(ROOT / "tools"))
    from publish_mod import download  # noqa: E402

    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    with urllib.request.urlopen(CATALOG_URL, timeout=120) as response:
        catalog = json.loads(response.read().decode("utf-8-sig"))
    runtime = next(f for p in catalog["packages"] if p["id"] == "jibbers_dotnet" for f in p["files"] if f["path"].endswith("runtime.zip"))
    archive = out / "runtime.zip"
    download(runtime["url"], runtime["size"], runtime["sha256"], archive)
    with zipfile.ZipFile(archive) as z:
        for name in ("Jibbers.Sdk.dll", "Jibbers.Sdk.xml"):
            if f"dotnet/{name}" in z.namelist():
                (out / name).write_bytes(z.read(f"dotnet/{name}"))
    archive.unlink()
    print(out / "Jibbers.Sdk.dll")


# ---- apply (trusted: re-checks everything, runs nothing) ----

def validate_staged(staged, registry, author, repo_root):
    """The validated entry and file list of a staged submission. Raises Rejected."""
    entry = json.loads((staged / "entry.json").read_text(encoding="utf-8"))
    expected_keys = {"folder", "modId", "authors", "id", "name", "description", "legacy", "assets"}
    if not isinstance(entry, dict) or not set(entry) <= expected_keys | {"version"} or not expected_keys <= set(entry):
        raise Rejected("entry.json has unexpected fields")
    folder, mod_id = entry["folder"], entry["modId"]
    fields = {"folder": folder, "name": entry["name"], "description": entry["description"], "update": entry_for(registry, folder) is not None}
    checked, _ = check_identity(fields, registry, author, mod_id, repo_root)
    if entry["id"] != checked["id"] or entry["legacy"] != checked["legacy"] or entry.get("version") != checked.get("version"):
        raise Rejected("entry.json changes the package identity")
    if fields["update"] and entry["authors"] != checked["authors"]:
        raise Rejected("entry.json changes the mod's authors")
    if not fields["update"] and entry["authors"] != [author]:
        raise Rejected("entry.json names other authors")
    files_root = staged / "files"
    mod_prefix, test_prefix = f"mods/cs/{folder}/", f"mods/cs/{folder}.Tests/"
    files = []
    for p in sorted(files_root.rglob("*")):
        rel = p.relative_to(files_root).as_posix()
        if p.is_symlink():
            raise Rejected(f"{rel} is a symbolic link")
        if p.is_dir():
            continue
        if not (rel.startswith(mod_prefix) or rel.startswith(test_prefix)):
            raise Rejected(f"{rel} is outside the mod's folders")
        files.append(rel)
    mod_files = [pathlib.PurePosixPath(f[len(mod_prefix):]) for f in files if f.startswith(mod_prefix)]
    test_files = [pathlib.PurePosixPath(f[len(test_prefix):]) for f in files if f.startswith(test_prefix)]
    for rel in mod_files + test_files:
        if hidden(pathlib.Path(rel)) or any(part in SKIPPED_DIRS for part in rel.parts[:-1]):
            raise Rejected(f"{rel} is hidden or in a build folder")
    mod_project_file = pathlib.PurePosixPath(f"{folder}.csproj")
    test_project_file = pathlib.PurePosixPath(f"{folder}.Tests.csproj")
    if mod_project_file not in mod_files or test_project_file not in test_files:
        raise Rejected("a project file is missing")
    text = (files_root / mod_prefix / mod_project_file).read_text(encoding="utf-8")
    version = re.search(r"<Version>([^<]*)</Version>", text)
    if not version or not VERSION.fullmatch(version.group(1)) or text != mod_project(folder, version.group(1)):
        raise Rejected("the mod's project file isn't the repository's template")
    if (files_root / test_prefix / test_project_file).read_text(encoding="utf-8") != test_project(folder):
        raise Rejected("the tests' project file isn't the repository's template")
    others = [f for f in mod_files + test_files if f.suffix.lower() == ".csproj" and f not in (mod_project_file, test_project_file)]
    if others:
        raise Rejected(f"unexpected project files: {others}")
    plain_mod = [f for f in mod_files if f != mod_project_file]
    plain_tests = [f for f in test_files if f != test_project_file]
    problems, _ = check_files(files_root / mod_prefix, [pathlib.Path(f) for f in plain_mod], allow_bundles=True)
    p2, _ = check_files(files_root / test_prefix, [pathlib.Path(f) for f in plain_tests], allow_bundles=False)
    if problems + p2:
        raise Rejected("; ".join(problems + p2))
    bundles = sorted(f.as_posix() for f in plain_mod if f.suffix.lower() == BUNDLE)
    expected_assets = [{"path": f"JibbersMods/packages/{entry['id']}/{pathlib.PurePosixPath(b).name}",
                        "zip": f"JibbersMods/packages/{entry['id']}/{pathlib.PurePosixPath(b).name}",
                        "repo": f"mods/cs/{folder}/{b}"} for b in bundles]
    if sorted(entry["assets"], key=lambda a: a["repo"]) != sorted(expected_assets, key=lambda a: a["repo"]):
        raise Rejected("entry.json's assets don't match the staged bundles")
    entry["name"], entry["description"] = checked["name"], checked["description"]
    return entry, fields["update"]


def apply(staged, registry_path, author, repo_root=ROOT):
    staged, repo_root = pathlib.Path(staged), pathlib.Path(repo_root)
    registry = load_registry(registry_path)
    entry, update = validate_staged(staged, registry, author, repo_root)
    folder = entry["folder"]
    for name in (folder, f"{folder}.Tests"):
        target = repo_root / "mods/cs" / name
        if target.exists():
            shutil.rmtree(target)
        shutil.copytree(staged / "files/mods/cs" / name, target)
    index = next((i for i, m in enumerate(registry["mods"]) if m["folder"] == folder), None)
    if index is None:
        registry["mods"].append(entry)
    else:
        registry["mods"][index] = entry
    pathlib.Path(registry_path).write_text(dump_registry(registry), encoding="utf-8", newline="\n")
    return entry, update


def cmd_apply(args):
    try:
        entry, update = apply(args.staged, args.registry, args.author, args.root)
    except Rejected as e:
        print(f"Rejected: {e}", file=sys.stderr)
        return 1
    print(json.dumps({"folder": entry["folder"], "name": entry["name"], "update": update}))
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    p = sub.add_parser("parse")
    p.add_argument("--event", required=True)
    p.add_argument("--out", required=True)
    p = sub.add_parser("stage")
    p.add_argument("--fields", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--registry", default=str(REGISTRY))
    p.add_argument("--source", help="use this folder instead of fetching (testing)")
    p = sub.add_parser("sdk")
    p.add_argument("--out", required=True)
    p = sub.add_parser("apply")
    p.add_argument("--staged", required=True)
    p.add_argument("--author", required=True)
    p.add_argument("--registry", default=str(REGISTRY))
    p.add_argument("--root", default=str(ROOT))
    args = parser.parse_args(argv)
    return {"parse": cmd_parse, "stage": cmd_stage, "sdk": cmd_sdk, "apply": cmd_apply}[args.command](args) or 0


if __name__ == "__main__":
    sys.exit(main())
