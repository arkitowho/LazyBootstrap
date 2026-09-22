"""现有更新包导入、编辑、发布和 XPath 回放的回归测试。"""

from copy import deepcopy
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

TOOL = Path(__file__).resolve().parents[1] / "Tools/Update-Package-Tool"
sys.path.insert(0, str(TOOL))
from manifest import Options, Rule, checksums
from package_builder import Cancelled
from package_import import import_package, operation_for, parse_operation, prepare_import, save_import
from xml_replay import ReplayEditor, infer_reference, locate
from xml_editor import XmlEditor, elements, equivalent


COPY = {"type": "copy", "source": "source/content", "target": "contents"}


class PackageImportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.package = self.root / "UPDATE_LAZY_KFC_2026071400 to 2026080500"
        (self.package / "source/content/空目录").mkdir(parents=True)
        (self.package / "source/content/配置.xml").write_bytes(b'<r><item id="x" v="old"/></r>')
        (self.package / "说明.txt").write_bytes("额外说明\r\n".encode("gbk"))
        self.operations = [deepcopy(COPY)]
        self.write_manifest()

    def write_manifest(self, checks=True):
        (self.package / "update").write_text(json.dumps({"operations": self.operations}, ensure_ascii=False), encoding="utf-8")
        if checks:
            checksums.generate(str(self.package), progress=lambda _: None)

    def session(self):
        return import_package(self.package)

    def plan(self, session=None, **kwargs):
        return prepare_import(session or self.session(), Options(output=str(self.root), suffix="另存副本", **kwargs))

    def test_round_trip_all_operations_unused_empty_and_original_fields(self):
        self.operations += [
            {"type": "mirror", "source": "source\\content", "target": "extra"},
            {"type": "delete", "target": "contents/old"},
            {"type": "editXml", "target": "contents/配置.xml", "namespaces": {}, "edits": [
                {"action": "setValue", "xpath": "r/item/@v", "value": "new"}]},
        ]
        self.write_manifest()
        session = import_package(self.root)
        self.assertEqual(session.naming(), ("2026071400", "2026080500", ""))
        self.assertTrue(all(rule.enabled for rule in session.rules))
        self.assertEqual(session.integrity_issues, [])
        self.assertEqual(session.manifest()["operations"], self.operations)
        saved = save_import(self.plan(session))
        self.assertEqual(json.loads((saved / "update").read_text("utf-8"))["operations"], self.operations)
        self.assertTrue((saved / "source/content/空目录").is_dir())
        for path in self.package.rglob("*"):
            if path.is_file() and path.name not in {"update", "checksums"}:
                self.assertEqual(path.read_bytes(), (saved / path.relative_to(self.package)).read_bytes())
        self.assertEqual(import_package(saved).integrity_issues, [])

    def test_checksum_confirmation_missing_malformed_mismatch(self):
        for raw in (None, b'{"bad":true}', b'{"algorithm":"SHA256","files":[]}'):
            with self.subTest(raw=raw):
                (self.package / "checksums").unlink(missing_ok=True)
                if raw:
                    (self.package / "checksums").write_bytes(raw)
                session = self.session()
                self.assertTrue(session.integrity_issues)
                with self.assertRaisesRegex(ValueError, "继续编辑"):
                    self.plan(session)
                session.integrity_accepted = True
                self.plan(session)
        self.write_manifest()
        (self.package / "说明.txt").write_bytes(b'tampered')
        self.assertTrue(any("SHA-256" in error for error in self.session().integrity_issues))

    def test_missing_payload_repair_shared_references_toggle_and_order(self):
        self.operations += [deepcopy(COPY)]
        self.operations[1]["target"] = "second"
        self.write_manifest()
        session = self.session()
        session.remove("source/content")
        self.assertEqual(session.references("source/content"), [0, 1])
        self.assertEqual(len(session.issues()), 2)
        with self.assertRaisesRegex(ValueError, "载荷缺失"):
            self.plan(session)
        replacement = self.root / "替换"
        replacement.mkdir()
        (replacement / "new").write_bytes(b'new')
        session.put(replacement, "source/content")
        self.assertEqual(session.issues(), [])
        session.rules.reverse()
        session.rules[1].enabled = False
        saved = save_import(self.plan(session))
        self.assertEqual(import_package(saved).rules[0].target, "second")
        self.assertEqual((saved / "source/content/new").read_bytes(), b'new')
        self.assertTrue((self.package / "source/content/配置.xml").exists())

    def test_protected_paths_conflicts_and_multiple_manifests(self):
        session = self.session()
        source = self.root / "payload"
        source.write_bytes(b'x')
        for destination in ("update", "checksums", "../x", "source/CON", "source/nested/update", "SOURCE/new"):
            with self.subTest(destination=destination), self.assertRaises(ValueError):
                session.put(source, destination)
        nested = self.package / "second"
        nested.mkdir()
        (nested / "update").write_text('{}')
        with self.assertRaisesRegex(ValueError, "2 份"):
            self.session()

    def test_invalid_manifests(self):
        invalid = [
            {"type": "copy", "source": "../outside", "target": "contents"},
            {"type": "copy", "source": "source/a", "target": "../outside"},
            {"type": "delete", "target": ".media-update/x"},
            {"type": "copy", "source": "source/a", "target": "contents", "edits": []},
            {"type": "delete", "target": "contents", "source": "source"},
            {"type": "editXml", "target": "contents/x", "edits": [{"action": "setValue", "xpath": "[", "value": "x"}]},
            {"type": "editXml", "target": "contents/x", "edits": [{"action": "setValue", "xpath": "/missing:r", "value": "x"}]},
            {"type": "editXml", "target": "contents/x", "edits": [{"action": "setValue", "xpath": "document('file:///x')", "value": "x"}]},
            {"type": "editXml", "target": "contents/x", "edits": [{"action": "remove", "xpath": "/r", "value": "x"}]},
            {"type": "editXml", "target": "contents/x", "edits": None},
        ]
        for operation in invalid:
            with self.subTest(operation=operation), self.assertRaises((ValueError, TypeError)):
                parse_operation(operation)
        (self.package / "update").write_text('{"operations":[],"operations":[]}')
        with self.assertRaises(ValueError):
            self.session()

    def test_cancel_external_changes_and_output_protection(self):
        session = self.session()
        plan = self.plan(session)
        def cancel():
            raise Cancelled()
        with self.assertRaises(Cancelled):
            save_import(plan, cancel=cancel)
        self.assertFalse(plan.destination.exists())
        changed = self.package / "说明.txt"
        changed.write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, "外部修改"):
            save_import(plan)
        self.assertFalse(plan.destination.exists())
        self.write_manifest()
        plan = self.plan()
        plan.destination.mkdir()
        marker = plan.destination / "keep"
        marker.write_bytes(b'keep')
        with self.assertRaisesRegex(ValueError, "已经存在"):
            save_import(plan)
        self.assertEqual(marker.read_bytes(), b'keep')

    def test_overwrite_keeps_backup_and_rename_failure_restores(self):
        session = self.session()
        replacement = self.root / "new"
        replacement.write_bytes(b'new')
        session.put(replacement, "说明.txt", replace=True)
        plan = prepare_import(session, Options(), overwrite=True)
        before = (self.package / "说明.txt").read_bytes()
        rename = Path.rename
        def fail_publish(path, destination):
            if path.name == "package":
                raise OSError("模拟发布重命名失败")
            return rename(path, destination)
        with mock.patch.object(Path, "rename", fail_publish), self.assertRaisesRegex(OSError, "模拟"):
            save_import(plan)
        self.assertEqual((self.package / "说明.txt").read_bytes(), before)
        self.assertFalse(plan.backup.exists())
        # 恢复目录重命名会改变目录戳，需要重新导入。
        session = self.session()
        session.put(replacement, "说明.txt", replace=True)
        plan = prepare_import(session, Options(), overwrite=True)
        logs = []
        save_import(plan, progress=logs.append)
        self.assertEqual((plan.backup / "说明.txt").read_bytes(), before)
        self.assertEqual((self.package / "说明.txt").read_bytes(), b'new')
        self.assertTrue(any(str(plan.backup) in line for line in logs))

    def test_cancel_during_switch_finishes_publication(self):
        plan = prepare_import(self.session(), Options(), overwrite=True)
        switched = False
        rename = Path.rename
        def switch(path, destination):
            nonlocal switched
            result = rename(path, destination)
            if path == self.package:
                switched = True
            return result
        def cancel():
            if switched:
                raise Cancelled()
        with mock.patch.object(Path, "rename", switch):
            save_import(plan, cancel=cancel)
        self.assertTrue(plan.backup.is_dir())
        self.assertTrue(self.package.is_dir())

    def test_cancel_copy_cleans_only_this_staging_and_first_rename_failure(self):
        plan = self.plan()
        copying = False
        def progress(message):
            nonlocal copying
            if message.startswith("正在复制载荷"):
                copying = True
        def cancel():
            if copying:
                raise Cancelled()
        with self.assertRaises(Cancelled):
            save_import(plan, progress, cancel)
        self.assertFalse(list(self.root.glob(".update-package-*")))
        self.assertTrue((self.package / "update").is_file())
        plan = prepare_import(self.session(), Options(), overwrite=True)
        with mock.patch.object(Path, "rename", side_effect=PermissionError("模拟目录被占用")), self.assertRaises(PermissionError):
            save_import(plan)
        self.assertTrue(self.package.is_dir())
        self.assertFalse(plan.backup.exists())

    def test_external_payload_changes_overlap_and_link_rejected(self):
        session = self.session()
        local = self.root / "new.bin"
        local.write_bytes(b'first')
        session.put(local, "source/new.bin")
        plan = self.plan(session)
        local.write_bytes(b'next')
        with self.assertRaisesRegex(ValueError, "载荷发生变化"):
            save_import(plan)
        with self.assertRaisesRegex(ValueError, "互相包含"):
            prepare_import(self.session(), Options(output=str(self.package), suffix="nested"))
        original = checksums.reject_link
        def linked(path):
            if path.name == "说明.txt":
                raise ValueError("模拟链接：" + str(path))
            return original(path)
        with mock.patch.object(checksums, "reject_link", linked), self.assertRaisesRegex(ValueError, "链接"):
            self.session()

    def test_no_import_dependency_silent_install_and_wrong_json_encoding(self):
        import builtins
        original = builtins.__import__
        def missing(name, *args, **kwargs):
            if name == "lxml":
                raise ImportError("模拟缺少依赖")
            return original(name, *args, **kwargs)
        with mock.patch("builtins.__import__", side_effect=missing), self.assertRaisesRegex(ValueError, "pip install"):
            parse_operation({"type": "editXml", "target": "contents/x", "edits": [{"action": "remove", "xpath": "/r/a"}]})
        (self.package / "update").write_text(json.dumps({"operations": [COPY]}), encoding="utf-16")
        with self.assertRaises(ValueError):
            self.session()

    def test_rules_only_no_source_and_disabled_new_rule(self):
        shutil.rmtree(self.package / "source")
        self.operations = [{"type": "delete", "target": "contents/old"}]
        self.write_manifest()
        session = self.session()
        session.rules.append(Rule(kind="delete", target="contents/untouched"))
        saved = save_import(self.plan(session))
        self.assertFalse((saved / "source").exists())
        self.assertEqual(len(import_package(saved).rules), 1)

    def test_components_append_isolated_mapping_and_failure(self):
        session = self.session()
        plan = self.plan(session, download_spice=True, asphyxia_enabled=True)
        self.assertEqual(plan.manifest["operations"][0], COPY)
        self.assertEqual(len(plan.manifest["operations"]), 3)
        self.assertTrue(all(op["source"].startswith(plan.component_prefix + "/") for op in plan.manifest["operations"][1:]))
        def fail(*args):
            raise ValueError("模拟下载失败")
        with self.assertRaisesRegex(ValueError, "下载失败"):
            save_import(plan, downloader=fail)
        self.assertFalse(plan.destination.exists())
        def spice(destination, workspace, prerelease, progress, cancel):
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(b'spice')
        def plugin(destination, workspace, progress, cancel):
            destination.mkdir(parents=True)
            (destination / "index.js").write_bytes(b'plugin')
        saved = save_import(plan, downloader=spice, plugin_downloader=plugin)
        self.assertEqual((saved / plan.component_prefix / "spice/spice64.exe").read_bytes(), b'spice')
        self.assertEqual(import_package(saved).integrity_issues, [])

    def test_launcher_build_is_opt_in_and_mapping(self):
        import launcher_build
        repo = self.root / "repo"
        (repo / "build/launcher").mkdir(parents=True)
        for name in ("Launcher.exe", "launcher/LazyBootstrap.exe", "launcher/MediaUpdater.exe", "说明.txt"):
            (repo / "build" / name).write_bytes(name.encode())
        with mock.patch.object(launcher_build, "REPO_ROOT", repo), mock.patch.object(launcher_build, "compile_launcher") as compile_:
            self.plan()
            compile_.assert_not_called()
            plan = self.plan(build_launcher=True)
            compile_.assert_called_once()
            operations = plan.manifest["operations"]
            self.assertTrue(any(op["target"] == "launcher" and op["type"] == "mirror" for op in operations))
            self.assertTrue(any(op["target"] == "启动.exe" for op in operations))
            saved = save_import(plan)
            self.assertEqual((saved / plan.component_prefix / "launcher-build/启动.exe").read_bytes(), b'Launcher.exe')

    def test_infer_reference_applies_prior_edits_and_copy_behavior(self):
        self.operations += [
            {"type": "editXml", "target": "contents/配置.xml", "edits": [{"action": "setValue", "xpath": "r/item/@v", "value": "changed"}]},
            {"type": "editXml", "target": "contents/配置.xml", "edits": [{"action": "appendChild", "xpath": "/r", "xml": "<new/>"}]},
        ]
        self.write_manifest()
        session = self.session()
        raw, origin = infer_reference(session, 2)
        self.assertIn(b'changed', raw)
        group = ReplayEditor(session.rules[2], raw, origin)
        self.assertIsNone(group.failure)
        saved = save_import(self.plan(session))
        self.assertIn(b'old', (saved / "source/content/配置.xml").read_bytes())


