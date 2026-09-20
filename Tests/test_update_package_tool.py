"""更新包工具：临时目录行为、模拟网络和可选 C#/Tk 互通测试。"""

from copy import deepcopy
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock
import zipfile

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "Tools/Update-Package-Tool"))
from manifest import Options, Rule, checksums, make_manifest, package_name, rule_operation, validate_version
from package_builder import Cancelled, build, content_candidates, prepare
import spice_download
import launcher_build
import asphyxia_download


class PackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="update-package-tool-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.original = self.root / "原始 增量"
        self.write(self.original / "data/中文.txt", "新内容")
        self.write(self.original / "prop/ea3-ident.xml", "<ea3_conf><soft><ext>1999010100</ext><dest>J</dest></soft></ea3_conf>")
        (self.original / "modules/空目录").mkdir(parents=True)
        self.output = self.root / "output"
        self.output.mkdir()
        self.options = Options(original=str(self.original), output=str(self.output),
                               old_version="2026071400", new_version="2026080500")
        self.repo = self.root / "repo"
        self.repo.mkdir()
        repo_patch = mock.patch.object(launcher_build, "REPO_ROOT", self.repo)
        repo_patch.start()
        self.addCleanup(repo_patch.stop)

    def write(self, path, text):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def generate(self):
        return build(prepare(self.options))

    def assert_clean(self):
        self.assertEqual(list(self.output.iterdir()), [])

    def test_default_options_and_checksum_contract(self):
        for name in ("download_spice", "include_prerelease", "edit_version", "build_launcher", "asphyxia_enabled"):
            self.assertFalse(getattr(self.options, name))
        self.assertFalse(Rule().enabled)
        self.options.rules = [Rule(enabled=False, target="../invalid")]
        result = self.generate()
        self.assertEqual(result.name, "UPDATE_LAZY_KFC_2026071400 to 2026080500")
        manifest = json.loads((result / "update").read_bytes())
        self.assertEqual(manifest, {"operations": [{"type": "copy", "source": "source/contents", "target": "contents"}]})
        self.assertTrue((result / "source/contents/modules/空目录").is_dir())
        document = json.loads((result / "checksums").read_bytes())
        self.assertEqual(set(document), {"algorithm", "files"})
        self.assertEqual(document["algorithm"], "SHA256")
        paths = {entry["path"] for entry in document["files"]}
        self.assertEqual(paths, {p.relative_to(result).as_posix() for p in result.rglob("*") if p.is_file()} - {"checksums"})
        for entry in document["files"]:
            self.assertEqual(entry["sha256"], hashlib.sha256((result / entry["path"]).read_bytes()).hexdigest())

    def test_wrapped_layout_and_ambiguous_roots(self):
        wrapper = self.root / "wrapper"
        wrapper.mkdir()
        shutil.move(str(self.original), wrapper / "contents")
        self.options.original = str(wrapper)
        self.assertEqual(content_candidates(str(wrapper)), [wrapper / "contents"])
        self.write(wrapper / "another/contents/data/file", "second")
        self.assertEqual(len(content_candidates(str(wrapper))), 2)
        with self.assertRaisesRegex(ValueError, "唯一"):
            prepare(self.options)
        self.options.content_root = str(wrapper / "contents")
        result = self.generate()
        self.assertTrue((result / "source/contents/data/中文.txt").is_file())
        self.assertFalse((result / "source/contents/another").exists())

    def test_custom_name_and_versions(self):
        self.options.suffix = "组件更新 中文"
        self.options.old_version = self.options.new_version = ""
        self.assertEqual(package_name(self.options), "UPDATE_LAZY_KFC_组件更新 中文")
        for bad in ("2026022900", "2026130100", "20260101", "２０２６０１０１００", "abcdefghij"):
            with self.subTest(bad=bad), self.assertRaises(ValueError):
                validate_version(bad)
        self.assertEqual(validate_version("2024022902"), "2024022902")
        for suffix in ("../bad", "a\\b", "foo.", "foo ", "a:b"):
            self.options.suffix = suffix
            with self.assertRaises(ValueError):
                package_name(self.options)

    def test_components_and_ordered_rules(self):
        extra = self.write(self.root / "extra.txt", "extra")
        self.options.build_launcher = self.options.asphyxia_enabled = True
        self.options.edit_version = True
        self.options.rules = [Rule(kind="delete", enabled=True, target="contents/old"),
                              Rule(kind="copy", enabled=True, source=str(extra), target="contents/new.txt"),
                              Rule(kind="editXml", enabled=True, target="contents/config.xml", xpath="/root/@a", value="")]
        def compile_launcher(progress, cancel):
            self.write(self.repo / "build/launcher/MediaUpdater.exe", "updater")
            self.write(self.repo / "build/launcher/LazyBootstrap.exe", "main")
            self.write(self.repo / "build/launcher/Libs/runtime.dll", "runtime")
            self.write(self.repo / "build/Launcher.exe", "entry")
        with mock.patch.object(launcher_build, "compile_launcher", side_effect=compile_launcher) as compiler:
            with mock.patch.object(asphyxia_download, "download", side_effect=self.fake_plugin) as plugin:
                result = self.generate()
                plugin.assert_called_once()
            compiler.assert_called_once()
        operations = json.loads((result / "update").read_bytes())["operations"]
        self.assertEqual([op["type"] for op in operations], ["copy", "mirror", "copy", "copy", "editXml", "delete", "copy", "editXml"])
        self.assertEqual(operations[4]["edits"][0]["value"], "2026080500")
        self.assertFalse((result / "source/contents/prop/ea3-ident.xml").exists())
        self.assertTrue((self.original / "prop/ea3-ident.xml").exists())
        self.assertEqual((result / "source/extras/0002/payload").read_text(), "extra")
        self.assertEqual((result / "source/启动.exe").read_text(), "entry")
        self.assertFalse((result / "source/Launcher.exe").exists())
        self.assertEqual((result / "source/launcher/Libs/runtime.dll").read_text(), "runtime")
        self.assertEqual((self.repo / "build/Launcher.exe").read_text(), "entry")
        self.assertEqual([op["target"] for op in operations[:4]], ["contents", "launcher", "启动.exe", asphyxia_download.PLUGIN_TARGET])
        self.assertEqual((result / asphyxia_download.PLUGIN_SOURCE / "index.ts").read_text(), "plugin")

    def fake_plugin(self, destination, workspace, progress, cancel):
        self.write(destination / "index.ts", "plugin")
        self.write(destination / "handlers/common.ts", "handler")
        return "kfc-test.zip"

    def test_plugin_is_opt_in_and_failure_cleans_staging(self):
        with mock.patch.object(asphyxia_download, "download") as download:
            result = self.generate()
            download.assert_not_called()
        self.assertFalse((result / "source/asphyxia").exists())
        self.options.suffix = "plugin-failed"
        self.options.asphyxia_enabled = True
        plan = prepare(self.options)
        def fail(destination, workspace, progress, cancel):
            self.fake_plugin(destination, workspace, progress, cancel)
            raise OSError("下载失败")
        with self.assertRaises(OSError):
            build(plan, plugin_downloader=fail)
        self.assertEqual(list(self.output.iterdir()), [result])
        self.assertFalse(plan.destination.exists())

    def test_downloaded_plugin_conflicts_checked_against_extra_rules(self):
        self.options.asphyxia_enabled = True
        extra = self.write(self.root / "file.txt", "file")
        self.options.rules = [Rule(enabled=True, source=str(extra), target=asphyxia_download.PLUGIN_TARGET + "/handlers")]
        plan = prepare(self.options)
        with self.assertRaisesRegex(ValueError, "冲突"):
            build(plan, plugin_downloader=self.fake_plugin)
        self.assert_clean()

    def seed_build(self):
        self.write(self.repo / "build/Launcher.exe", "entry")
        self.write(self.repo / "build/launcher/LazyBootstrap.exe", "main")
        self.write(self.repo / "build/launcher/MediaUpdater.exe", "updater")

    def test_compile_failure_or_cancel_never_uses_old_build(self):
        self.options.build_launcher = True
        self.seed_build()
        for failure in (ValueError("编译失败"), Cancelled()):
            with mock.patch.object(launcher_build, "compile_launcher", side_effect=failure):
                with self.assertRaises(type(failure)):
                    self.generate()
            self.assert_clean()

    def test_build_extra_files_and_changed_preview(self):
        self.options.build_launcher = True
        self.seed_build()
        self.write(self.repo / "build/说明.txt", "说明")
        with mock.patch.object(launcher_build, "compile_launcher") as compiler:
            plan = prepare(self.options)
            result = build(plan)
            compiler.assert_called_once()
        self.assertEqual((result / "source/说明.txt").read_text(encoding="utf-8"), "说明")
        self.assertIn({"type": "copy", "source": "source/说明.txt", "target": "说明.txt"}, plan.manifest["operations"])
        self.options.suffix = "changed"
        with mock.patch.object(launcher_build, "compile_launcher"):
            plan = prepare(self.options)
        self.write(self.repo / "build/Launcher.exe", "changed-entry")
        with self.assertRaisesRegex(ValueError, "发生变化"):
            build(plan)
        self.assertFalse(plan.destination.exists())

    def test_compile_rejects_inputs_and_output_in_build(self):
        self.seed_build()
        self.options.build_launcher = True
        with mock.patch.object(launcher_build, "compile_launcher") as compiler:
            self.options.output = str(self.repo / "build")
            with self.assertRaisesRegex(ValueError, "重叠"):
                prepare(self.options)
            self.options.output = str(self.output)
            self.options.rules = [Rule(enabled=True, source=str(self.repo / "build/Launcher.exe"), target="entry.exe")]
            with self.assertRaisesRegex(ValueError, "重叠"):
                prepare(self.options)
            compiler.assert_not_called()

    def test_missing_build_output_rejected(self):
        self.seed_build()
        (self.repo / "build/launcher/MediaUpdater.exe").unlink()
        self.options.build_launcher = True
        with mock.patch.object(launcher_build, "compile_launcher"):
            with self.assertRaisesRegex(ValueError, "MediaUpdater"):
                prepare(self.options)
        self.assert_clean()

    def test_path_and_reserved_target_validation(self):
        for target in ("", "../x", "C:/x", "/x", "contents/CON.txt", "contents/a.", "contents/a ",
                       "tmp/x", ".media-update/x", "update_log.txt", "contents/.media-update-a", "launcher/MediaUpdater.exe.pending"):
            with self.subTest(target=target), self.assertRaises(ValueError):
                rule_operation(Rule(kind="delete", target=target), 1)
        with self.assertRaises(ValueError):
            rule_operation(Rule(kind="delete", target="launcher/MediaUpdater.exe"), 1)
        nested = self.write(self.root / "extra/.media-update-private", "bad").parent
        self.options.rules = [Rule(enabled=True, source=str(nested), target="contents/extra")]
        with self.assertRaises(ValueError):
            prepare(self.options)

    def test_known_type_conflicts_and_explicit_delete(self):
        extra = self.write(self.root / "extra", "replacement")
        self.options.rules = [Rule(enabled=True, source=str(extra), target="contents/data")]
        with self.assertRaisesRegex(ValueError, "冲突"):
            prepare(self.options)
        self.options.rules.insert(0, Rule(kind="delete", enabled=True, target="contents/data"))
        prepare(self.options)

    def test_existing_output_and_overlap(self):
        existing = self.output / package_name(self.options)
        existing.mkdir()
        self.write(existing / "keep", "untouched")
        with self.assertRaises(ValueError):
            prepare(self.options)
        self.assertEqual((existing / "keep").read_text(), "untouched")
        self.options.output = str(self.original)
        with self.assertRaisesRegex(ValueError, "包含"):
            prepare(self.options)
        self.options.output = str(self.output)
        self.options.suffix = "new"
        self.options.content_root = str(self.output)
        with self.assertRaises(ValueError):
            prepare(self.options)

    def test_additional_update_and_ident_directory_rejected(self):
        extra = self.write(self.original / "data/update", "bad")
        with self.assertRaisesRegex(ValueError, "额外 update"):
            prepare(self.options)
        extra.unlink()
        ident = self.original / "prop/ea3-ident.xml"
        ident.unlink()
        ident.mkdir()
        self.options.edit_version = True
        with self.assertRaisesRegex(ValueError, "不能是目录"):
            prepare(self.options)

    def test_changed_after_preview_and_during_copy(self):
        plan = prepare(self.options)
        self.write(self.original / "data/中文.txt", "changed")
        with self.assertRaisesRegex(ValueError, "发生变化"):
            build(plan)
        self.assert_clean()
        plan = prepare(self.options)
        def change(message):
            if message.startswith("正在复制"):
                self.write(self.original / "data/中文.txt", "changed-again")
        with self.assertRaisesRegex(ValueError, "发生变化"):
            build(plan, change)
        self.assert_clean()

    def test_cancel_cleans_only_current_workspace(self):
        marker = self.write(self.output / "keep", "keep")
        plan = prepare(self.options)
        cancelled = False
        def report(message):
            nonlocal cancelled
            if message.startswith("正在复制"):
                cancelled = True
        def cancel():
            if cancelled:
                raise Cancelled()
        with self.assertRaises(Cancelled):
            build(plan, report, cancel)
        self.assertEqual(list(self.output.iterdir()), [marker])
        self.assertEqual((self.original / "data/中文.txt").read_text(encoding="utf-8"), "新内容")

    def test_download_failure_and_selected_overwrite(self):
        self.options.download_spice = True
        spice = self.write(self.original / "Spice64.EXE", "old")
        plan = prepare(self.options)
        with self.assertRaisesRegex(OSError, "offline"):
            build(plan, downloader=mock.Mock(side_effect=OSError("offline")))
        self.assert_clean()
        def download(destination, workspace, prerelease, progress, cancel):
            destination.write_bytes(b"new-spice")
            return "tag"
        result = build(plan, downloader=download)
        self.assertEqual((result / "source/contents/spice64.exe").read_bytes(), b"new-spice")
        self.assertEqual(spice.read_bytes(), b"old")
        document = json.loads((result / "checksums").read_bytes())
        entry = next(e for e in document["files"] if e["path"].lower().endswith("spice64.exe"))
        self.assertEqual(entry["sha256"], hashlib.sha256(b"new-spice").hexdigest())

    def test_cancel_during_checksum_and_preserve_previous_manifest(self):
        plan = prepare(self.options)
        cancelled = False
        def report(message):
            nonlocal cancelled
            if message.startswith("正在计算 SHA-256"):
                cancelled = True
        def cancel():
            if cancelled:
                raise Cancelled()
        with self.assertRaises(Cancelled):
            build(plan, report, cancel)
        self.assert_clean()
        result = build(plan)
        previous = (result / "checksums").read_bytes()
        cancelled = False
        with self.assertRaises(Cancelled):
            checksums.generate(str(result), progress=report, cancel=cancel)
        self.assertEqual((result / "checksums").read_bytes(), previous)
        self.assertFalse(list(result.glob(".checksums-*.tmp")))

    @unittest.skipUnless(os.name == "nt", "需要 Windows 目录联接")
    def test_junction_input_rejected(self):
        junction = self.root / "junction"
        command = "New-Item -ItemType Junction -Path '" + str(junction).replace("'", "''") + "' -Target '" + str(self.original).replace("'", "''") + "' | Out-Null"
        subprocess.run(["pwsh", "-NoProfile", "-Command", command], check=True, capture_output=True)
        self.addCleanup(lambda: junction.rmdir() if junction.exists() else None)
        self.options.original = str(junction)
        with self.assertRaises(ValueError):
            prepare(self.options)

    @unittest.skipUnless(os.environ.get("UPDATE_TEST_WORKER"), "设置 UPDATE_TEST_WORKER 启用 C# 互通")
    def test_csharp_verify_apply_and_tampering(self):
        worker = os.environ["UPDATE_TEST_WORKER"]
        self.options.edit_version = True
        self.options.asphyxia_enabled = True
        self.options.rules = [Rule(kind="delete", enabled=True, target="contents/obsolete"),
                              Rule(kind="editXml", enabled=True, target="contents/config.xml", xpath="/root/@value", value="new")]
        with mock.patch.object(asphyxia_download, "download", side_effect=self.fake_plugin):
            package = self.generate()
        game = self.root / "game"
        ident = self.write(game / "contents/prop/ea3-ident.xml", "<ea3_conf><soft><ext>2026071400</ext><dest>U</dest></soft></ea3_conf>")
        self.write(game / "contents/keep", "keep")
        self.write(game / "contents/obsolete", "old")
        config = self.write(game / "contents/config.xml", '<root value="old"/>')
        self.write(game / "asphyxia/savedata/player.json", "save")
        staging = game / ".media-update/tmp"
        shutil.copytree(package, staging)
        def run(*args):
            return subprocess.run([worker, *map(str, args)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                  timeout=60, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        verified = run("verify", game, staging)
        self.assertEqual(verified.returncode, 0, verified.stdout.decode("utf-8-sig", errors="replace"))
        applied = run("apply", game, staging)
        self.assertEqual(applied.returncode, 0, applied.stdout.decode("utf-8-sig", errors="replace"))
        self.assertIn("2026080500", ident.read_text(encoding="utf-8"))
        self.assertIn("<dest>U</dest>", ident.read_text(encoding="utf-8"))
        self.assertIn('value="new"', config.read_text(encoding="utf-8"))
        self.assertFalse((game / "contents/obsolete").exists())
        self.assertEqual((game / "contents/keep").read_text(), "keep")
        self.assertEqual((game / asphyxia_download.PLUGIN_TARGET / "index.ts").read_text(), "plugin")
        self.assertEqual((game / "asphyxia/savedata/player.json").read_text(), "save")
        self.assertEqual((game / "contents/data/中文.txt").read_text(encoding="utf-8"), "新内容")
        (staging / "source/contents/data/中文.txt").write_bytes(b"tampered")
        self.assertNotEqual(run("verify", game, staging).returncode, 0)


class XmlRuleTests(unittest.TestCase):
    def test_all_actions(self):
        for action in ("setValue", "addAttribute", "replaceElement", "appendChild", "insertBefore", "insertAfter", "remove"):
            rule = Rule(kind="editXml", target="contents/config.xml", action=action, xpath="/root", name="a", value="", xml="<child/>")
            operation = rule_operation(rule, 1)
            edit = operation["edits"][0]
            self.assertEqual(edit["action"], action)
            self.assertEqual(set(edit), {"action", "xpath"} | ({"value", "name"} if action == "addAttribute" else
                             {"value"} if action == "setValue" else set() if action == "remove" else {"xml"}))

    def test_xml_and_namespace_errors(self):
        base = Rule(kind="editXml", target="contents/c.xml", action="appendChild", xpath="/root")
        for fragment in ("<a/><b/>", "<!DOCTYPE a><a/>", "<?xml version='1.0'?><a/>", "<!--comment--><a/>", "<bad>"):
            base.xml = fragment
            with self.assertRaises(ValueError):
                rule_operation(base, 1)
        base.action = "addAttribute"
        base.name = "p:a"
        with self.assertRaises(ValueError):
            rule_operation(base, 1)
        base.namespaces = {"p": "urn:test"}
        self.assertEqual(rule_operation(base, 1)["namespaces"], {"p": "urn:test"})
        base.namespaces = {"xmlns": "urn:test"}
        with self.assertRaises(ValueError):
            rule_operation(base, 1)
        base.namespaces = {}
        base.name = "a"
        base.value = "\x01"
        with self.assertRaises(ValueError):
            rule_operation(base, 1)


@unittest.skipUnless(os.name == "nt" and shutil.which("pwsh"), "需要 Windows 和 pwsh")
class CompilerTests(unittest.TestCase):
    def test_script_failure_and_output(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "build.ps1").write_text("Write-Output 'compiler-test'; exit 17", encoding="utf-8")
            messages = []
            with mock.patch.object(launcher_build, "REPO_ROOT", root):
                with self.assertRaisesRegex(ValueError, "17"):
                    launcher_build.compile_launcher(messages.append)
            self.assertTrue(any("compiler-test" in message for message in messages))

    def test_cancel_running_compiler(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "build.ps1").write_text("Write-Output 'ready'; Start-Sleep -Seconds 60", encoding="utf-8")
            ready = False
            def progress(message):
                nonlocal ready
                if message == "编译输出：ready":
                    ready = True
            def cancel():
                if ready:
                    raise Cancelled()
            start = time.monotonic()
            with mock.patch.object(launcher_build, "REPO_ROOT", root):
                with self.assertRaises(Cancelled):
                    launcher_build.compile_launcher(progress, cancel)
            self.assertLess(time.monotonic() - start, 15)


class Response(io.BytesIO):
    def __init__(self, value, headers=None):
        super().__init__(value)
        self.headers = headers or {}


class DownloadTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.archive = self.make_zip(["spice2x/spice64.exe"])
        self.release = {"tag_name": "26-09-01", "published_at": "2026-09-01T00:00:00Z", "prerelease": False, "draft": False,
                        "assets": [{"name": "spice2x-26-09-01.zip", "size": len(self.archive),
                        "digest": "sha256:" + hashlib.sha256(self.archive).hexdigest(),
                        "browser_download_url": "https://github.com/spice2x/spice2x.github.io/releases/download/tag/a.zip"}]}

    def make_zip(self, names):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, "w") as archive:
            for name in names:
                archive.writestr(name, b"MZtest")
        return stream.getvalue()

    def opener(self, url):
        return Response(json.dumps(self.release).encode()) if url.endswith("/latest") else Response(self.archive)

    def test_stable_download_extracts_only_executable(self):
        report = []
        destination = self.root / "spice64.exe"
        version = spice_download.download(destination, self.root, progress=report.append, opener=self.opener)
        self.assertEqual(version, "26-09-01")
        self.assertEqual(destination.read_bytes(), b"MZtest")
        self.assertTrue(any(version in message for message in report))

    def test_prerelease_order_and_draft_filter(self):
        newest = dict(self.release, prerelease=True, published_at="2026-09-12T00:00:00Z", tag_name="26-09-12")
        newest["assets"] = [dict(self.release["assets"][0], name="spice2x-26-09-12.zip")]
        draft = dict(newest, draft=True, published_at="2026-09-20T00:00:00Z")
        opener = lambda url: Response(json.dumps([self.release, draft, newest]).encode())
        tag, _ = spice_download.select_release(True, lambda: None, opener)
        self.assertEqual(tag, "26-09-12")

    def test_bad_size_hash_and_duplicate_executable(self):
        for condition in ("size", "hash", "duplicate"):
            with self.subTest(condition=condition):
                asset = deepcopy(self.release["assets"][0])
                archive = self.archive
                if condition == "size":
                    archive = archive[:-1]
                elif condition == "hash":
                    asset["digest"] = "sha256:" + "0" * 64
                else:
                    archive = self.make_zip(["a/spice64.exe", "b/spice64.exe"])
                    asset.update(size=len(archive), digest="sha256:" + hashlib.sha256(archive).hexdigest())
                release = dict(self.release, assets=[asset])
                opener = lambda url: Response(json.dumps(release).encode()) if url.endswith("/latest") else Response(archive)
                folder = self.root / condition
                folder.mkdir()
                with self.assertRaises(ValueError):
                    spice_download.download(folder / "out.exe", folder, opener=opener)

    def test_cancel_before_network(self):
        opener = mock.Mock()
        def cancel():
            raise Cancelled()
        with self.assertRaises(Cancelled):
            spice_download.download(self.root / "out", self.root, cancel=cancel, opener=opener)
        opener.assert_not_called()


class PluginDownloadTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.destination = self.root / "sdvx@asphyxia"
        self.archive = self.make_zip(["wrapper/index.ts", "wrapper/data/歌曲.ts"])
        self.releases = [self.release("kfc-7.2.2.zip", "2026-09-20T00:00:00Z")]

    def make_zip(self, names):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, "w") as archive:
            for name in names:
                archive.writestr(name, b"plugin")
        return stream.getvalue()

    def release(self, name, date, **extra):
        return {"tag_name": name.removesuffix(".zip"), "published_at": date, "draft": False, **extra,
                "assets": [{"name": name, "updated_at": date, "size": len(self.archive),
                            "digest": "sha256:" + hashlib.sha256(self.archive).hexdigest(),
                            "browser_download_url": asphyxia_download.DOWNLOAD_PREFIX + "tag/" + name}]}

    def opener(self, url):
        if url.startswith(asphyxia_download.API):
            return Response(json.dumps(self.releases).encode())
        return Response(self.archive)

    def download(self):
        return asphyxia_download.download(self.destination, self.root, opener=self.opener)

    def test_selects_newest_kfc_file_not_latest_other_game_or_version_text(self):
        self.releases = [self.release("mdx-999.zip", "2026-09-22T00:00:00Z"),
                         self.release("kfc-9.zip", "2026-09-01T00:00:00Z"),
                         self.release("KFC-7.zip", "2026-09-20T00:00:00Z", prerelease=True),
                         self.release("kfc-draft.zip", "2026-09-23T00:00:00Z", draft=True),
                         self.release("kfc-7.zip.sha256", "2026-09-24T00:00:00Z")]
        _, asset = asphyxia_download.select_asset(opener=self.opener)
        self.assertEqual(asset["name"], "KFC-7.zip")

    def test_paginates_release_list(self):
        calls = []
        def opener(url):
            calls.append(url)
            if url.endswith("page=1"):
                return Response(json.dumps([self.release("mdx-new.zip", "2026-09-22T00:00:00Z")]).encode(),
                                {"Link": '<https://api.github.com/next>; rel="next"'})
            return Response(json.dumps(self.releases).encode())
        _, asset = asphyxia_download.select_asset(opener=opener)
        self.assertEqual(asset["name"], "kfc-7.2.2.zip")
        self.assertEqual(len(calls), 2)
        self.assertTrue(calls[1].endswith("page=2"))

    def test_removes_single_wrapper_and_preserves_contents(self):
        self.assertEqual(self.download(), "kfc-7.2.2.zip")
        self.assertEqual((self.destination / "index.ts").read_bytes(), b"plugin")
        self.assertTrue((self.destination / "data/歌曲.ts").is_file())
        self.assertFalse((self.destination / "wrapper").exists())

    def test_flat_archive_keeps_structure(self):
        self.archive = self.make_zip(["index.ts", "data/songs.ts", "handlers/common.ts"])
        self.releases = [self.release("kfc-flat.zip", "2026-09-20T00:00:00Z")]
        self.download()
        self.assertTrue((self.destination / "index.ts").is_file())
        self.assertTrue((self.destination / "data/songs.ts").is_file())

    def test_only_one_wrapper_layer_is_removed(self):
        self.archive = self.make_zip(["outer/inner/index.ts"])
        self.releases = [self.release("kfc-nested.zip", "2026-09-20T00:00:00Z")]
        self.download()
        self.assertTrue((self.destination / "inner/index.ts").is_file())
        self.assertFalse((self.destination / "index.ts").exists())

    def test_mixed_top_level_entries_are_preserved(self):
        self.archive = self.make_zip(["README.md", "plugin/index.ts"])
        self.releases = [self.release("kfc-mixed.zip", "2026-09-20T00:00:00Z")]
        self.download()
        self.assertTrue((self.destination / "README.md").is_file())
        self.assertTrue((self.destination / "plugin/index.ts").is_file())

    def test_rejects_unsafe_paths_and_collisions_before_extraction(self):
        cases = (["../escape"], ["/absolute"], ["C:/escape"], ["wrap/CON.txt"],
                 ["wrap/a", "wrap/a/b"], ["wrap/A/a", "wrap/a/b"], ["wrap/update"],
                 ["wrap/.media-update-test"], ["wrap/index.ts", "wrap/INDEX.ts"])
        for names in cases:
            with self.subTest(names=names), zipfile.ZipFile(io.BytesIO(self.make_zip(names))) as archive:
                with self.assertRaises(ValueError):
                    asphyxia_download.extraction_entries(archive, lambda: None)
        self.assertFalse(self.destination.exists())

    def test_rejects_symlinks_and_empty_archives(self):
        link = zipfile.ZipInfo("wrapper/index.ts")
        link.create_system = 3
        link.external_attr = 0o120777 << 16
        for names in ([], ["wrapper/"], [link]):
            with self.subTest(names=names), zipfile.ZipFile(io.BytesIO(self.make_zip(names))) as archive:
                with self.assertRaises(ValueError):
                    asphyxia_download.extraction_entries(archive, lambda: None)

    def test_missing_asset_and_corrupt_download(self):
        self.releases = [self.release("mdx.zip", "2026-09-20T00:00:00Z")]
        with self.assertRaisesRegex(ValueError, "没有 kfc"):
            self.download()
        self.releases = [self.release("kfc.zip", "2026-09-20T00:00:00Z")]
        self.archive = self.archive[:-1]
        with self.assertRaisesRegex(ValueError, "校验失败"):
            self.download()
        self.assertFalse(self.destination.exists())

    def test_cancel_before_network_and_during_extraction(self):
        def cancel():
            raise Cancelled()
        opener = mock.Mock()
        with self.assertRaises(Cancelled):
            asphyxia_download.download(self.destination, self.root, cancel=cancel, opener=opener)
        opener.assert_not_called()
        stop = False
        def progress(message):
            nonlocal stop
            stop = stop or message.startswith("正在解压插件")
        def checkpoint():
            if stop:
                raise Cancelled()
        with self.assertRaises(Cancelled):
            asphyxia_download.download(self.destination, self.root, progress, checkpoint, opener=self.opener)


