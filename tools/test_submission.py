"""Tests for tools/submission.py and tools/changed_mods.py. Run: python tools/test_submission.py"""
import io
import json
import pathlib
import shutil
import stat
import sys
import tempfile
import unittest
import zipfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import changed_mods  # noqa: E402
import submission as s  # noqa: E402

MOD = '''using Jibbers.Sdk;

namespace NightLights;

public sealed class NightLightsMod : JibbersMod
{
    public override string Id => "night_lights";
    public override string Name => "Night Lights";
}
'''
PROJECT = '''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><AssemblyName>Whatever</AssemblyName><Version>2.1.0</Version></PropertyGroup>
  <Target Name="Sneaky" BeforeTargets="Build"><Exec Command="calc.exe" /></Target>
</Project>
'''
ISSUE = '''### Mod name

Night Lights

### Folder

NightLights

### Description

Lights up the
slopes at night.

### Source repository

_No response_

### Commit

_No response_

### Path in the repository

_No response_

### Source zip

_No response_

### Update

- [ ] This updates a mod already in the catalog (only its authors can update it).

### License

- [X] I wrote this mod (or have the right to submit it) and release it under GPL-3.0-only, like the rest of this repository.

### Notes for reviewers

_No response_
'''


def fields(**extra):
    base = {"name": "Night Lights", "folder": "NightLights", "description": "Lights up the slopes at night.",
            "repo": "", "commit": "", "path": "", "zip": "", "update": False, "license": True, "notes": "",
            "issue": 7, "author": "skier"}
    base.update(extra)
    return base


class Workspace(unittest.TestCase):
    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp())
        self.source = self.tmp / "source"
        self.repo = self.tmp / "repo"
        (self.repo / "mods/cs").mkdir(parents=True)
        (self.repo / "tools").mkdir()
        self.registry_path = self.repo / "tools/public-mods.json"
        shutil.copyfile(s.REGISTRY, self.registry_path)
        self.write("NightLights/NightLights.csproj", PROJECT)
        self.write("NightLights/NightLightsMod.cs", MOD)
        self.write("NightLights/README.md", "# Night Lights\n")

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def write(self, rel, text, data=None):
        p = self.source / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        if data is not None:
            p.write_bytes(data)
        else:
            p.write_text(text, encoding="utf-8")

    def registry(self):
        return s.load_registry(self.registry_path)

    def stage(self, **extra):
        out = self.tmp / "out"
        lines, entry, update = s.stage(fields(**extra), self.registry(), out, self.source, self.repo)
        return out, lines, entry, update

    def rejected(self, contains, **extra):
        with self.assertRaises(s.Rejected) as caught:
            self.stage(**extra)
        self.assertIn(contains, str(caught.exception))


class ParseTests(unittest.TestCase):
    def test_issue_form_answers(self):
        f = s.parse_issue(ISSUE)
        self.assertEqual(f["name"], "Night Lights")
        self.assertEqual(f["folder"], "NightLights")
        self.assertEqual(f["repo"], "")
        self.assertFalse(f["update"])
        self.assertTrue(f["license"])
        self.assertIn("slopes", f["description"])

    def test_zip_link_only_from_github_attachments(self):
        self.assertEqual(s.zip_link("see https://github.com/user-attachments/files/123/mod.zip thanks"),
                         "https://github.com/user-attachments/files/123/mod.zip")
        self.assertIsNone(s.zip_link("https://evil.example/files/1/mod.zip"))

    def test_registry_round_trips(self):
        text = s.REGISTRY.read_text(encoding="utf-8").replace("\r\n", "\n")
        self.assertEqual(s.dump_registry(s.load_registry()), text)