class XPathReplayTests(unittest.TestCase):
    def rule(self, edits, **kwargs):
        return Rule(kind="editXml", target="contents/config.xml", enabled=True, edits=edits, **kwargs)

    def test_complex_relative_absolute_attribute_and_quote_xpath(self):
        data = b'<r><item id="a&quot;b\'c" v="old"/><item id="other"/></r>'
        edits = [{"action": "setValue", "xpath": '''r/item[@id=concat('a"b',"'",'c')]/@v''', "value": "new"},
                 {"action": "addAttribute", "xpath": "/r/item[position()=2 and not(@v)]", "name": "v", "value": "ok"}]
        editor = ReplayEditor(self.rule(edits), data)
        self.assertIsNone(editor.failure)
        self.assertEqual(editor.edits, edits)
        self.assertIn('v="ok"', editor.final.preview())
        self.assertEqual(editor.to_rule("contents/config.xml", True).edits, edits)

    def test_namespace_attribute_prefix_and_default_namespace(self):
        edits = [{"action": "addAttribute", "xpath": "/n:r/n:item", "name": "p:flag", "value": "x"},
                 {"action": "setValue", "xpath": "/n:r/n:item/@p:flag", "value": "y"},
                 {"action": "appendChild", "xpath": "/n:r", "xml": '<child xmlns="urn:x"/>'}]
        group = ReplayEditor(self.rule(edits, namespaces={"n": "urn:r", "p": "urn:p"}), b'<r xmlns="urn:r"><item/></r>')
        self.assertIsNone(group.failure)
        self.assertIn('p:flag="y"', group.final.preview())
        self.assertEqual(group.namespaces, {"n": "urn:r", "p": "urn:p"})

    def test_axes_xml_prefix_and_function_names_inside_literals(self):
        edits = [{"action": "setValue", "xpath": "/r/child::a[@v='document(x)']/ancestor::r/a/@xml:lang", "value": "zh"}]
        rule = parse_operation({"type": "editXml", "target": "contents/config.xml", "edits": edits})
        group = ReplayEditor(rule, b'<r><a v="document(x)" xml:lang="en"/></r>')
        self.assertIsNone(group.failure)
        self.assertIn('xml:lang="zh"', group.final.preview())

    def test_failed_step_rebind_reorder_undo_and_reopen(self):
        edits = [{"action": "setValue", "xpath": "/r/missing", "value": "first"},
                 {"action": "appendChild", "xpath": "/r", "xml": "<b/>"}]
        group = ReplayEditor(self.rule(edits), b'<r><a/></r>')
        self.assertEqual(group.failure[0], 0)
        with self.assertRaises(ValueError):
            group.state_before(1)
        with self.assertRaises(ValueError):
            group.to_rule("contents/config.xml", True)
        group.change(0, node_path=(0,))
        self.assertIsNone(group.failure)
        self.assertEqual(group.edits[0]["xpath"], "/r/a")
        group.commit(list(reversed(group.edits)))
        self.assertIsNone(group.failure)
        group.undo()
        self.assertEqual(group.edits[0]["action"], "setValue")
        group.redo()
        self.assertEqual(group.edits[0]["action"], "appendChild")
        rule = group.to_rule("contents/config.xml", True)
        reopened = ReplayEditor(rule, rule.reference_bytes)
        self.assertEqual(reopened.final.preview(), group.final.preview())
        self.assertEqual(rule.reference_bytes, b'<r><a/></r>')

    def test_multiple_unsupported_and_dtd(self):
        for xpath in ("/r/a", "/r/a/text()", "/r/comment()", "/", "/r/namespace::*"):
            with self.subTest(xpath=xpath):
                data = b'<r><a>v</a><a>v</a><!--c--></r>'
                group = ReplayEditor(self.rule([{"action": "setValue", "xpath": xpath, "value": "x"}]), data)
                self.assertIsNotNone(group.failure)
        with self.assertRaises(ValueError):
            ReplayEditor(self.rule([]), b'<!DOCTYPE r [<!ENTITY e SYSTEM "file:///missing">]><r>&e;</r>')

    def test_encodings_continuous_new_nodes_identity_changes_and_skips(self):
        for codec, encoding in (("utf-8", "auto"), ("utf-16le", "utf-16le"), ("utf-16be", "utf-16be"), ("gbk", "gbk"), ("cp932", "shift-jis")):
            with self.subTest(codec=codec):
                declaration = "shift_jis" if codec == "cp932" else codec
                data = (f'<?xml version="1.0" encoding="{declaration}"?><r><a id="old">音</a></r>').encode(codec)
                edits = [{"action": "setValue", "xpath": '/r/a/@id', "value": "new"},
                         {"action": "appendChild", "xpath": "/r", "xml": '<b id="x"/>'},
                         {"action": "appendChild", "xpath": "/r", "xml": '<b id="x"/>'},
                         {"action": "setValue", "xpath": '/r/a[@id="new"]', "value": "日"},
                         {"action": "setValue", "xpath": '/r/b[@id="x"]', "value": "音"}]
                group = ReplayEditor(self.rule(edits, encoding=encoding), data)
                self.assertIsNone(group.failure)
                self.assertEqual(len(elements(group.final.document.documentElement)), 2)
                self.assertIn("日", group.final.preview())
                self.assertIn("音", group.final.preview())


