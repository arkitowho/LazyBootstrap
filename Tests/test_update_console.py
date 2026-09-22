"""Windows 真实控制台回归；设置 UPDATE_TEST_UPDATER 为发布后的更新器。"""

import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest


class Coord(ctypes.Structure):
    _fields_ = [("x", ctypes.c_short), ("y", ctypes.c_short)]


class Rect(ctypes.Structure):
    _fields_ = [("left", ctypes.c_short), ("top", ctypes.c_short),
                ("right", ctypes.c_short), ("bottom", ctypes.c_short)]


class ScreenInfo(ctypes.Structure):
    _fields_ = [("size", Coord), ("cursor", Coord), ("attributes", wintypes.WORD),
                ("window", Rect), ("maximum", Coord)]


class Character(ctypes.Union):
    _fields_ = [("unicode", wintypes.WCHAR), ("ascii", ctypes.c_char)]


class Cell(ctypes.Structure):
    _fields_ = [("character", Character), ("attributes", wintypes.WORD)]


class KeyEvent(ctypes.Structure):
    _fields_ = [("down", wintypes.BOOL), ("repeat", wintypes.WORD), ("key", wintypes.WORD),
                ("scan", wintypes.WORD), ("character", wintypes.WCHAR), ("control", wintypes.DWORD)]


class EventUnion(ctypes.Union):
    _fields_ = [("key", KeyEvent), ("padding", ctypes.c_byte * 16)]


class InputEvent(ctypes.Structure):
    _fields_ = [("kind", wintypes.WORD), ("event", EventUnion)]


class CursorInfo(ctypes.Structure):
    _fields_ = [("size", wintypes.DWORD), ("visible", wintypes.BOOL)]