class StageTests(Workspace):
    def test_new_mod_is_staged_with_template_projects_and_a_smoke_test(self):
        out, lines, entry, update = self.stage()
        self.assertFalse(update)
        self.assertEqual(entry["id"], "jibbers_night_lights")
        self.assertEqual(entry["authors"], ["skier"])
        project = (out / "files/mods/cs/NightLights/NightLights.csproj").read_text(encoding="utf-8")
        self.assertEqual(project, s.mod_project("NightLights", "2.1.0"))
        self.assertNotIn("calc", project)
        self.assertEqual((out / "files/mods/cs/NightLights.Tests/NightLights.Tests.csproj").read_text(encoding="utf-8"), s.test_project("NightLights"))
        smoke = (out / "files/mods/cs/NightLights.Tests/SubmissionSmokeTests.cs").read_text(encoding="utf-8")
        self.assertIn("typeof(global::NightLights.NightLightsMod)", smoke)
        self.assertIn('"night_lights"', smoke)
        report = "\n".join(lines)
        self.assertIn("ignored `<Target>`", report)

    def test_their_tests_are_kept_without_their_test_runner(self):
        self.write("NightLights.Tests/NightLights.Tests.csproj", '<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../NightLights/NightLights.csproj" /></ItemGroup></Project>')
        self.write("NightLights.Tests/LightTests.cs", "static class LightTests { [Test] static void Works() { } }")
        self.write("NightLights.Tests/TestRunner.cs", "static class Program {}")
        out, *_ = self.stage()
        tests = out / "files/mods/cs/NightLights.Tests"
        self.assertTrue((tests / "LightTests.cs").exists())
        self.assertFalse((tests / "TestRunner.cs").exists())
        self.assertFalse((tests / "SubmissionSmokeTests.cs").exists())

    def test_advisory_scan_flags_native_calls(self):
        self.write("NightLights/Native.cs", 'static class N { [System.Runtime.InteropServices.DllImport("user32")] static extern int MessageBoxA(); }')
        _, lines, *_ = self.stage()
        self.assertIn("native code", "\n".join(lines))

    def test_bundles_become_assets(self):
        self.write("NightLights/lights.bundle", None, b"UnityFS\0bundle")
        out, lines, entry, _ = self.stage()
        self.assertEqual(entry["assets"], [{"path": "JibbersMods/packages/jibbers_night_lights/lights.bundle",
                                            "zip": "JibbersMods/packages/jibbers_night_lights/lights.bundle",
                                            "repo": "mods/cs/NightLights/lights.bundle"}])
        self.assertIn("compiled Unity asset bundle", "\n".join(lines))

    def test_license_is_required(self):
        self.rejected("license", license=False)

    def test_packages_are_rejected(self):
        self.write("NightLights/NightLights.csproj", '<Project><ItemGroup><PackageReference Include="Newtonsoft.Json" Version="13.0.1" /></ItemGroup></Project>')
        self.rejected("Newtonsoft.Json")

    def test_other_references_are_rejected(self):
        self.write("NightLights/NightLights.csproj", '<Project><ItemGroup><Reference Include="Evil"><HintPath>evil.dll</HintPath></Reference></ItemGroup></Project>')
        self.rejected("Only the Jibbers SDK")

    def test_binaries_and_scripts_are_rejected(self):
        self.write("NightLights/payload.dll", None, b"MZ")
        self.write("NightLights/build.ps1", "Remove-Item C:\\")
        self.write("NightLights/Directory.Build.props", "<Project />")
        with self.assertRaises(s.Rejected) as caught:
            self.stage()
        message = str(caught.exception)
        for name in ("payload.dll", "build.ps1", "Directory.Build.props"):
            self.assertIn(name, message)

    def test_hidden_folders_are_rejected(self):
        self.write("NightLights/.secret/x.cs", "class X {}")
        self.rejected("hidden")

    def test_oversized_files_are_rejected(self):
        self.write("NightLights/Big.cs", "//" + "x" * (s.MAX_CODE + 10))
        self.rejected("Big.cs")

    def test_dtds_are_rejected(self):
        self.write("NightLights/NightLights.csproj", '<!DOCTYPE p [<!ENTITY a "aaaa">]><Project>&a;</Project>')
        self.rejected("DTD")

    def test_a_mod_needs_a_constant_id(self):
        self.write("NightLights/NightLightsMod.cs", MOD.replace('"night_lights"', 'Compute()'))
        self.rejected("constant id")

    def test_one_mod_per_submission(self):
        self.write("NightLights/Other.cs", "public sealed class OtherMod : JibbersMod { public override string Id => \"other\"; }")
        self.rejected("Several")

    def test_folder_names_are_checked(self):
        self.rejected("PascalCase", folder="night-lights")
        self.rejected("PascalCase", folder="NightTests")

    def test_taken_folders_and_ids_are_rejected(self):
        self.rejected("already uses the folder", folder="Rumble")
        self.write("NightLights/NightLightsMod.cs", MOD.replace('"night_lights"', '"rumble"'))
        self.rejected("already taken")
        (self.repo / "mods/cs/NightLights").mkdir()
        self.write("NightLights/NightLightsMod.cs", MOD)
        self.rejected("already used in this repository")

    def test_updates_need_an_author(self):
        registry = self.registry()
        registry["mods"].append({"folder": "NightLights", "modId": "night_lights", "authors": ["skier"], "id": "jibbers_night_lights",
                                 "name": "Night Lights", "description": "Old.", "legacy": False, "assets": []})
        self.registry_path.write_text(s.dump_registry(registry), encoding="utf-8")
        self.rejected("tick **Update**")
        self.rejected("can update", update=True, author="someone-else")
        _, _, entry, update = self.stage(update=True)
        self.assertTrue(update)
        self.assertEqual(entry["description"], "Lights up the slopes at night.")
        _, _, entry, _ = self.stage(update=True, author="jaxoncarelos")  # maintainers may update any mod
        self.assertEqual(entry["authors"], ["skier"])
        self.write("NightLights/NightLightsMod.cs", MOD.replace('"night_lights"', '"renamed"'))
        self.rejected("keep the mod id", update=True)

    def test_update_of_a_missing_mod_is_rejected(self):
        self.rejected("no mod in", update=True)


