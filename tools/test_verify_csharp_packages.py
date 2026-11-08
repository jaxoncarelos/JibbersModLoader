"""Regression coverage for mixed license and Unity-bundle release assets."""
import io
import pathlib
import tempfile
import unittest
import zipfile

from verify_csharp_packages import verify_mod_assets


class ModAssetsTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.repository = pathlib.Path(self.directory.name)
        self.assets = [
            {"zip": "effects.bundle", "repo": "effects.bundle"},
            {"zip": "packages/mod/NLayer-LICENSE.txt", "repo": "LICENSE.txt"},
        ]
        self.contents = {"effects.bundle": b"bundle", "packages/mod/NLayer-LICENSE.txt": b"MIT license notice"}
        for asset in self.assets:
            (self.repository / asset["repo"]).write_bytes(self.contents[asset["zip"]])

    def verify(self, contents):
        with zipfile.ZipFile(io.BytesIO(), "w") as archive:
            for name, data in contents.items():
                archive.writestr(name, data)
            verify_mod_assets(archive, self.assets, self.repository)

    def test_license_and_bundle_are_accepted(self):
        self.verify(self.contents)

    def test_missing_license_is_rejected(self):
        with self.assertRaisesRegex(AssertionError, "Missing mod asset"):
            self.verify({"effects.bundle": b"bundle"})

    def test_changed_license_is_rejected(self):
        with self.assertRaisesRegex(AssertionError, "Changed mod asset"):
            self.verify({**self.contents, "packages/mod/NLayer-LICENSE.txt": b"wrong notice"})

    def test_undeclared_bundle_is_rejected(self):
        with self.assertRaisesRegex(AssertionError, "Unexpected or missing Unity bundle"):
            self.verify({**self.contents, "private.bundle": b"not public"})

    def test_missing_bundle_is_rejected(self):
        with self.assertRaisesRegex(AssertionError, "Unexpected or missing Unity bundle"):
            self.verify({"packages/mod/NLayer-LICENSE.txt": b"MIT license notice"})


if __name__ == "__main__":
    unittest.main()
