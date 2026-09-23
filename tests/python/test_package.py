"""Offline release checks. Build artifacts are optional until build.ps1 is run."""

import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path, PurePosixPath
from unittest.mock import patch
from urllib.parse import urlsplit
from urllib.request import url2pathname


ROOT = Path(__file__).resolve().parents[2]
VERSION_PATH = ROOT / "GameData/KSPAutoCraft/KSPAutoCraft.version"
GAME_PREFIX = "GameData/KSPAutoCraft/"
GAME_ENTRIES = {
    GAME_PREFIX + "Plugins/KSPAutoCraft.dll",
    GAME_PREFIX + "KSPAutoCraft.version",
    GAME_PREFIX + "LICENSE",
    GAME_PREFIX + "README.md",
    "KSPAutoCraft.ckan",
}
PYTHON_MODULES = {"__init__.py", "__main__.py", "client.py", "physics.py", "performance.py", "llm.py", "contracts.py", "designer.py", "desktop.py", "gateway.py"}
GAME_ENTRIES.update(GAME_PREFIX + "Client/ksp_autocraft/" + name for name in PYTHON_MODULES)
CLIENT_ENTRIES = {
    "client/pyproject.toml",
    "client/ksp_autocraft/__init__.py",
    "client/ksp_autocraft/__main__.py",
    "client/ksp_autocraft/client.py",
    "client/ksp_autocraft/physics.py",
    "client/ksp_autocraft/performance.py",
    "client/ksp_autocraft/llm.py",
    "client/ksp_autocraft/contracts.py",
    "client/ksp_autocraft/designer.py",
    "client/ksp_autocraft/desktop.py",
    "client/ksp_autocraft/gateway.py",
    "examples/starter-stack.json",
    "docs/AIHUB.md",
    "docs/API.md",
    "docs/NATURAL-DESIGN.md",
    "docs/GAME-UI.md",
    "docs/PERFORMANCE.md",
    "docs/craft-plan.schema.json",
    "docs/CKAN-IMPORT-FIX.md",
    "README.md",
    "LICENSE",
}
POWERSHELL = shutil.which("powershell.exe")


def version_string(data):
    return ".".join(str(data[part]) for part in ("MAJOR", "MINOR", "PATCH"))


class PackageSourceTests(unittest.TestCase):
    def test_version_source_and_ksp_target(self):
        data = json.loads(VERSION_PATH.read_text(encoding="utf-8"))
        self.assertEqual(data["NAME"], "KSP AutoCraft")
        for key in ("VERSION", "KSP_VERSION"):
            for part in ("MAJOR", "MINOR", "PATCH"):
                self.assertIs(type(data[key][part]), int)
                self.assertGreaterEqual(data[key][part], 0)
        self.assertEqual(version_string(data["KSP_VERSION"]), "1.12.5")

    def test_project_targets_net472_without_bundling_game_references(self):
        project = ET.parse(ROOT / "src/KSPAutoCraft/KSPAutoCraft.csproj")
        self.assertEqual(project.findtext("./PropertyGroup/TargetFramework"), "net472")
        references = project.findall("./ItemGroup/Reference")
        self.assertTrue(references)
        for reference in references:
            with self.subTest(reference=reference.get("Include")):
                self.assertEqual(reference.findtext("Private").lower(), "false")


class PackageTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.version_data = json.loads(VERSION_PATH.read_text(encoding="utf-8"))
        cls.version = version_string(cls.version_data["VERSION"])
        cls.archive_path = ROOT / "dist" / f"KSPAutoCraft-{cls.version}.zip"
        cls.metadata_path = cls.archive_path.with_suffix(".ckan")
        cls.client_path = ROOT / "dist" / f"KSPAutoCraft-client-{cls.version}.zip"
        cls.repository_path = ROOT / "dist" / "KSPAutoCraft-local-repository.zip"
        if not any(path.exists() for path in (cls.archive_path, cls.metadata_path, cls.client_path)):
            raise unittest.SkipTest("Release artifacts are not built yet; run scripts/build.ps1 when ready.")

    def check_archive(self, archive, expected):
        names = archive.namelist()
        self.assertEqual(set(names), expected)
        self.assertEqual(len(names), len(expected), "Duplicate ZIP entries")
        self.assertEqual(names, sorted(names), "ZIP entries must be sorted ordinally")
        self.assertIsNone(archive.testzip())
        for entry in archive.infolist():
            with self.subTest(entry=entry.filename):
                name = entry.filename
                self.assertNotIn("\\", name)
                self.assertFalse(entry.is_dir())
                self.assertFalse(PurePosixPath(name).is_absolute())
                self.assertNotIn("..", PurePosixPath(name).parts)
                self.assertNotIn(":", name)
                self.assertEqual(entry.date_time, (1980, 1, 1, 0, 0, 0))
                self.assertFalse(entry.flag_bits & 1, "Encrypted ZIP entry")
                for part in PurePosixPath(name).parts:
                    lower = part.lower()
                    self.assertNotIn(lower, {
                        "plugindata", "__pycache__", ".pytest_cache", ".mypy_cache",
                        ".ruff_cache", ".git", ".venv", "venv", "build", "dist",
                        "settings.json", "secrets.json", "token", "token.txt", ".env",
                    })
                    self.assertFalse(lower.endswith((".egg-info", ".dist-info", ".pyc", ".pyo")))

    def test_all_outputs_exist(self):
        for path in (self.archive_path, self.metadata_path, self.client_path, self.repository_path):
            with self.subTest(path=path):
                self.assertTrue(path.is_file(), f"Incomplete release: {path}")

    def test_game_archive_is_only_install_payload_and_embedded_ckan(self):
        with zipfile.ZipFile(self.archive_path) as archive:
            self.check_archive(archive, GAME_ENTRIES)
            dlls = [name for name in archive.namelist() if name.lower().endswith(".dll")]
            self.assertEqual(dlls, [GAME_PREFIX + "Plugins/KSPAutoCraft.dll"])
            self.assertEqual(archive.read(GAME_PREFIX + "KSPAutoCraft.version"), VERSION_PATH.read_bytes())
            for name in ("LICENSE", "README.md"):
                self.assertEqual(archive.read(GAME_PREFIX + name), (ROOT / name).read_bytes())
            self.assertIn(b"MIT License", archive.read(GAME_PREFIX + "LICENSE"))
            for name in PYTHON_MODULES:
                self.assertEqual(archive.read(GAME_PREFIX + "Client/ksp_autocraft/" + name), (ROOT / "client/ksp_autocraft" / name).read_bytes())

    def test_local_repository_contains_only_this_release_metadata(self):
        with zipfile.ZipFile(self.repository_path) as archive:
            name = f"KSPAutoCraft/KSPAutoCraft-{self.version}.ckan"
            self.check_archive(archive, {name})
            self.assertEqual(archive.read(name), self.metadata_path.read_bytes())

    def test_plugin_is_built_pe_dll(self):
        with zipfile.ZipFile(self.archive_path) as archive:
            dll = archive.read(GAME_PREFIX + "Plugins/KSPAutoCraft.dll")
        self.assertGreaterEqual(len(dll), 64)
        self.assertEqual(dll[:2], b"MZ")
        pe_offset = struct.unpack_from("<I", dll, 0x3C)[0]
        self.assertGreaterEqual(pe_offset, 64)
        self.assertLessEqual(pe_offset + 24, len(dll))
        self.assertEqual(dll[pe_offset:pe_offset + 4], b"PE\x00\x00")
        characteristics = struct.unpack_from("<H", dll, pe_offset + 22)[0]
        self.assertTrue(characteristics & 0x2000, "PE image must be a DLL")
        built_dll = ROOT / "src/KSPAutoCraft/bin/Release/net472/KSPAutoCraft.dll"
        self.assertEqual(dll, built_dll.read_bytes())

    def test_ckan_metadata_matches_source_and_embedded_copy(self):
        raw = self.metadata_path.read_bytes()
        self.assertFalse(raw.startswith(b"\xef\xbb\xbf"), "CKAN JSON must be UTF-8 without BOM")
        metadata = json.loads(raw.decode("utf-8"))
        with zipfile.ZipFile(self.archive_path) as archive:
            self.assertEqual(archive.read("KSPAutoCraft.ckan"), raw)
            packaged_version = json.loads(archive.read(GAME_PREFIX + "KSPAutoCraft.version"))
        self.assertEqual(set(metadata), {
            "spec_version", "identifier", "name", "author", "abstract", "license",
            "version", "ksp_version", "download", "install", "depends",
        })
        self.assertIs(type(metadata["spec_version"]), int)
        self.assertEqual(metadata["spec_version"], 1)
        self.assertEqual(metadata["identifier"], "KSPAutoCraft")
        self.assertEqual(metadata["name"], "KSP AutoCraft")
        self.assertEqual(metadata["author"], "fubuki11st")
        self.assertEqual(metadata["license"], "MIT")
        self.assertIsInstance(metadata["abstract"], str)
        self.assertTrue(metadata["abstract"].strip())
        self.assertLessEqual(len(metadata["abstract"]), 200)
        self.assertEqual(metadata["version"], self.version)
        self.assertEqual(metadata["version"], version_string(packaged_version["VERSION"]))
        self.assertEqual(metadata["ksp_version"], version_string(self.version_data["KSP_VERSION"]))
        self.assertEqual(metadata["ksp_version"], version_string(packaged_version["KSP_VERSION"]))
        self.assertEqual(metadata["install"], [{"file": "GameData/KSPAutoCraft", "install_to": "GameData"}])
        self.assertEqual(metadata["depends"], [{"name": "KSPAIHub", "min_version": "0.4.0"}])

    def test_download_is_actual_archive_file_uri_or_absolute_https(self):
        metadata = json.loads(self.metadata_path.read_text(encoding="utf-8"))
        download = metadata["download"]
        self.assertIsInstance(download, str)
        uri = urlsplit(download)
        self.assertIn(uri.scheme, {"file", "https"})
        self.assertFalse(uri.fragment)
        self.assertIsNone(uri.username)
        self.assertIsNone(uri.password)
        self.assertFalse(any(character.isspace() for character in download))
        if uri.scheme == "file":
            self.assertFalse(uri.query)
            # Include the authority so UNC paths round-trip on Windows as well.
            uri_path = ("//" + uri.netloc if uri.netloc else "") + uri.path
            local_path = Path(url2pathname(uri_path))
            self.assertTrue(local_path.is_absolute())
            self.assertTrue(local_path.is_file())
            self.assertTrue(local_path.samefile(self.archive_path))
        else:
            self.assertTrue(uri.hostname)
            self.assertTrue(uri.netloc)
            if uri.port is not None:
                self.assertGreater(uri.port, 0)
                self.assertLessEqual(uri.port, 65535)

    def test_client_sources_are_separate_and_allowlisted(self):
        with zipfile.ZipFile(self.client_path) as archive:
            self.check_archive(archive, CLIENT_ENTRIES)
            for name in archive.namelist():
                with self.subTest(entry=name):
                    self.assertFalse(name.lower().endswith((".dll", ".ckan")))
                    self.assertFalse(name.startswith("GameData/"))
                    self.assertEqual(archive.read(name), (ROOT / name).read_bytes())