@unittest.skipUnless(os.environ.get("UPDATE_TOOL_UI_TEST"), "设置 UPDATE_TOOL_UI_TEST 启用桌面测试")
class GuiSmokeTests(unittest.TestCase):
    def setUp(self):
        import tkinter as tk
        from gui import App
        self.root = tk.Tk()
        self.app = App(self.root)
        self.root.update()
        self.addCleanup(self.cleanup)

    def cleanup(self):
        if self.root.winfo_exists():
            self.app.close()

    def pump(self, timeout=5):
        deadline = time.monotonic() + timeout
        while self.app.busy and time.monotonic() < deadline:
            self.root.update()
            time.sleep(.01)
        self.assertFalse(self.app.busy, "后台任务未按时结束")

    def test_rule_dialog_and_list(self):
        from gui import RuleDialog
        self.assertTrue(all(not flag.get() for flag in self.app.flags.values()))
        self.assertIn("build_launcher", self.app.flags)
        self.assertNotIn("entry", self.app.variables)
        self.assertNotIn("launcher", self.app.variables)
        self.assertNotIn("asphyxia", self.app.variables)
        # 旧版单动作 XML 仍可编辑；新建 XML 使用独立的可视化规则组。
        dialog = RuleDialog(self.root, Rule(kind="editXml", xpath="/root/@a"))
        self.root.update()
        dialog.kind.set("修改 XML")
        dialog.refresh()
        dialog.target.set("contents/config.xml")
        dialog.xpath.set("/root/@a")
        dialog.enabled.set(True)
        dialog.save()
        self.assertTrue(dialog.result.enabled)
        self.app.rules = [dialog.result, Rule(kind="delete", target="contents/old")]
        self.app.refresh_rules(0)
        self.app.move_rule(1)
        self.assertEqual(self.app.rules[1].kind, "editXml")
        self.app.toggle_rule()
        self.assertFalse(self.app.rules[1].enabled)

    def test_preview_generate_and_responsive_cancel(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            (folder / "input/data").mkdir(parents=True)
            (folder / "input/data/a").write_text("payload")
            (folder / "out").mkdir()
            for key, value in {"original": str(folder / "input"), "output": str(folder / "out"),
                               "old_version": "2026071400", "new_version": "2026080500"}.items():
                self.app.variables[key].set(value)
            self.app.preview()
            self.pump()
            self.assertIsNotNone(self.app.plan)
            self.assertIn('"operations"', self.app.preview_text.get("1.0", "end"))
            with mock.patch("gui.messagebox.showinfo"):
                self.app.generate()
                self.pump()
            self.assertTrue((self.app.last_output / "checksums").is_file())
            def slow(report, cancel):
                while True:
                    cancel()
                    time.sleep(.01)
            self.app.start_worker(slow, "complete")
            self.root.update()
            self.assertTrue(self.app.busy)
            self.app.cancel()
            self.pump()
            self.assertEqual(self.app.status.get(), "已取消")


if __name__ == "__main__":
    unittest.main()
