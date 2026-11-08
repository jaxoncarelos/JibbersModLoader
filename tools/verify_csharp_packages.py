"""Check the managed release layout and public allowlist without loading game code."""
import json
import pathlib
import sys
import zipfile

def verify_mod_assets(archive, assets, repository):
    """Require every declared asset, preserve its bytes, and reject extra bundles."""
    entries = {name.replace("\\", "/"): name for name in archive.namelist()}
    expected_bundles = {asset["zip"] for asset in assets if asset["zip"].endswith(".bundle")}
    assert {name for name in entries if name.endswith(".bundle")} == expected_bundles, "Unexpected or missing Unity bundle"
    for asset in assets:
        name = asset["zip"]
        assert name in entries, f"Missing mod asset: {name}"
        assert archive.read(entries[name]) == (repository / asset["repo"]).read_bytes(), f"Changed mod asset: {name}"


def main():
    dist = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "dist")
    registry = json.loads((pathlib.Path(__file__).resolve().parent / "public-mods.json").read_text(encoding="utf-8"))
    public = {m["folder"] for m in registry["mods"]}
    assets = [a for m in registry["mods"] for a in m["assets"]]
    for filename in ("JibbersCSharp.zip", "JibbersCSharpMods.zip", "JibbersCSharpSDK.zip"):
        with zipfile.ZipFile(dist / filename) as archive:
            names = {n.replace("\\", "/") for n in archive.namelist()}
            assert all(not n.startswith("/") and ".." not in n.split("/") for n in names), filename
            assert "LICENSE" in names, filename
            assert all(not n.endswith((".key", ".rs")) for n in names), filename
            if filename == "JibbersCSharp.zip":
                for required in ("version.dll", "JibbersMods/system/jibbers_loader.dll", "JibbersMods/jibbers_dotnet.dll",
                                 "JibbersMods/dotnet/Jibbers.Sdk.dll", "JibbersMods/dotnet/Jibbers.Sdk.runtimeconfig.json"):
                    assert required in names, (filename, required)
                assert archive.read("version.dll") != archive.read("JibbersMods/system/jibbers_loader.dll"), "Bootstrap split is missing"
                assert any(n.endswith("/hostfxr.dll") for n in names), "Build with -BundleRuntime for players"
                assert any(n.endswith("/coreclr.dll") for n in names), "Bundled runtime missing"
            elif filename == "JibbersCSharpMods.zip":
                assert "install.ps1" in names
                assert {n for n in names if n.endswith(".dll")} == {f"JibbersMods/cs/{p}/{p}.dll" for p in public}
                verify_mod_assets(archive, assets, pathlib.Path(__file__).resolve().parent.parent)
                assert "mods.json" in names, "install.ps1 reads the mod list from mods.json"
                assert not any(n.endswith(".json") and n != "mods.json" for n in names), "Player settings must not be packaged"
            else:
                projects = {n.split("/")[1] for n in names if n.startswith("mods/") and n.endswith(".csproj")}
                sources = public | set(registry.get("sourceOnly", []))
                assert projects == sources | {p + ".Tests" for p in sources}, (filename, projects)
                assert "mods/FPV/fpv_symbols.json" in names and "mods/TestRunner.cs" in names
                assert not any(n.startswith("Jibbers.Sdk/Features/") for n in names), "Mod code belongs in mods/, not the SDK"
                assert not any(n.endswith("/bin/") or "/obj/" in n for n in names)
                assert not any(n.split("/")[-1] in {"ModPortTests.cs", "SkiSmoothTests.cs", "FastLoadTests.cs", "CollisionStreamTests.cs"} for n in names)
            print(f"Verified {filename}: layout, licenses and public mod allowlist")


if __name__ == "__main__":
    main()