@unittest.skipUnless(os.name == "nt" and POWERSHELL, "Windows PowerShell 5.1 is required")
class BuildScriptTests(unittest.TestCase):
    """Exercise packaging with a stub dotnet, never compile or use an installed game."""

    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="KSP packaging # ")
        self.addCleanup(temporary.cleanup)
        self.workspace = Path(temporary.name)
        self.root = self.workspace / "source"
        for name in CLIENT_ENTRIES | {
            "scripts/build.ps1", "GameData/KSPAutoCraft/KSPAutoCraft.version",
            "src/KSPAutoCraft/KSPAutoCraft.csproj",
        }:
            destination = self.root / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            if name == "README.md" and not (ROOT / name).exists():
                destination.write_text("Packaging fixture documentation.\n", encoding="utf-8")
            else:
                shutil.copyfile(ROOT / name, destination)

        # A synthetic PE fixture tests the packaging contract, not compilation.
        dll = bytearray(128)
        dll[:2] = b"MZ"
        struct.pack_into("<I", dll, 0x3C, 64)
        dll[64:68] = b"PE\x00\x00"
        struct.pack_into("<H", dll, 86, 0x2000)
        binary = self.root / "src/KSPAutoCraft/bin/Release/net472/KSPAutoCraft.dll"
        binary.parent.mkdir(parents=True)
        binary.write_bytes(dll)
        for name in (
            "src/KSPAutoCraft/bin/Release/net472/UnityEngine.dll",
            "GameData/KSPAutoCraft/PluginData/settings.json",
            "client/ksp_autocraft/__pycache__/client.pyc",
            "client/ksp_autocraft.egg-info/PKG-INFO",
            "client/.env",
        ):
            forbidden = self.root / name
            forbidden.parent.mkdir(parents=True, exist_ok=True)
            forbidden.write_text("not for distribution", encoding="utf-8")

        self.ksp_root = self.workspace / "KSP game"
        assembly = self.ksp_root / "KSP_x64_Data/Managed/Assembly-CSharp.dll"
        assembly.parent.mkdir(parents=True)
        assembly.write_bytes(b"read-only game fixture")
        native_bin = self.workspace / "native tools"
        native_bin.mkdir()
        (native_bin / "dotnet.cmd").write_bytes(
            b'@echo off\r\n> "%PACKAGE_TEST_ARGS%" echo %*\r\nexit /b %PACKAGE_TEST_EXIT%\r\n'
        )
        self.argument_log = self.workspace / "build-arguments.txt"
        self.environment = dict(os.environ, PATH=str(native_bin) + os.pathsep + os.environ.get("PATH", ""))
        self.environment.update(PACKAGE_TEST_ARGS=str(self.argument_log), PACKAGE_TEST_EXIT="0")

    def run_script(self, *extra):
        return subprocess.run(
            [POWERSHELL, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
             "-File", str(self.root / "scripts/build.ps1"), "-KspRoot", str(self.ksp_root), *extra],
            cwd=self.workspace, env=self.environment, stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT, encoding="oem", errors="replace", timeout=60,
        )

    def check_generated_packages(self):
        with patch.multiple(sys.modules[__name__], ROOT=self.root,
                            VERSION_PATH=self.root / "GameData/KSPAutoCraft/KSPAutoCraft.version"):
            result = unittest.TestResult()
            unittest.defaultTestLoader.loadTestsFromTestCase(PackageTests).run(result)
        self.assertFalse(result.errors + result.failures, result.errors + result.failures)
        self.assertFalse(result.skipped)
        self.assertGreater(result.testsRun, 0)

    def test_local_packaging_is_deterministic_and_game_is_untouched(self):
        game_before = {path.relative_to(self.ksp_root): path.read_bytes()
                       for path in self.ksp_root.rglob("*") if path.is_file()}
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stdout)
        self.check_generated_packages()
        arguments = self.argument_log.read_text()
        self.assertIn("build", arguments)
        self.assertIn(str(self.root / "src/KSPAutoCraft/KSPAutoCraft.csproj"), arguments)
        self.assertIn("--configuration Release --framework net472", arguments)
        self.assertIn("/p:KspRoot=" + str(self.ksp_root), arguments)
        version = version_string(json.loads(VERSION_PATH.read_text(encoding="utf-8"))["VERSION"])
        self.assertIn("/p:Version=" + version, arguments)
        outputs = {path.name: path.read_bytes() for path in (self.root / "dist").iterdir()}
        self.assertEqual(set(outputs), {
            f"KSPAutoCraft-{version}.zip", f"KSPAutoCraft-{version}.ckan",
            f"KSPAutoCraft-client-{version}.zip",
            "KSPAutoCraft-local-repository.zip",
        })
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(outputs, {path.name: path.read_bytes() for path in (self.root / "dist").iterdir()})
        self.assertEqual(game_before, {path.relative_to(self.ksp_root): path.read_bytes()
                                      for path in self.ksp_root.rglob("*") if path.is_file()})

    def test_https_download_override(self):
        # This is a fixture URL; packaging validates syntax without network access.
        url = "https://downloads.example.org/releases/KSPAutoCraft.zip?asset=1"
        result = self.run_script("-DownloadUrl", url)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.check_generated_packages()
        metadata_path = next((self.root / "dist").glob("*.ckan"))
        self.assertEqual(json.loads(metadata_path.read_text(encoding="utf-8"))["download"], url)

    def test_locked_output_fails_before_build_and_preserves_release(self):
        import ctypes
        from ctypes import wintypes

        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stdout)
        outputs = {path.name: path.read_bytes() for path in (self.root / "dist").iterdir()}
        self.argument_log.unlink()
        archive = next(path for path in (self.root / "dist").glob("*.zip") if "-client-" not in path.name)
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                      wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        kernel.CreateFileW.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.CloseHandle.restype = wintypes.BOOL
        # GENERIC_READ + FILE_SHARE_READ reproduces a ZIP reader denying deletion.
        handle = kernel.CreateFileW(str(archive), 0x80000000, 1, None, 3, 0, None)
        self.assertNotEqual(handle, wintypes.HANDLE(-1).value)
        try:
            result = self.run_script()
        finally:
            kernel.CloseHandle(handle)
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn("Release output is in use or unreadable", result.stdout)
        self.assertFalse(self.argument_log.exists(), "Build must not start with a locked release output")
        self.assertEqual(outputs, {path.name: path.read_bytes() for path in (self.root / "dist").iterdir()})

    def test_native_failure_propagates_without_packaging(self):
        self.environment["PACKAGE_TEST_EXIT"] = "37"
        result = self.run_script()
        self.assertEqual(result.returncode, 37, result.stdout)
        self.assertFalse((self.root / "dist").exists())

    def test_invalid_download_is_rejected_before_build(self):
        for url in ("relative.zip", "http://downloads.example.org/mod.zip", "file:///C:/mod.zip",
                    "https://", "https://user:secret@example.org/mod.zip",
                    "https://example.org/mod.zip#fragment", "https://example.org:0/mod.zip"):
            with self.subTest(url=url):
                result = self.run_script("-DownloadUrl", url)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("absolute HTTPS URL", result.stdout)
                self.assertFalse(self.argument_log.exists())
                self.assertFalse((self.root / "dist").exists())


if __name__ == "__main__":
    unittest.main()