@unittest.skipUnless(os.environ.get("UPDATE_TEST_WORKER"), "设置 UPDATE_TEST_WORKER 启用 C# 工作进程测试")
class ImportWorkerTests(unittest.TestCase):
    setUp = PackageImportTests.setUp
    write_manifest = PackageImportTests.write_manifest
    session = PackageImportTests.session
    plan = PackageImportTests.plan
    def run_worker(self, command, game, package):
        return subprocess.run([os.environ["UPDATE_TEST_WORKER"], command, str(game), str(package)], capture_output=True, text=True, encoding="utf-8", errors="replace")

    def test_verify_apply_matches_preview_and_second_apply_skip_log(self):
        edits = [{"action": "setValue", "xpath": '/r/item/@v', "value": "old"},
                 {"action": "appendChild", "xpath": '/r', "xml": '<added id="a"/>'},
                 {"action": "setValue", "xpath": '/r/added/@id', "value": "b"}]
        self.operations += [{"type": "editXml", "target": "contents/配置.xml", "edits": edits}]
        self.write_manifest()
        session = self.session()
        raw, origin = infer_reference(session, 1)
        preview = ReplayEditor(session.rules[1], raw, origin)
        self.assertIsNone(preview.failure)
        session.rules[1] = preview.to_rule("contents/配置.xml", True)
        saved = save_import(self.plan(session))
        game = self.root / "game"
        (game / "contents").mkdir(parents=True)
        (game / "asphyxia").mkdir()
        staging = game / ".media-update/tmp"
        shutil.copytree(saved, staging)
        verified = self.run_worker("verify", game, staging)
        self.assertEqual(verified.returncode, 0, verified.stdout + verified.stderr)
        applied = self.run_worker("apply", game, staging)
        self.assertEqual(applied.returncode, 0, applied.stdout + applied.stderr)
        actual = XmlEditor((game / "contents/配置.xml").read_bytes())
        self.assertTrue(equivalent(actual.document.documentElement, preview.final.document.documentElement))
        self.assertIn('id="b"', actual.preview())  # 第一条跳过不能阻止后续修改。
        self.assertIn("skip", (game / ".media-update/update_log.txt").read_text("utf-8").lower())

    def test_namespaces_encodings_all_operations_and_apply_failure(self):
        for number, (codec, encoding) in enumerate((("utf-8", "auto"), ("utf-16-le", "utf-16le"), ("utf-16-be", "utf-16be"), ("gbk", "gbk"), ("cp932", "shift-jis"))):
            with self.subTest(encoding=encoding):
                declaration = {"utf-16-le": "utf-16le", "utf-16-be": "utf-16be", "cp932": "shift_jis"}.get(codec, codec)
                data = (f'<?xml version="1.0" encoding="{declaration}"?><r xmlns="urn:r"><item id="a" value="音"/><item id="b"/><!--保留--></r>').encode(codec)
                (self.package / "source/content/配置.xml").write_bytes(data)
                edits = [{"action": "setValue", "xpath": 'n:r/n:item[@id="a"]/@value', "value": "日"},
                         {"action": "addAttribute", "xpath": '/n:r/n:item[2]', "name": "p:new", "value": "音"},
                         {"action": "insertAfter", "xpath": '/n:r/n:item[@id="a"]', "xml": '<item xmlns="urn:r" id="inserted"/>'},
                         {"action": "setValue", "xpath": '/n:r/n:item[@id="inserted"]', "value": "日"},
                         {"action": "remove", "xpath": '/n:r/n:item[@id="b"]/@p:new'}]
                self.operations = [deepcopy(COPY), {"type": "mirror", "source": "source/content", "target": "mirrored"},
                                   {"type": "delete", "target": "contents/old"},
                                   {"type": "editXml", "target": "contents/配置.xml", "encoding": encoding,
                                    "namespaces": {"n": "urn:r", "p": "urn:p"}, "edits": edits}]
                self.write_manifest()
                session = self.session()
                preview = ReplayEditor(session.rules[-1], data)
                self.assertIsNone(preview.failure)
                game = self.root / f"game-{number}"
                (game / "contents").mkdir(parents=True)
                (game / "asphyxia").mkdir()
                (game / "mirrored").mkdir()
                (game / "mirrored/extra").write_bytes(b'extra')
                (game / "contents/old").write_bytes(b'old')
                (game / "contents/keep").write_bytes(b'keep')
                plan = prepare_import(session, Options(output=str(self.root), suffix=f"encoded-{number}"))
                saved = save_import(plan)
                staging = game / ".media-update/tmp"
                shutil.copytree(saved, staging)
                for command in ("verify", "apply"):
                    result = self.run_worker(command, game, staging)
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                actual = XmlEditor((game / "contents/配置.xml").read_bytes(), encoding)
                self.assertTrue(equivalent(actual.document.documentElement, preview.final.document.documentElement))
                self.assertTrue((game / "contents/keep").exists())
                self.assertFalse((game / "contents/old").exists())
                self.assertFalse((game / "mirrored/extra").exists())
        # 没有参考验证的原规则允许导出；安装时缺失匹配仍失败，已安装配置保持不变。
        self.operations = [{"type": "editXml", "target": "contents/config.xml", "edits": [{"action": "setValue", "xpath": "/r/absent", "value": "x"}]}]
        self.write_manifest()
        plan = prepare_import(self.session(), Options(output=str(self.root), suffix="missing-node"))
        saved = save_import(plan)
        game = self.root / "game-failure"
        (game / "contents").mkdir(parents=True)
        (game / "asphyxia").mkdir()
        config = game / "contents/config.xml"
        config.write_bytes(b'<r><keep/></r>')
        staging = game / ".media-update/tmp"
        shutil.copytree(saved, staging)
        result = self.run_worker("apply", game, staging)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(config.read_bytes(), b'<r><keep/></r>')