@unittest.skipUnless(os.name == "nt" and os.environ.get("UPDATE_TEST_UPDATER"), "需要 Windows 和 UPDATE_TEST_UPDATER")
class ConsoleUpdateTests(unittest.TestCase):
    def setUp(self):
        self.k = ctypes.WinDLL("kernel32", use_last_error=True)
        self.k.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                                      ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        self.k.CreateFileW.restype = wintypes.HANDLE
        self.k.GetConsoleScreenBufferInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(ScreenInfo)]
        self.k.ReadConsoleOutputW.argtypes = [wintypes.HANDLE, ctypes.POINTER(Cell), Coord, Coord, ctypes.POINTER(Rect)]
        self.k.SetConsoleWindowInfo.argtypes = [wintypes.HANDLE, wintypes.BOOL, ctypes.POINTER(Rect)]
        self.k.WriteConsoleInputW.argtypes = [wintypes.HANDLE, ctypes.POINTER(InputEvent), wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
        self.k.GetConsoleCursorInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(CursorInfo)]
        self.k.CloseHandle.argtypes = [wintypes.HANDLE]
        self.temporary = tempfile.TemporaryDirectory(prefix="LazyBootstrap-console-")
        self.root = Path(self.temporary.name)
        self.package = self.root / ".media-update/tmp"
        self.package.mkdir(parents=True)
        self.process = None
        self.handles = []

    def tearDown(self):
        if self.process is not None and self.process.poll() is None:
            self.process.kill()  # Only the child created by this test.
            self.process.wait(timeout=10)
        for handle in self.handles:
            self.k.CloseHandle(handle)
        self.k.FreeConsole()
        self.temporary.cleanup()

    def check(self, result):
        if not result:
            raise ctypes.WinError(ctypes.get_last_error())

    def launch(self, operation):
        (self.package / "update").write_text(json.dumps({"operations": [operation]}), encoding="utf-8")
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = subprocess.SW_HIDE
        # No UAC changes outside this test-owned child and temporary game directory.
        self.process = subprocess.Popen([os.environ["UPDATE_TEST_UPDATER"], "--game", str(self.root),
                                         "--package", str(self.package), "--parent-pid", "2147483647"],
                                        creationflags=subprocess.CREATE_NEW_CONSOLE, startupinfo=startup,
                                        env=dict(os.environ, __COMPAT_LAYER="RunAsInvoker"))
        self.k.FreeConsole()
        deadline = time.monotonic() + 5
        while not self.k.AttachConsole(self.process.pid):
            if time.monotonic() >= deadline:
                raise ctypes.WinError(ctypes.get_last_error())
            time.sleep(.05)
        self.output = self.k.CreateFileW("CONOUT$", 0xC0000000, 3, None, 3, 0, None)
        self.input = self.k.CreateFileW("CONIN$", 0xC0000000, 3, None, 3, 0, None)
        self.handles.extend([self.output, self.input])

    def screen(self):
        info = ScreenInfo()
        self.check(self.k.GetConsoleScreenBufferInfo(self.output, ctypes.byref(info)))
        window = info.window
        width = window.right - window.left + 1
        height = window.bottom - window.top + 1
        cells = (Cell * (width * height))()
        self.check(self.k.ReadConsoleOutputW(self.output, cells, Coord(width, height), Coord(0, 0), ctypes.byref(window)))
        rows = ["".join(cell.character.unicode for cell in cells[y * width:(y + 1) * width]
                        if not cell.attributes & 0x200) for y in range(height)]
        return "\n".join(rows), cells, info

    def wait_text(self, text, timeout=10):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            screen = self.screen()
            if text in screen[0]:
                return screen
            time.sleep(.1)
        self.fail(f"控制台未显示 {text!r}:\n{self.screen()[0]}")

    def press(self, key, count=1):
        records = (InputEvent * (2 * count))()
        for i in range(2 * count):
            records[i].kind = 1
            records[i].event.key = KeyEvent(i % 2 == 0, 1, key, 0, "\r" if key == 13 else "\0", 0)
        written = wintypes.DWORD()
        self.check(self.k.WriteConsoleInputW(self.input, records, len(records), ctypes.byref(written)))

    def test_success_resize_acknowledgement_and_cursor_restore(self):
        self.launch({"type": "delete", "target": "contents/missing"})
        text, cells, info = self.wait_text("Update Successful!")
        self.assertEqual(text.count("Update Successful!"), 1)
        self.assertIn("秒后", text)
        self.assertIn("无需修改", text)
        self.assertNotIn("游戏：", text)
        self.assertNotIn("日志：", text)
        self.assertNotIn("预演检查", text)
        self.assertNotIn("█", text)
        rows = text.splitlines()
        first_border = next(i for i, row in enumerate(rows) if "┌" in row)
        self.assertGreater(first_border, 0)
        self.assertTrue(all(not row.strip() for row in rows[:first_border]))
        self.assertTrue(any(cell.character.unicode == "U" and cell.attributes & 15 == 10 for cell in cells))
        cursor = CursorInfo()
        self.check(self.k.GetConsoleCursorInfo(self.output, ctypes.byref(cursor)))
        self.assertFalse(cursor.visible)
        top = info.window.top
        rectangle = Rect(0, top, 47, top + 13)
        self.check(self.k.SetConsoleWindowInfo(self.output, True, ctypes.byref(rectangle)))
        self.wait_text("回车关闭")
        self.assertNotIn("┌", self.screen()[0], "紧凑布局残留旧边框")
        self.assertIn("游戏：", self.screen()[0])
        self.assertIn("日志：", self.screen()[0])
        self.check(self.k.SetConsoleWindowInfo(self.output, True, ctypes.byref(info.window)))
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            expanded = self.screen()[0]
            if "┌" in expanded and expanded.count("Update Successful!") == 1:
                break
            time.sleep(.1)
        else:
            self.fail("扩大窗口后未重新生成异常卡片")
        expanded_rows = expanded.splitlines()
        top_border = next(i for i, row in enumerate(expanded_rows) if "┌" in row)
        bottom_border = next(i for i, row in enumerate(expanded_rows) if "└" in row)
        self.assertTrue(all(not row.strip() for row in expanded_rows[:top_border] + expanded_rows[bottom_border + 1:]),
                        "窗口放大或结果切换后，卡片外残留文字")
        self.assertIsNone(self.process.poll())
        self.press(13)
        self.assertEqual(self.process.wait(timeout=5), 0)
        self.check(self.k.GetConsoleCursorInfo(self.output, ctypes.byref(cursor)))
        self.assertTrue(cursor.visible)
        self.assertEqual(self.screen()[2].attributes, info.attributes)

    def test_failure_scroll_and_enter(self):
        source = "source/" + "测试目录/" * 90 + "missing-marker.bin"
        self.launch({"type": "copy", "source": source, "target": "contents/a"})
        info = self.screen()[2]
        rectangle = Rect(0, info.window.top, 47, info.window.top + 13)
        self.check(self.k.SetConsoleWindowInfo(self.output, True, ctypes.byref(rectangle)))
        initial = self.wait_text("预演失败")[0]
        self.assertNotIn("Update Successful!", initial)
        self.assertNotIn("missing-marker.bin", initial)
        for _ in range(100):
            self.press(0x28)
            time.sleep(.11)
            # The filename can wrap across two visual rows after preceding details change.
            if "missing-marker.bin" in "".join(row.rstrip() for row in self.screen()[0].splitlines()):
                break
        else:
            self.fail("错误详情不能滚动到源路径末尾：\n" + self.screen()[0])
        self.assertFalse((self.root / "contents/a").exists())
        self.press(13)
        self.assertEqual(self.process.wait(timeout=5), 1)

    def test_many_files_do_not_scroll_or_wait_between_operations(self):
        source = self.package / "source"
        source.mkdir()
        for i in range(150):
            (source / f"{i}.txt").write_text("new", encoding="utf-8")
        self.launch({"type": "copy", "source": "source", "target": "contents/files"})
        text, _, info = self.wait_text("Update Successful!")
        self.assertEqual(text.count("Update Successful!"), 1)
        self.assertEqual(info.window.top, 0, "控制台发生了向下滚屏")
        self.assertEqual(len(list((self.root / "contents/files").glob("*.txt"))), 150)
        self.assertNotIn("Installing", text)
        self.wait_text("回车关闭")
        self.press(13)
        self.assertEqual(self.process.wait(timeout=5), 0)


if __name__ == "__main__":
    unittest.main()
