"""Installer contract tests use a fake CKAN and isolated game directories."""
import ctypes
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile
from ctypes import wintypes

ROOT = Path(__file__).resolve().parents[2]
POWERSHELL = shutil.which("powershell.exe")


@unittest.skipUnless(os.name == "nt" and POWERSHELL, "Windows PowerShell required")
class LocalInstallTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="autocraft install ")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name) / "source"
        (self.root / "scripts").mkdir(parents=True)
        shutil.copyfile(ROOT / "scripts/install-local.ps1", self.root / "scripts/install-local.ps1")
        version = "GameData/KSPAutoCraft/KSPAutoCraft.version"
        (self.root / version).parent.mkdir(parents=True)
        (self.root / version).write_text(json.dumps({"VERSION": {"MAJOR": 0, "MINOR": 1, "PATCH": 0}}), encoding="utf-8")
        (self.root / "dist").mkdir()
        self.archive = self.root / "dist/KSPAutoCraft-0.1.0.zip"
        self.dll = b"isolated installer DLL fixture"
        with zipfile.ZipFile(self.archive, "w") as archive:
            archive.writestr("GameData/KSPAutoCraft/Plugins/KSPAutoCraft.dll", self.dll)
            archive.writestr(version, (self.root / version).read_bytes())
            archive.writestr("GameData/KSPAutoCraft/LICENSE", "MIT")
            archive.writestr("GameData/KSPAutoCraft/README.md", "fixture")
            archive.writestr("GameData/KSPAutoCraft/Client/ksp_autocraft/__main__.py", "# bundled fixture\n")
            archive.writestr("GameData/KSPAutoCraft/Client/ksp_autocraft/desktop.py", "# worker fixture\n")
        self.metadata = self.root / "dist/KSPAutoCraft-0.1.0.ckan"
        self.metadata.write_text(json.dumps({
            "identifier": "KSPAutoCraft", "version": "0.1.0", "download": "file:///missing/original-location.zip",
            "install": [{"file": "GameData/KSPAutoCraft", "install_to": "GameData"}],
        }), encoding="utf-8")
        repo_metadata = json.loads(self.metadata.read_text())
        repo_metadata["download"] = self.archive.as_uri()
        with zipfile.ZipFile(self.root / "dist/KSPAutoCraft-local-repository.zip", "w") as archive:
            archive.writestr("KSPAutoCraft/KSPAutoCraft-0.1.0.ckan", json.dumps(repo_metadata))
        self.game = Path(temporary.name) / "game"
        (self.game / "GameData").mkdir(parents=True)
        (self.game / "CKAN").mkdir()
        self.registry = self.game / "CKAN/registry.json"
        self.registry.write_text('{"installed_modules":{},"installed_files":{}}', encoding="utf-8")
        self.log = Path(temporary.name) / "invocation.json"
        self.stub = Path(temporary.name) / "fake-ckan.py"
        self.stub.write_text('''import json, os, sys, zipfile
from pathlib import Path
args = sys.argv[1:]
mode = os.environ.get('INSTALL_TEST_MODE', 'success')
if mode == 'fail': sys.exit(37)
if mode == 'empty': sys.exit(0)
game = Path(args[args.index('--gamedir') + 1])
if args[0] == 'repo':
    registry = json.loads((game / 'CKAN/registry.json').read_text())
    repos = registry.setdefault('sorted_repositories', {})
    if args[2] in repos: sys.exit(23)
    repos[args[2]] = {'uri': args[3]}
    (game / 'CKAN/registry.json').write_text(json.dumps(registry))
    sys.exit(0)
if args[0] == 'update': sys.exit(0)
if args[0] == 'upgrade':
    with zipfile.ZipFile(Path(os.environ['INSTALL_TEST_ARCHIVE']).parent / 'KSPAutoCraft-local-repository.zip') as repo:
        metadata = json.loads(repo.read('KSPAutoCraft/KSPAutoCraft-0.1.0.ckan'))
else:
    manifest = Path(args[args.index('--ckanfiles') + 1])
    metadata = json.loads(manifest.read_text(encoding='utf-8'))
Path(os.environ['INSTALL_TEST_LOG']).write_text(json.dumps({'args': args, 'metadata': metadata}))
with zipfile.ZipFile(os.environ['INSTALL_TEST_ARCHIVE']) as archive:
    archive.extractall(game)
    names = archive.namelist()
entry = {'source_module': metadata, 'installed_files': {name: {} for name in names}}
registry = json.loads((game / 'CKAN/registry.json').read_text())
registry.update({'installed_modules': {'KSPAutoCraft': entry}, 'installed_files': {name: 'KSPAutoCraft' for name in names}})
(game / 'CKAN/registry.json').write_text(json.dumps(registry), encoding='utf-8')
''', encoding="utf-8")
        self.ckan = Path(temporary.name) / "ckan.cmd"
        self.ckan.write_text(f'@echo off\n"{sys.executable}" "{self.stub}" %*\nexit /b %errorlevel%\n', encoding="utf-8")
        self.environment = dict(os.environ, INSTALL_TEST_LOG=str(self.log), INSTALL_TEST_ARCHIVE=str(self.archive))

    def run_script(self, *extra):
        return subprocess.run([
            POWERSHELL, "-NoProfile", "-NonInteractive", "-File", str(self.root / "scripts/install-local.ps1"),
            "-KspRoot", str(self.game), "-CkanPath", str(self.ckan), *extra,
        ], cwd=self.root, env=self.environment, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            encoding="oem", errors="replace", timeout=60)

    def test_success_uses_explicit_manifest_and_verifies_installation(self):
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stdout)
        invocation = json.loads(self.log.read_text())
        args = invocation["args"]
        self.assertEqual(args[0], "install")
        self.assertNotIn("import", args)
        self.assertIn("--no-recommends", args)
        self.assertIn("--headless", args)
        self.assertEqual(invocation["metadata"]["download"], self.archive.as_uri())
        self.assertIn("installation completed and verified", result.stdout)
        self.assertFalse(list((self.root / "dist").glob(".autocraft-install-*")))

    def test_exit_zero_without_registration_is_not_success(self):
        self.environment["INSTALL_TEST_MODE"] = "empty"
        result = self.run_script()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("not registered as installed", result.stdout)
        self.assertFalse(list((self.root / "dist").glob(".autocraft-install-*")))

    def test_native_failure_preserves_game_and_cleans_temporary_manifest(self):
        before = self.registry.read_bytes()
        self.environment["INSTALL_TEST_MODE"] = "fail"
        result = self.run_script()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("exit code 37", result.stdout)
        self.assertEqual(before, self.registry.read_bytes())
        self.assertFalse((self.game / "GameData/KSPAutoCraft").exists())
        self.assertFalse(list((self.root / "dist").glob(".autocraft-install-*")))

    def test_matching_install_is_idempotent(self):
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stdout)
        before = self.registry.read_bytes()
        self.log.unlink()
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("already installed and verified", result.stdout)
        self.assertFalse(self.log.exists())
        self.assertEqual(before, self.registry.read_bytes())

    def test_desktop_defaults_are_created_once_and_user_preferences_preserved(self):
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stdout)
        profile_path = self.game / "GameData/KSPAutoCraft/PluginData/desktop.json"
        profile = json.loads(profile_path.read_text(encoding="utf-8"))
        self.assertNotIn("autoCheckOnEnter", profile)
        self.assertNotIn("modelConfig", profile)
        self.assertTrue(profile["autoEnableGeneration"])
        self.assertEqual(profile["schemaVersion"], 2)
        self.assertTrue(Path(profile["pythonExecutable"]).is_file(), "Installer should resolve the actual Python interpreter")
        profile["autoEnableGeneration"] = False
        profile_path.write_text(json.dumps(profile), encoding="utf-8")
        before = profile_path.read_bytes()
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(before, profile_path.read_bytes())

    def test_legacy_model_settings_are_retired_with_backup_and_preferences_preserved(self):
        self.assertEqual(self.run_script().returncode, 0)
        path = self.game / 'GameData/KSPAutoCraft/PluginData/desktop.json'
        old = json.loads(path.read_text())
        old.update(schemaVersion=1, modelConfig='custom-user-model.json', autoCheckOnEnter=False, autoEnableGeneration=False)
        path.write_text(json.dumps(old))
        before = path.read_bytes()
        result = self.run_script()
        self.assertEqual(result.returncode, 0, result.stdout)
        current = json.loads(path.read_text())
        self.assertEqual(current, {'schemaVersion': 2, 'pythonExecutable': old['pythonExecutable'], 'autoEnableGeneration': False})
        backups = list((path.parent / 'Backups').glob('desktop-before-aihub-*.json'))
        self.assertEqual(len(backups), 1)
        self.assertEqual(backups[0].read_bytes(), before)
        self.assertEqual(self.run_script().returncode, 0)
        self.assertEqual(len(list((path.parent / 'Backups').glob('desktop-before-aihub-*.json'))), 1)

    def test_future_settings_are_not_overwritten_during_migration(self):
        self.assertEqual(self.run_script().returncode, 0)
        path = self.game / 'GameData/KSPAutoCraft/PluginData/desktop.json'
        path.write_text('{"schemaVersion":99,"future":"user data"}')
        before = path.read_bytes()
        self.assertNotEqual(self.run_script().returncode, 0)
        self.assertEqual(path.read_bytes(), before)

    def test_bundled_client_is_verified_not_just_the_dll(self):
        self.assertEqual(self.run_script().returncode, 0)
        client = self.game / "GameData/KSPAutoCraft/Client/ksp_autocraft/desktop.py"
        client.write_text("modified user file")
        self.log.unlink()
        result = self.run_script()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Bundled client file differs", result.stdout)
        self.assertFalse(self.log.exists())

    def test_changed_registered_dll_is_not_overwritten(self):
        self.assertEqual(self.run_script().returncode, 0)
        installed = self.game / "GameData/KSPAutoCraft/Plugins/KSPAutoCraft.dll"
        installed.write_bytes(b"different existing DLL")
        self.log.unlink()
        result = self.run_script()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Installed DLL differs", result.stdout)
        self.assertEqual(installed.read_bytes(), b"different existing DLL")
        self.assertFalse(self.log.exists())

    def test_upgrade_is_explicit_and_uses_indexed_exact_version(self):
        self.assertEqual(self.run_script().returncode, 0)
        registry = json.loads(self.registry.read_text())
        registry["installed_modules"]["KSPAutoCraft"]["source_module"]["version"] = "0.0.9"
        self.registry.write_text(json.dumps(registry), encoding="utf-8")
        self.log.unlink()
        blocked = self.run_script()
        self.assertNotEqual(blocked.returncode, 0)
        self.assertIn("-Upgrade", blocked.stdout)
        self.assertFalse(self.log.exists())
        result = self.run_script("-Upgrade")
        self.assertEqual(result.returncode, 0, result.stdout)
        args = json.loads(self.log.read_text())["args"]
        self.assertEqual(args[0], "upgrade")
        self.assertIn("KSPAutoCraft=0.1.0", args)
        self.assertNotIn("--ckanfile", args)
        self.assertNotIn("--ckanfiles", args)

    def test_active_registry_lock_is_respected(self):
        lock = self.game / "CKAN/registry.locked"
        lock.write_bytes(b"lock fixture")
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                      wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        kernel.CreateFileW.restype = wintypes.HANDLE
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel.CloseHandle.restype = wintypes.BOOL
        handle = kernel.CreateFileW(str(lock), 0x80000000, 0, None, 3, 0, None)
        self.assertNotEqual(handle, wintypes.HANDLE(-1).value)
        try:
            result = self.run_script()
        finally:
            kernel.CloseHandle(handle)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Exit its GUI normally", result.stdout)
        self.assertFalse(self.log.exists())
        self.assertEqual(lock.read_bytes(), b"lock fixture")


if __name__ == "__main__":
    unittest.main()