class ZipTests(unittest.TestCase):
    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp())

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def zipped(self, entries):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w") as z:
            for name, data in entries:
                if isinstance(data, zipfile.ZipInfo):
                    z.writestr(data, "target")
                else:
                    z.writestr(name, data)
        return buffer.getvalue()

    def test_single_top_folder_is_unwrapped(self):
        s.extract_zip(self.zipped([("mod/NightLights/A.cs", "class A {}")]), self.tmp)
        self.assertTrue((self.tmp / "NightLights/A.cs").exists())

    def test_traversal_and_absolute_paths_are_refused(self):
        for bad in ("../evil.cs", "a/../../evil.cs", "/abs.cs", "C:/win.cs"):
            with self.assertRaises(s.Rejected, msg=bad):
                s.extract_zip(self.zipped([(bad, "x")]), self.tmp)

    def test_symlinks_are_refused(self):
        link = zipfile.ZipInfo("mod/link")
        link.external_attr = (stat.S_IFLNK | 0o777) << 16
        with self.assertRaises(s.Rejected):
            s.extract_zip(self.zipped([("mod/link", link)]), self.tmp)

    def test_zip_bombs_are_refused(self):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as z:
            z.writestr("big.cs", "0" * (s.MAX_ZIP_EXPANDED + 1))
        with self.assertRaises(s.Rejected):
            s.extract_zip(buffer.getvalue(), self.tmp)

    def test_downloads_only_from_github(self):
        with self.assertRaises(s.Rejected):
            s.download_zip("https://evil.example/user-attachments/files/1/mod.zip")


