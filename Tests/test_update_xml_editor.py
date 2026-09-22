"""XML 可视化编辑模型、Tk 交互与现有 C# 更新器互通。"""

from copy import deepcopy
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from unittest import mock
from xml.dom import Node

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "Tools/Update-Package-Tool"))
from manifest import Options, Rule, make_manifest, rule_operation
from package_builder import build, prepare
from xml_editor import XmlEditor, at_address, attributes, decode_xml, elements, infer_target, parse_xml, text_value


def structure(node):
    """比较命名空间、结构、属性和值，忽略序列化前缀与排版空白。"""
    if node.nodeType == Node.DOCUMENT_NODE:
        return tuple(structure(c) for c in node.childNodes)
    if node.nodeType == Node.ELEMENT_NODE:
        children = []
        for child in node.childNodes:
            if child.nodeType in {Node.TEXT_NODE, Node.CDATA_SECTION_NODE}:
                if child.data.strip() or not elements(node):
                    children.append(("text", child.data))
            else:
                children.append(structure(child))
        return (node.namespaceURI, node.localName,
                sorted((a.namespaceURI or "", a.localName, a.value) for a in attributes(node)), children)
    return (node.nodeType, node.nodeName, node.nodeValue)


class EditorTests(unittest.TestCase):
    def test_duplicate_edits_keep_preview_and_replay_stable(self):
        model = XmlEditor(b'<root><anchor/><leaf><![CDATA[same]]></leaf></root>')
        root = model.document.documentElement
        model.apply(root, "appendChild", xml='<group a="1" b="2"><child>value</child></group>')
        before = model.preview()
        model.apply(root, "appendChild", xml='<group b="2" a="1">\n <child><![CDATA[value]]></child>\n</group>')
        self.assertEqual(model.preview(), before)
        anchor = elements(root)[0]
        for action in ("insertBefore", "insertAfter"):
            model.apply(anchor, action, xml=f'<added position="{action}"/>')
            model.apply(anchor, action, xml=f'<added position="{action}"/>')
        model.apply(root, "addAttribute", name="enabled", value="true")
        before = model.preview()
        model.apply(root, "addAttribute", name="enabled", value="true")
        leaf = root.getElementsByTagName("leaf")[0]
        model.apply(leaf, "setValue", value="same")
        model.apply(leaf, "replaceElement", xml='<leaf>same</leaf>')
        self.assertEqual(model.preview(), before)
        self.assertEqual(len(root.getElementsByTagName("added")), 2)
        with self.assertRaises(ValueError):
            model.apply(root, "addAttribute", name="enabled", value="false")
        model.apply(root, "appendChild", xml='<group a="1" b="2"><child>different</child></group>')
        self.assertEqual(len(root.getElementsByTagName("group")), 2)
        rule = model.to_rule("contents/config.xml")
        self.assertEqual(XmlEditor.from_rule(rule).preview(), model.preview())

    def test_continuous_edits_identity_quotes_and_reopen(self):
        raw = b'''<root><item id="a'&quot;b"/><item/><item/></root>'''
        model = XmlEditor(raw, reference_path="only-a-reference.xml")
        first = elements(model.document.documentElement)[0]
        model.apply(first, "setValue", attribute="id", value="changed")
        self.assertIn("concat(", model.edits[0]["xpath"])
        model.apply(first, "addAttribute", name="value", value="result")
        self.assertIn("[@id='changed']", model.edits[1]["xpath"])
        second = elements(model.document.documentElement)[1]
        self.assertTrue(model.locator(second)[1])
        model.apply(second, "remove")
        self.assertEqual(model.edits[-1]["xpath"], "/root/item[2]")
        model.apply(model.document.documentElement, "appendChild", xml=model.fragment("new", "值", [("key", "k")]))
        model.apply(elements(model.document.documentElement)[-1], "setValue", value="后续")
        saved = model.to_rule("contents/config.xml")
        self.assertFalse(saved.enabled)
        self.assertEqual(structure(XmlEditor.from_rule(saved).document), structure(model.document))
        wire = rule_operation(saved, 1)
        self.assertEqual(set(wire), {"type", "target", "encoding", "edits"})
        self.assertNotIn("reference", json.dumps(wire))
        self.assertEqual(model.reference_bytes, raw)
        self.assertEqual(make_manifest(Options(rules=[saved]))["operations"],
                         [{"type": "copy", "source": "source/contents", "target": "contents"}])

    def test_undo_redo_branch_and_cancel_isolation(self):
        model = XmlEditor(b'<r><a x="1"/></r>')
        model.apply(elements(model.document.documentElement)[0], "setValue", value="one")
        rule = model.to_rule("contents/c.xml", True)
        original = deepcopy(rule)
        reopened = XmlEditor.from_rule(rule)
        reopened.apply(elements(reopened.document.documentElement)[0], "setValue", value="two")
        result = reopened.preview()
        reopened.undo()
        self.assertEqual(reopened.preview(), model.preview())
        reopened.redo()
        self.assertEqual(reopened.preview(), result)
        reopened.undo()
        reopened.apply(elements(reopened.document.documentElement)[0], "remove", attribute="x")
        reopened.redo()
        self.assertEqual(reopened.position, 2)
        self.assertEqual(len(reopened.actions), 2)
        self.assertEqual(rule, original)

    def test_namespaces_fragments_and_search(self):
        model = XmlEditor('<r xmlns="urn:r" xmlns:p="urn:p"><p:a name="查找" p:x="长值"/><a/></r>'.encode())
        root = model.document.documentElement
        child = elements(root)[0]
        model.apply(child, "setValue", attribute="p:x", value="修改")
        model.apply(child, "addAttribute", name="p:y", value="另一个")
        model.apply(root, "appendChild", xml='<plain><nested/></plain>')
        model.apply(elements(root)[-1], "addAttribute", name="id", value="plain")
        model.apply(root, "appendChild", xml=model.fragment("p:z", "", [("p:k", "v")], bindings=model.bindings(root)))
        model.apply(root, "appendChild", xml='<p:branch xmlns:p="urn:p"><noNamespace/></p:branch>')
        self.assertEqual(model.search("查找"), [(0,)])
        self.assertEqual(model.search("p:x"), [(0,)])
        self.assertEqual(model.search("修改"), [(0,)])
        self.assertEqual(structure(parse_xml(model.preview())), structure(model.document))
        self.assertEqual(len(model.to_rule("contents/c.xml").namespaces), 2)

    def test_comments_processing_instructions_and_restrictions(self):
        model = XmlEditor(b'<?keep a?><r><a>old<!--keep--><?in p?></a></r>')
        root = model.document.documentElement
        child = elements(root)[0]
        model.apply(child, "setValue", value="new")
        self.assertIn('<!--keep--><?in p?>', model.preview())
        for node, action, values in ((root, "setValue", {"value": "bad"}), (root, "remove", {}),
                                    (root, "insertBefore", {"xml": "<a/>"}),
                                    (child, "addAttribute", {"name": "xmlns", "value": "urn:x"}),
                                    (child, "appendChild", {"xml": "<!DOCTYPE a><a/>"}),
                                    (child, "setValue", {"value": "\x01"})):
            previous = model.preview(), deepcopy(model.edits)
            with self.subTest(action=action), self.assertRaises(ValueError):
                model.apply(node, action, **values)
            self.assertEqual((model.preview(), model.edits), previous)
        for raw in (b'<!DOCTYPE r [<!ENTITY x "expanded">]><r>&x;</r>',
                    b'<!DOCTYPE r SYSTEM "file:///secret"><r/>', b'<r>'):
            with self.assertRaises(ValueError):
                XmlEditor(raw)

    def test_all_encodings_and_conflicts(self):
        for codec, declaration, text, bom in (("utf-8", "UTF-8", "中文", b"\xef\xbb\xbf"),
                ("utf-16-le", "UTF-16", "中文", b"\xff\xfe"), ("utf-16-be", "UTF-16", "中文", b"\xfe\xff"),
                ("gbk", "GBK", "中文", b""), ("cp932", "Shift_JIS", "日本語", b"")):
            with self.subTest(codec=codec):
                data = bom + f'<?xml version="1.0" encoding="{declaration}"?><r>{text}</r>'.encode(codec)
                model = XmlEditor(data)
                self.assertEqual(text_value(model.document.documentElement), text)
                model.apply(model.document.documentElement, "setValue", value=text + "1")
                self.assertEqual(model.codec, codec)
        for data, requested in ((b'<?xml version="1.0" encoding="gbk"?><r/>', "utf-8"),
                                (b'\xef\xbb\xbf<?xml version="1.0" encoding="gbk"?><r/>', "auto"),
                                (b'<r>\xff</r>', "auto"), (b'<r/>', "latin-1")):
            with self.assertRaises(ValueError):
                XmlEditor(data, requested)
        model = XmlEditor(b'<?xml version="1.0" encoding="gbk"?><r/>')
        with self.assertRaises(UnicodeError):
            model.apply(model.document.documentElement, "setValue", value="😀")
        self.assertEqual(model.position, 0)

    def test_target_inference_and_payload_unchanged(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            original, output = root / "原始", root / "输出"
            (original / "prop").mkdir(parents=True)
            output.mkdir()
            ref = original / "prop/配置.xml"
            raw = b'<r><value>old</value></r>'
            ref.write_bytes(raw)
            model = XmlEditor(raw, reference_path=str(ref))
            model.apply(elements(model.document.documentElement)[0], "setValue", value="new")
            target = infer_target(ref, original)
            self.assertEqual(target, "contents/prop/配置.xml")
            self.assertEqual(infer_target(root / "outside.xml", original), "")
            options = Options(original=str(original), output=str(output), suffix="xml", rules=[model.to_rule(target, True)])
            package = build(prepare(options))
            self.assertEqual(ref.read_bytes(), raw)
            self.assertEqual((package / "source/contents/prop/配置.xml").read_bytes(), raw)
            self.assertEqual(len(json.loads((package / "update").read_bytes())["operations"]), 2)
            outside = root / "outside.xml"
            outside.write_bytes(raw)
            model.reference_path = str(outside)
            options.suffix = "outside"
            options.rules = [model.to_rule("contents/outside.xml", True)]
            package = build(prepare(options))
            self.assertFalse((package / "source/contents/outside.xml").exists())


@unittest.skipUnless(os.environ.get("UPDATE_TEST_WORKER"), "设置 UPDATE_TEST_WORKER 启用 C# 互通")
class CsharpEditorTests(unittest.TestCase):
    def test_generated_group_can_be_installed_twice_without_duplication(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            original, output, game = root / "original", root / "output", root / "game"
            (original / "data").mkdir(parents=True)
            (original / "data/payload").write_bytes(b'new')
            output.mkdir()
            (game / "contents").mkdir(parents=True)
            (game / "asphyxia").mkdir()
            raw = b'<root><anchor/><leaf>same</leaf></root>'
            config = game / "contents/config.xml"
            config.write_bytes(raw)
            model = XmlEditor(raw)
            node = model.document.documentElement
            anchor = elements(node)[0]
            model.apply(node, "addAttribute", name="enabled", value="true")
            for action, target in (("appendChild", node), ("insertBefore", anchor), ("insertAfter", anchor)):
                model.apply(target, action, xml=f'<added name="{action}"><child>new</child></added>')
                model.apply(target, action, xml=f'<added name="{action}"><child>new</child></added>')
            package = build(prepare(Options(original=str(original), output=str(output), suffix="repeat",
                                           rules=[model.to_rule("contents/config.xml", True)])))
            staging = game / ".media-update/tmp"
            shutil.copytree(package, staging)
            installed = None
            for _ in range(2):
                process = self.run_worker("apply", game, staging)
                self.assertEqual(process.returncode, 0, process.stdout.decode("utf-8-sig", errors="replace"))
                if installed is not None:
                    self.assertEqual(config.read_bytes(), installed)
                installed = config.read_bytes()
                self.assertEqual(structure(XmlEditor(installed).document), structure(parse_xml(model.preview())))

    def run_worker(self, mode, game, package):
        return subprocess.run([os.environ["UPDATE_TEST_WORKER"], mode, str(game), str(package)],
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60,
                              creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))

    def test_sequential_edits_match_preview_in_all_encodings(self):
        for codec, declaration, bom in (("utf-8", "UTF-8", b""), ("utf-16-le", "UTF-16", b"\xff\xfe"),
                                       ("utf-16-be", "UTF-16", b"\xfe\xff"), ("gbk", "GBK", b""),
                                       ("cp932", "Shift_JIS", b"")):
            with self.subTest(codec=codec), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                original, output, game = root / "original", root / "output", root / "game"
                (original / "prop").mkdir(parents=True)
                output.mkdir()
                (game / "contents/prop").mkdir(parents=True)
                (game / "asphyxia").mkdir()
                raw = bom + (f'<?xml version="1.0" encoding="{declaration}"?>'
                    '<?keep pi?><r xmlns="urn:r" xmlns:p="urn:p"><item id="a\'&quot;b" p:x="v">old<!--keep--></item>'
                    '<item/><item/><tail/></r>').encode(codec)
                ref = original / "prop/config.xml"
                ref.write_bytes(raw)
                model = XmlEditor(raw)
                docroot = model.document.documentElement
                first = elements(docroot)[0]
                model.apply(first, "setValue", attribute="id", value="changed")
                model.apply(first, "setValue", attribute="p:x", value="new")
                model.apply(first, "addAttribute", name="p:y", value="日本")
                model.apply(first, "setValue", value="text")
                model.apply(first, "remove", attribute="p:x")
                model.apply(elements(docroot)[1], "remove")
                model.apply(elements(docroot)[1], "insertBefore", xml='<before xmlns="urn:r"/>')
                model.apply(elements(docroot)[2], "insertAfter", xml='<after xmlns="urn:r"/>')
                model.apply(elements(docroot)[-1], "replaceElement", xml='<p:tail xmlns:p="urn:p"><p:child/></p:tail>')
                model.apply(docroot, "appendChild", xml='<plain><nested/></plain>')
                model.apply(elements(docroot)[-1], "addAttribute", name="id", value="new")
                model.apply(elements(elements(docroot)[-1])[0], "setValue", value="nested")
                model.apply(docroot, "appendChild", xml='<p:branch xmlns:p="urn:p"><noNamespace/></p:branch>')
                model.apply(elements(elements(docroot)[-1])[0], "addAttribute", name="lines", value="one\ntwo\tthree\rfour")
                model.apply(elements(elements(docroot)[-1])[0], "setValue", value="one\rtwo")
                rule = model.to_rule("contents/prop/config.xml", True)
                package = build(prepare(Options(original=str(original), output=str(output), suffix="xml", rules=[rule])))
                config = game / "contents/prop/config.xml"
                config.write_bytes(b'<different/>')
                staging = game / ".media-update/tmp"
                shutil.copytree(package, staging)
                for mode in ("verify", "apply"):
                    process = self.run_worker(mode, game, staging)
                    self.assertEqual(process.returncode, 0, process.stdout.decode("utf-8-sig", errors="replace"))
                actual = XmlEditor(config.read_bytes())
                self.assertEqual(structure(actual.document), structure(parse_xml(model.preview())))
                self.assertEqual(ref.read_bytes(), raw)

    def test_no_match_fails_without_modifying_game(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            original, output, game = root / "original", root / "output", root / "game"
            (original / "data").mkdir(parents=True)
            (original / "data/new").write_bytes(b'new')
            output.mkdir()
            (game / "contents").mkdir(parents=True)
            (game / "asphyxia").mkdir()
            raw = b'<r><different/></r>'
            config = game / "contents/config.xml"
            config.write_bytes(raw)
            model = XmlEditor(b'<r><expected/></r>')
            model.apply(elements(model.document.documentElement)[0], "setValue", value="v")
            package = build(prepare(Options(original=str(original), output=str(output), suffix="missing",
                                           rules=[model.to_rule("contents/config.xml", True)])))
            staging = game / ".media-update/tmp"
            shutil.copytree(package, staging)
            self.assertNotEqual(self.run_worker("apply", game, staging).returncode, 0)
            self.assertEqual(config.read_bytes(), raw)
            self.assertFalse((game / "contents/data/new").exists())


@unittest.skipUnless(os.environ.get("UPDATE_TOOL_UI_TEST"), "设置 UPDATE_TOOL_UI_TEST 启用桌面测试")
class EditorGuiTests(unittest.TestCase):
    def setUp(self):
        import tkinter as tk
        from xml_editor_gui import XmlEditorDialog
        self.root = tk.Tk()
        self.root.withdraw()
        self.dialog = XmlEditorDialog(self.root)
        self.addCleanup(self.root.destroy)
        self.errors = mock.patch("xml_editor_gui.messagebox.showerror")
        self.error = self.errors.start()
        self.addCleanup(self.errors.stop)

    def pump(self, dialog=None):
        dialog = dialog or self.dialog
        deadline = time.monotonic() + 10
        while not dialog.closed and dialog.busy and time.monotonic() < deadline:
            self.root.update()
            time.sleep(.005)
        self.root.update()
        self.assertFalse(dialog.busy and not dialog.closed)
        self.error.assert_not_called()

    def test_select_edit_form_fragment_undo_save_reopen_cancel(self):
        from xml_editor_gui import ElementDialog, XmlEditorDialog, put_text
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "prop").mkdir()
            ref = root / "prop/config.xml"
            raw = b'<root><item name="first" value="old"/></root>'
            ref.write_bytes(raw)
            self.dialog.original = str(root)
            with mock.patch("xml_editor_gui.filedialog.askopenfilename", return_value=str(ref)):
                self.dialog.pick_file()
            self.pump()
            self.assertFalse(self.dialog.enabled.get())
            self.assertEqual(self.dialog.target.get(), "contents/prop/config.xml")
            self.dialog.reveal((0,))
            self.dialog.attrs.selection_set("value")
            self.dialog.select_attribute()
            put_text(self.dialog.attribute_value, "长属性值" * 100)
            self.dialog.set_attribute()
            self.pump()
            node = self.dialog.selected_node()
            form = ElementDialog(self.dialog, self.dialog.model, node)
            form.name.set("added")
            put_text(form.attrs, "id=new\nvalue=a=b")
            put_text(form.value, "值")
            form.save()
            self.dialog.change("appendChild", xml=form.result)
            self.pump()
            self.dialog.history(False)
            self.pump()
            self.assertEqual(self.dialog.model.position, 1)
            self.dialog.history(True)
            self.pump()
            self.dialog.reveal((0, 0))
            self.dialog.change("insertAfter", xml='<fragment><nested/></fragment>')
            self.pump()
            preview = self.dialog.model.preview()
            self.dialog.save()
            self.pump()
            rule = self.dialog.result
            self.assertEqual(len(rule.edits), 3)
            reopened = XmlEditorDialog(self.root, rule)
            self.pump(reopened)
            self.assertEqual(reopened.model.preview(), preview)
            reopened.change("addAttribute", name="cancelled", value="v")
            self.pump(reopened)
            reopened.destroy()
            self.assertIsNone(reopened.result)
            self.assertEqual(len(rule.edits), 3)
            self.assertEqual(ref.read_bytes(), raw)

    def test_large_tree_search_lazy_display(self):
        model = XmlEditor(('<root>' + ''.join(f'<item name="n{i}" value="v{i}"/>' for i in range(10000)) + '</root>').encode())
        self.dialog.loaded(model)
        self.pump()
        self.assertEqual(len(self.dialog.tree.get_children("n:")), 1)
        self.dialog.query.set("n9999")
        self.dialog.search()
        self.pump()
        self.assertEqual(self.dialog.tree.selection(), ("n:9999",))
        self.assertLessEqual(len(self.dialog.tree.get_children("n:")), 202)
        self.assertIn("仅显示前", self.dialog.preview.get("1.0", "end"))
        previous = self.dialog.tree.get_children("n:")[0]
        self.dialog.tree.focus(previous)
        self.dialog.more()
        self.assertTrue(self.dialog.tree.exists("n:9600"))

    def test_cancel_during_background_read_and_invalid_target(self):
        self.dialog.loaded(XmlEditor(b'<r/>'))
        self.pump()
        self.dialog.change("addAttribute", name="a", value="v")
        self.pump()
        self.dialog.save()
        deadline = time.monotonic() + 5
        while self.dialog.busy and time.monotonic() < deadline:
            self.root.update()
            time.sleep(.005)
        self.error.assert_called_once()
        self.assertIsNone(self.dialog.result)
        self.error.reset_mock()
        started, release, finished = threading.Event(), threading.Event(), threading.Event()
        def slow_read():
            started.set()
            release.wait(5)
            finished.set()
            return XmlEditor(b'<other/>')
        self.dialog.work(slow_read, self.dialog.loaded)
        self.assertTrue(started.wait(1))
        self.dialog.destroy()
        release.set()
        self.assertTrue(finished.wait(1))
        self.root.update()
        self.assertIsNone(self.dialog.result)
        self.error.assert_not_called()


if __name__ == "__main__":
    unittest.main()
