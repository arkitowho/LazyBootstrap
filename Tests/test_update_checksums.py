"""Python 工具测试；设置 UPDATE_TEST_WORKER 可同时验证普通/NativeAOT 更新引擎。"""

import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import zipfile

REPO = Path(__file__).resolve().parents[1]
sys.dont_write_bytecode = True
sys.path.insert(0, str(REPO / "Tools"))
import generate_update_checksums as tool


class ChecksumToolTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="LazyBootstrap-checksum-tool-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.package = self.root / "package"
        self.package.mkdir()
        self.write_manifest(self.package, [{"type": "delete", "target": "contents/missing"}])

    @staticmethod
    def write_manifest(package, operations):
        (package / "update.json").write_text(json.dumps({"schemaVersion": 1,
                                                        "operations": operations}), encoding="utf-8")

    def generate(self, package=None):
        with contextlib.redirect_stdout(io.StringIO()):
            return tool.generate(str(package or self.package))

    def run_process(self, args, *, environment=None):
        return subprocess.run([str(arg) for arg in args], input=b"", stdout=subprocess.PIPE,
                              stderr=subprocess.STDOUT, timeout=60,
                              env=environment,
                              creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))

    def verify_for_launch(self, worker, game, staging):
        with subprocess.Popen([str(worker), "verify", str(game), str(staging)],
                              stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                              creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)) as process:
            output, _ = process.communicate(timeout=60)
            self.assertEqual(process.returncode, 0, output)
            self.last_launcher_pid = process.pid
        package = output.decode("utf-8-sig").strip()
        self.assertTrue(Path(package).is_dir())
        return package

    def make_game(self):
        game = self.root / "game"
        (game / "contents").mkdir(parents=True)
        (game / "asphyxia").mkdir()
        return game, game / ".media-update" / "update_tmp"

    def test_unicode_empty_large_hidden_and_stable_output(self):
        folder = self.package / "source" / "中文 日本"
        folder.mkdir(parents=True)
        (folder / "abc").write_bytes(b"abc")
        (folder / "empty").write_bytes(b"")
        (folder / "large").write_bytes(bytes(range(256)) * 20000)
        (self.package / ".hidden").write_bytes(b"hidden")
        (folder / "checksums.json").write_bytes(b"nested-payload")
        output = self.generate()
        before = output.read_bytes()
        self.assertFalse(before.startswith(b"\xef\xbb\xbf"))
        document = json.loads(before)
        self.assertEqual(document["algorithm"], "SHA256")
        paths = [entry["path"] for entry in document["files"]]
        self.assertEqual(paths, sorted(paths))
        self.assertNotIn("checksums.json", paths)
        self.assertIn("source/中文 日本/checksums.json", paths)
        self.assertIn(".hidden", paths)
        for entry in document["files"]:
            self.assertEqual(entry["sha256"], hashlib.sha256((self.package / entry["path"]).read_bytes()).hexdigest())
        self.generate()
        self.assertEqual(before, output.read_bytes())

    def test_wrapper_and_only_delete_package(self):
        output = self.generate(self.root)
        self.assertEqual(output, self.package / "checksums.json")
        self.assertEqual(len(json.loads(output.read_bytes())["files"]), 1)

    def test_missing_or_multiple_manifests(self):
        (self.package / "update.json").unlink()
        with self.assertRaises(ValueError):
            self.generate()
        (self.package / "update.json").write_text("{}")
        (self.package / "nested").mkdir()
        (self.package / "nested" / "UPDATE.JSON").write_text("{}")
        with self.assertRaises(ValueError):
            self.generate()

    def test_file_outside_package_rejected(self):
        (self.root / "outside.txt").write_text("outside")
        with self.assertRaises(ValueError):
            self.generate(self.root)
        self.assertFalse((self.package / "checksums.json").exists())

    def test_invalid_windows_paths(self):
        for path in ("../outside", "C:/outside", "/root", "a//b", "a/./b", "a\\b", "source/CON.txt",
                     "source/COM¹", "source/file:stream", "source/a.", "source/a ", "a/\x00"):
            with self.subTest(path=path), self.assertRaises(ValueError):
                tool.validate_relative(path)

    def test_scan_case_conflict(self):
        original = Path.iterdir

        def duplicate(directory):
            children = list(original(directory))
            return iter(children + children) if directory == self.package else iter(children)

        with mock.patch.object(Path, "iterdir", duplicate), self.assertRaisesRegex(ValueError, "大小写冲突"):
            self.generate()

    @unittest.skipUnless(os.name == "nt", "Windows 目录联接测试")
    def test_junction_and_linked_input_rejected(self):
        target = self.root / "target"
        target.mkdir()
        link = self.package / "linked"
        command = "New-Item -ItemType Junction -Path '" + str(link).replace("'", "''") + "' -Target '" + str(target).replace("'", "''") + "' | Out-Null"
        result = self.run_process(["pwsh", "-NoProfile", "-Command", command])
        self.assertEqual(result.returncode, 0, result.stdout)
        try:
            with self.assertRaises(ValueError):
                self.generate()
            with self.assertRaises(ValueError):
                self.generate(link)
        finally:
            link.rmdir()

    def test_failed_replace_preserves_existing_manifest_and_cleans_temp(self):
        output = self.generate()
        original = output.read_bytes()
        (self.package / "new").write_text("new")
        with mock.patch.object(tool.os, "replace", side_effect=OSError("injected write failure")), self.assertRaises(OSError):
            self.generate()
        self.assertEqual(original, output.read_bytes())
        self.assertEqual(list(self.package.glob(".checksums-*.tmp")), [])

    def test_package_change_during_scan_preserves_previous_manifest(self):
        output = self.generate()
        original = output.read_bytes()
        scan = tool.scan
        calls = 0

        def changed(root):
            nonlocal calls
            calls += 1
            if calls == 2:
                (self.package / "extra").write_text("changed")
            return scan(root)

        with mock.patch.object(tool, "scan", changed), self.assertRaises(ValueError):
            self.generate()
        self.assertEqual(original, output.read_bytes())

    def test_cli_exit_status(self):
        result = self.run_process([sys.executable, REPO / "Tools/generate_update_checksums.py", self.package])
        self.assertEqual(result.returncode, 0, result.stdout)
        (self.package / "update.json").unlink()
        result = self.run_process([sys.executable, REPO / "Tools/generate_update_checksums.py", self.package])
        self.assertNotEqual(result.returncode, 0)

    @unittest.skipUnless(os.environ.get("UPDATE_TEST_WORKER"), "设置 UPDATE_TEST_WORKER 运行 C# 互通测试")
    def test_python_package_csharp_apply_and_checksum_rejection(self):
        worker = os.environ["UPDATE_TEST_WORKER"]
        game, staging = self.make_game()
        (game / "contents/files").mkdir()
        self.write_manifest(self.package, [{"type": "copy", "source": "source", "target": "contents/files"},
                                          {"type": "editXml", "target": "contents/config.xml",
                                           "edits": [{"action": "setValue", "xpath": "/config/@value", "value": "new"}]}])
        source = self.package / "source"
        source.mkdir()
        (source / "中文.txt").write_text("日本", encoding="utf-8")
        (source / "large").write_bytes(b"abc" * 1000000)
        self.generate()
        shutil.copytree(self.package, staging)
        config = game / "contents/config.xml"
        original = b"\xef\xbb\xbf<?xml version='1.0'?>\r\n<config value='old' />\r\n"
        config.write_bytes(original)
        package = self.verify_for_launch(worker, game, staging)
        result = self.run_process([worker, "apply", game, package])
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual((game / "contents/files/中文.txt").read_text(encoding="utf-8"), "日本")
        config.write_bytes(original)
        package = self.verify_for_launch(worker, game, staging)
        result = self.run_process([worker, "apply", game, package])
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn(b'new', config.read_bytes())
        config.write_bytes(original)
        (staging / "source/中文.txt").write_bytes(b"tampered")
        result = self.run_process([worker, "verify", game, staging])
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(config.read_bytes(), original)
        self.assertFalse((game / ".media-update/active").exists())

    @unittest.skipUnless(os.environ.get("UPDATE_TEST_UPDATER") and os.environ.get("UPDATE_TEST_WORKER"), "设置 UPDATE_TEST_UPDATER 验证发布产物和示例 ZIP")
    def test_example_zips_with_published_updater(self):
        updater = os.environ["UPDATE_TEST_UPDATER"]
        output = self.root / "zips"
        result = self.run_process(["pwsh", "-NoProfile", "-File", REPO / "Packaging/New-ExamplePackages.ps1", "-OutputDirectory", output])
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertEqual(len(list(output.glob("*.zip"))), 4)
        game, staging = self.make_game()
        config = game / "contents/lazy/spicetools.xml"
        config.parent.mkdir()
        config.write_text('<games><game name="Sound Voltex"><analogs><analog name="VOL-L" sensivity="1"/></analogs><options><option name="sp2x-windowborder" value=""/></options></game></games>', encoding="utf-8")
        for kind in ("copy", "mirror", "editXml", "delete"):
            with self.subTest(kind=kind):
                with zipfile.ZipFile(output / f"UPDATE_LAZY_KFC_example_{kind}.zip") as archive:
                    self.assertIn("checksums.json", archive.namelist())
                    archive.extractall(staging)
                # Test the unmodified release binary without elevation, against this test-owned directory only.
                # This does not test UAC presentation or grant the child any additional permissions.
                environment = dict(os.environ, __COMPAT_LAYER="RunAsInvoker")
                package = self.verify_for_launch(os.environ["UPDATE_TEST_WORKER"], game, staging)
                result = self.run_process([updater, "--game", game, "--package", package,
                                           "--parent-pid", self.last_launcher_pid], environment=environment)
                self.assertEqual(result.returncode, 0, result.stdout)
                self.assertIn("更新成功".encode("utf-8"), result.stdout)
                self.assertFalse(staging.exists())
                self.assertFalse((game / ".media-update/verified-package.json").exists())
                self.assertTrue((game / ".media-update/updater_log.txt").is_file())
                self.assertFalse((game / "updater_log.txt").exists())
                self.assertFalse((game / "update_tmp").exists())
                if kind == "editXml":
                    self.assertIn('value="/ENABLED"', config.read_text(encoding="utf-8"))
                    self.assertIn('sensivity="1.2"', config.read_text(encoding="utf-8"))
        self.assertFalse((game / "contents/update-example").exists())


if __name__ == "__main__":
    unittest.main()