class ApplyTests(Workspace):
    def staged(self, **extra):
        out, *_ = self.stage(**extra)
        return out

    def test_apply_copies_files_and_registers_the_mod(self):
        out = self.staged()
        entry, update = s.apply(out, self.registry_path, "skier", self.repo)
        self.assertFalse(update)
        self.assertTrue((self.repo / "mods/cs/NightLights/NightLightsMod.cs").exists())
        self.assertTrue((self.repo / "mods/cs/NightLights.Tests/NightLights.Tests.csproj").exists())
        registry = self.registry()
        self.assertEqual(registry["mods"][-1]["id"], "jibbers_night_lights")
        # The rest of the registry is untouched, byte for byte.
        before = s.dump_registry(s.load_registry()).splitlines()
        after = self.registry_path.read_text(encoding="utf-8").splitlines()
        self.assertEqual(after[: len(before) - 3], before[: len(before) - 3])

    def test_files_outside_the_mod_are_refused(self):
        out = self.staged()
        evil = out / "files/.github/workflows/evil.yml"
        evil.parent.mkdir(parents=True)
        evil.write_text("on: push", encoding="utf-8")
        with self.assertRaises(s.Rejected):
            s.apply(out, self.registry_path, "skier", self.repo)

    def test_tampered_projects_are_refused(self):
        out = self.staged()
        project = out / "files/mods/cs/NightLights/NightLights.csproj"
        project.write_text(project.read_text(encoding="utf-8").replace("</Project>", "<Target Name=\"x\" /></Project>"), encoding="utf-8")
        with self.assertRaises(s.Rejected):
            s.apply(out, self.registry_path, "skier", self.repo)

    def test_tampered_entries_are_refused(self):
        for change in ({"id": "loader"}, {"authors": ["skier", "mallory"]}, {"legacy": True}, {"extra": 1},
                       {"assets": [{"path": "version.dll", "zip": "version.dll", "repo": "x"}]}):
            out = self.staged()
            entry = json.loads((out / "entry.json").read_text(encoding="utf-8"))
            entry.update(change)
            (out / "entry.json").write_text(json.dumps(entry), encoding="utf-8")
            with self.assertRaises(s.Rejected, msg=str(change)):
                s.apply(out, self.registry_path, "skier", self.repo)

    def test_a_different_author_cannot_apply_an_update(self):
        out = self.staged()
        s.apply(out, self.registry_path, "skier", self.repo)
        out = self.staged(update=True)
        with self.assertRaises(s.Rejected):
            s.apply(out, self.registry_path, "mallory", self.repo)
        s.apply(out, self.registry_path, "skier", self.repo)

    def test_updates_replace_the_folder(self):
        out = self.staged()
        s.apply(out, self.registry_path, "skier", self.repo)
        (self.source / "NightLights/README.md").unlink()
        out = self.staged(update=True)
        s.apply(out, self.registry_path, "skier", self.repo)
        self.assertFalse((self.repo / "mods/cs/NightLights/README.md").exists())


class ChangedModsTests(unittest.TestCase):
    after = {"Rumble": {"id": "jibbers_rumble"}, "Slvsh": {"id": "jibbers_slvsh"}, "New": {"id": "jibbers_new"}}
    before = {"Rumble": {"id": "jibbers_rumble"}, "Slvsh": {"id": "jibbers_slvsh"}}

    def test_sources_tests_and_new_entries(self):
        paths = ["mods/cs/Rumble/Rumble.cs", "mods/cs/Slvsh.Tests/SlvshTests.cs", "mods/cs/Local/Local.cs", "README.md"]
        self.assertEqual(changed_mods.changed(paths, self.before, self.after), ["Rumble", "Slvsh", "New"])

    def test_author_edits_alone_do_not_publish(self):
        after = {name: dict(entry, authors=["someone"]) for name, entry in self.before.items()}
        self.assertEqual(changed_mods.changed(["tools/public-mods.json"], self.before, after), [])

    def test_sdk_changes_need_the_full_channel(self):
        self.assertIsNone(changed_mods.changed(["mods/cs/Rumble/Rumble.cs", "sdk/dotnet/Jibbers.Sdk/X.cs"], self.before, self.after))


if __name__ == "__main__":
    unittest.main(verbosity=1)