@unittest.skipUnless(os.environ.get("UPDATE_TOOL_UI_TEST"), "设置 UPDATE_TOOL_UI_TEST 启用桌面测试")
class ImportGuiTests(unittest.TestCase):
    def setUp(self):
        import tkinter as tk
        from package_import_gui import PackageEditor
        self.root = tk.Tk()
        self.root.withdraw()
        self.window = PackageEditor(self.root)
        self.addCleanup(self.root.destroy)
        self.patch = mock.patch("package_import_gui.messagebox.showerror")
        self.error = self.patch.start()
        self.addCleanup(self.patch.stop)

    def pump(self, window=None):
        window = window or self.window
        deadline = time.monotonic() + 15
        while window.winfo_exists() and window.busy and time.monotonic() < deadline:
            self.root.update()
            time.sleep(.005)
        self.root.update()
        self.assertFalse(window.winfo_exists() and window.busy)

    def test_import_file_tree_rule_toggle_preview_failed_import_keeps_session(self):
        with tempfile.TemporaryDirectory() as temporary:
            package = Path(temporary) / "UPDATE_LAZY_KFC_test"
            (package / "source/empty").mkdir(parents=True)
            (package / "update").write_text(json.dumps({"operations": [COPY]}))
            checksums.generate(str(package), progress=lambda _: None)
            self.window.load_package(package)
            self.pump()
            self.assertEqual(self.window.session.root, package)
            self.assertFalse(self.window.overwrite.get())
            self.assertTrue(all(not v.get() for v in self.window.flags.values()))
            self.assertIn("f:source", self.window.file_tree.get_children())
            self.window.rule_tree.selection_set("0")
            self.window.toggle_rule()
            self.assertFalse(self.window.session.rules[0].enabled)
            self.window.toggle_rule()
            self.window.session.rules[0].package_source = "source"
            self.window.variables["suffix"].set("copy")
            self.window.prepare()
            self.pump()
            self.assertIsNotNone(self.window.plan)
            session = self.window.session
            self.window.load_package(temporary + "/missing")
            self.pump()
            self.assertIs(self.window.session, session)
            self.error.assert_called_once()

    def test_step_gui_rebind_insert_history_and_save(self):
        from import_xml_gui import ImportedXmlDialog
        rule = Rule(kind="editXml", enabled=True, target="contents/config.xml", edits=[{"action": "setValue", "xpath": "/r/missing", "value": "long" * 100}])
        dialog = ImportedXmlDialog(self.window, rule, b'<r><a/></r>')
        self.pump(dialog)
        self.assertEqual(dialog.group.failure[0], 0)
        dialog.reveal((0,))
        dialog.rebind()
        self.pump(dialog)
        self.assertIsNone(dialog.group.failure)
        dialog.current = len(dialog.group.edits)
        dialog.show_state()
        self.pump(dialog)
        dialog.reveal(())
        dialog.change("appendChild", xml='<b/>')
        self.pump(dialog)
        self.assertEqual(len(dialog.group.edits), 2)
        dialog.history(False)
        self.pump(dialog)
        self.assertEqual(len(dialog.group.edits), 1)
        dialog.history(True)
        self.pump(dialog)
        dialog.save()
        deadline = time.monotonic() + 10
        while dialog.winfo_exists() and time.monotonic() < deadline:
            self.root.update()
            time.sleep(.005)
        self.assertEqual(len(dialog.result.edits), 2)
        self.assertTrue(dialog.result.visual_verified)
        self.assertEqual(rule.edits[0]["xpath"], "/r/missing")

    def test_large_file_tree_and_long_operation_list(self):
        from package_import import Entry, PackageSession
        root = Path(tempfile.gettempdir())
        entries = {"source": Entry(True, root, ())}
        entries.update({f"source/文件{index:05}.txt": Entry(False, root / str(index), ()) for index in range(10000)})
        rules = [Rule(kind="delete", target=f"contents/old-{index}", enabled=True) for index in range(2000)]
        self.window.session = PackageSession(root, {}, entries, rules)
        self.window.changed()
        self.root.update()
        self.window.file_tree.focus("f:source")
        self.window.expand_files()
        self.assertLessEqual(len(self.window.file_tree.get_children("f:source")), 201)
        self.assertEqual(len(self.window.rule_tree.get_children()), 2000)
        self.window.rule_tree.selection_set("1999")
        self.window.move_rule(-1)
        self.assertEqual(self.window.session.rules[1998].target, "contents/old-1999")
        self.error.assert_not_called()

    def test_checksum_cancel_continue_and_worker_cancel_preserve_session(self):
        from package_import import PackageSession
        with tempfile.TemporaryDirectory() as temporary:
            package = Path(temporary) / "UPDATE_LAZY_KFC_warning"
            package.mkdir()
            (package / "update").write_text(json.dumps({"operations": [{"type": "delete", "target": "contents/old"}]}))
            prior = PackageSession(Path(temporary), {}, {}, [])
            self.window.session = prior
            dialog = mock.Mock(result=False)
            with mock.patch("package_import_gui.IntegrityDialog", return_value=dialog), mock.patch.object(self.window, "wait_window"):
                self.window.load_package(package)
                self.pump()
            self.assertIs(self.window.session, prior)
            dialog.result = True
            with mock.patch("package_import_gui.IntegrityDialog", return_value=dialog), mock.patch.object(self.window, "wait_window"):
                self.window.load_package(package)
                self.pump()
            self.assertTrue(self.window.session.integrity_accepted)
            current = self.window.session
            def cancellable(progress, cancel):
                for _ in range(100):
                    time.sleep(.005)
                    cancel()
                return None
            self.window.work(cancellable, lambda result: self.fail("取消后不应应用结果"))
            self.window.stop.set()
            self.pump()
            self.assertIs(self.window.session, current)
            self.error.assert_not_called()


if __name__ == "__main__":
    unittest.main()
