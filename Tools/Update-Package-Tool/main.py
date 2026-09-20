"""Python 3.10+ / Windows x64 图形工具入口。"""

import os
import struct
import sys
import tkinter as tk
from tkinter import messagebox, ttk

from gui import App


def main():
    root = tk.Tk()
    root.withdraw()
    if os.name != "nt" or struct.calcsize("P") != 8 or sys.version_info < (3, 10):
        messagebox.showerror("运行环境不支持", "请使用 Windows x64 和 Python 3.10 或更新的 64 位版本。", parent=root)
        root.destroy()
        return 1
    style = ttk.Style(root)
    if "vista" in style.theme_names():
        style.theme_use("vista")
    root.option_add("*Font", ("Microsoft YaHei UI", 10))
    App(root)
    root.deiconify()
    root.mainloop()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
