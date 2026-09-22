"""中文 Tkinter 界面。工作线程仅通过队列传递消息，不访问 Tk 控件。"""

from copy import deepcopy
import json
import os
from pathlib import Path
import queue
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from tkinter.scrolledtext import ScrolledText

from manifest import ACTIONS, ENCODINGS, Options, Rule, rule_operation
from package_builder import Cancelled, build, content_candidates, prepare
from xml_editor_gui import XmlEditorDialog


KINDS = {"复制文件或目录": "copy", "删除目标": "delete", "修改 XML": "editXml"}


def parse_namespaces(text):
    result = {}
    for line in text.splitlines():
        if not line.strip():
            continue
        key, separator, value = line.partition("=")
        key, value = key.strip(), value.strip()
        if not separator or not key or not value or key in result:
            raise ValueError("命名空间每行填写 前缀=URI，前缀不能重复。")
        result[key] = value
    return result


class RuleDialog(tk.Toplevel):
    def __init__(self, parent, rule=None):
        super().__init__(parent)
        self.title("编辑附加规则" if rule else "添加附加规则")
        self.transient(parent)
        self.resizable(True, True)
        self.result = None
        rule = deepcopy(rule or Rule())
        self.enabled = tk.BooleanVar(value=rule.enabled)
        self.kind = tk.StringVar(value=next(k for k, v in KINDS.items() if v == rule.kind))
        self.action = tk.StringVar(value=next(k for k, v in ACTIONS.items() if v == rule.action))
        self.source = tk.StringVar(value=rule.source)
        self.target = tk.StringVar(value=rule.target)
        self.xpath = tk.StringVar(value=rule.xpath)
        self.name = tk.StringVar(value=rule.name)
        self.encoding = tk.StringVar(value=rule.encoding)
        container = ttk.Frame(self, padding=16)
        container.pack(fill="both", expand=True)
        container.columnconfigure(1, weight=1)
        ttk.Checkbutton(container, text="启用本条规则", variable=self.enabled).grid(row=0, column=0, columnspan=2, sticky="w")
        ttk.Label(container, text="操作类型").grid(row=1, column=0, sticky="w", pady=6)
        types = ttk.Combobox(container, textvariable=self.kind,
                             values=list(KINDS) if rule.kind == "editXml" else list(KINDS)[:2], state="readonly")
        types.grid(row=1, column=1, sticky="ew")
        types.bind("<<ComboboxSelected>>", lambda event: self.refresh())
        ttk.Label(container, text="游戏内目标路径").grid(row=2, column=0, sticky="w")
        ttk.Entry(container, textvariable=self.target, width=62).grid(row=2, column=1, sticky="ew")
        ttk.Label(container, text="相对于游戏根目录，例如 contents/prop/ea3-ident.xml").grid(row=3, column=1, sticky="w")
        self.copy_frame = ttk.Frame(container)
        self.copy_frame.grid(row=4, column=0, columnspan=2, sticky="ew", pady=8)
        self.copy_frame.columnconfigure(1, weight=1)
        ttk.Label(self.copy_frame, text="本地源路径").grid(row=0, column=0)
        ttk.Entry(self.copy_frame, textvariable=self.source).grid(row=0, column=1, sticky="ew")
        ttk.Button(self.copy_frame, text="选文件", command=lambda: self.pick_source(False)).grid(row=0, column=2)
        ttk.Button(self.copy_frame, text="选目录", command=lambda: self.pick_source(True)).grid(row=0, column=3)
        self.xml_frame = ttk.Frame(container)
        self.xml_frame.grid(row=5, column=0, columnspan=2, sticky="nsew")
        self.xml_frame.columnconfigure(1, weight=1)
        for row, (label, variable) in enumerate((("XPath", self.xpath), ("属性名", self.name))):
            ttk.Label(self.xml_frame, text=label).grid(row=row, column=0, sticky="w", pady=4)
            ttk.Entry(self.xml_frame, textvariable=variable).grid(row=row, column=1, sticky="ew")
        ttk.Label(self.xml_frame, text="XML 动作").grid(row=2, column=0, sticky="w")
        actions = ttk.Combobox(self.xml_frame, textvariable=self.action, values=list(ACTIONS), state="readonly")
        actions.grid(row=2, column=1, sticky="ew")
        actions.bind("<<ComboboxSelected>>", lambda event: self.refresh())
        ttk.Label(self.xml_frame, text="文件编码").grid(row=3, column=0, sticky="w", pady=4)
        ttk.Combobox(self.xml_frame, textvariable=self.encoding, values=ENCODINGS, state="readonly").grid(row=3, column=1, sticky="ew")
        self.value_label = ttk.Label(self.xml_frame, text="值（允许为空）")
        self.value_label.grid(row=4, column=0, sticky="nw", pady=4)
        self.value = ScrolledText(self.xml_frame, height=5, width=60, wrap="word")
        self.value.grid(row=4, column=1, sticky="ew")
        self.value.insert("1.0", rule.xml if rule.action in {"replaceElement", "appendChild", "insertBefore", "insertAfter"} else rule.value)
        ttk.Label(self.xml_frame, text="命名空间\n每行 前缀=URI").grid(row=5, column=0, sticky="nw", pady=6)
        self.namespaces = ScrolledText(self.xml_frame, height=3, width=60)
        self.namespaces.grid(row=5, column=1, sticky="ew", pady=6)
        self.namespaces.insert("1.0", "\n".join(f"{key}={value}" for key, value in rule.namespaces.items()))
        ttk.Label(container, text="静态检查不执行 XPath；实际匹配与安装权限由更新器预演检查。",
                  wraplength=620).grid(row=6, column=0, columnspan=2, sticky="w", pady=8)
        buttons = ttk.Frame(container)
        buttons.grid(row=7, column=0, columnspan=2, sticky="e")
        ttk.Button(buttons, text="保存规则", command=self.save).pack(side="left", padx=6)
        ttk.Button(buttons, text="取消", command=self.destroy).pack(side="left")
        self.refresh()
        self.update_idletasks()
        # 在主窗口内打开，不沿用 Tk 默认的屏幕左上角位置。
        self.geometry(f"+{parent.winfo_rootx() + 40}+{parent.winfo_rooty() + 60}")
        self.grab_set()

    def pick_source(self, directory):
        value = (filedialog.askdirectory(parent=self, title="选择复制源目录") if directory
                 else filedialog.askopenfilename(parent=self, title="选择复制源文件"))
        if value:
            self.source.set(value)

    def refresh(self):
        self.copy_frame.grid_remove()
        self.xml_frame.grid_remove()
        if KINDS[self.kind.get()] == "copy":
            self.copy_frame.grid()
        elif KINDS[self.kind.get()] == "editXml":
            self.xml_frame.grid()
        action = ACTIONS[self.action.get()]
        self.value_label.configure(text="XML 片段" if action in {"replaceElement", "appendChild", "insertBefore", "insertAfter"}
                                   else "值（允许为空）")
        self.value.configure(state="disabled" if action == "remove" else "normal")
        name_entry = self.xml_frame.grid_slaves(row=1, column=1)[0]
        name_entry.configure(state="normal" if action == "addAttribute" else "disabled")

    def save(self):
        try:
            kind = KINDS[self.kind.get()]
            action = ACTIONS[self.action.get()]
            rule = Rule(kind=kind, enabled=self.enabled.get(), source=self.source.get(),
                        target=self.target.get().strip(), action=action, xpath=self.xpath.get().strip(),
                        value=self.value.get("1.0", "end-1c"), xml=self.value.get("1.0", "end-1c"),
                        name=self.name.get().strip(), encoding=self.encoding.get(),
                        namespaces=parse_namespaces(self.namespaces.get("1.0", "end-1c")) if kind == "editXml" else {})
            rule_operation(rule, 1)
            self.result = rule
            self.destroy()
        except (ValueError, OSError) as error:
            messagebox.showerror("规则无效", str(error), parent=self)


class App(ttk.Frame):
    def __init__(self, root):
        super().__init__(root, padding=14)
        self.pack(fill="both", expand=True)
        self.root = root
        root.title("懒人包更新包创建工具")
        root.geometry("1000x760")
        root.minsize(820, 640)
        self.variables = {name: tk.StringVar() for name in
                          ("original", "output", "content_root", "old_version", "new_version", "suffix")}
        self.flags = {name: tk.BooleanVar(value=False) for name in
                      ("download_spice", "include_prerelease", "edit_version", "build_launcher", "asphyxia_enabled")}
        self.rules = []
        self.events = queue.Queue()
        self.stop = threading.Event()
        self.busy = False
        self.plan = None
        self.last_output = None
        self.states = []
        self.closing = False
        ttk.Label(self, text="更新包创建工具", font=("Microsoft YaHei UI", 18, "bold")).pack(anchor="w")
        self.import_button = ttk.Button(self, text="编辑现有更新包 · 导入更新包目录", command=self.edit_package)
        self.import_button.pack(anchor="e")
        ttk.Label(self, text="选择已解压的原始增量目录，生成 source、update 和 checksums。所有附加功能需手动勾选。",
                  wraplength=940).pack(anchor="w", pady=(6, 12))
        self.tabs = ttk.Notebook(self)
        self.tabs.pack(fill="both", expand=True)
        self.basic = ttk.Frame(self.tabs, padding=14)
        self.components = ttk.Frame(self.tabs, padding=14)
        self.rule_page = ttk.Frame(self.tabs, padding=14)
        self.preview_page = ttk.Frame(self.tabs, padding=10)
        for frame, title in ((self.basic, "基本信息"), (self.components, "可选组件"),
                             (self.rule_page, "附加规则"), (self.preview_page, "生成预览")):
            self.tabs.add(frame, text=title)
        self.basic.columnconfigure(1, weight=1)
        self.path_row(self.basic, 0, "原始增量目录 *", "original")
        self.path_row(self.basic, 1, "内容根目录", "content_root")
        ttk.Label(self.basic, text="留空则自动识别；存在多个候选时，请选择包含 data、modules、prop 等内容的目录。",
                  wraplength=700).grid(row=2, column=1, columnspan=2, sticky="w")
        self.path_row(self.basic, 3, "输出位置 *", "output")
        for row, (label, name) in enumerate((("旧版本", "old_version"), ("新版本", "new_version"), ("自定义名称后缀", "suffix")), 4):
            ttk.Label(self.basic, text=label).grid(row=row, column=0, sticky="w", pady=10)
            ttk.Entry(self.basic, textvariable=self.variables[name]).grid(row=row, column=1, columnspan=2, sticky="ew")
        ttk.Label(self.basic, text="默认名称：UPDATE_LAZY_KFC_旧版本 to 新版本\n日期版本为 YYYYMMDDNN，例如 2026080500；自定义后缀不改变固定前缀。\n仅输出目录，不压缩；已有同名输出不会被覆盖。",
                  wraplength=720, justify="left").grid(row=7, column=0, columnspan=3, sticky="w", pady=16)
        self.components.columnconfigure(1, weight=1)
        ttk.Checkbutton(self.components, text="下载最新 spice2x，仅加入 spice64.exe", variable=self.flags["download_spice"]).grid(row=0, column=0, columnspan=3, sticky="w", pady=8)
        ttk.Checkbutton(self.components, text="包含预发布版本（仅在勾选下载时生效）", variable=self.flags["include_prerelease"]).grid(row=1, column=0, columnspan=3, sticky="w", padx=24)
        ttk.Checkbutton(self.components, text="安装时修改 ea3-ident.xml 的版本日期", variable=self.flags["edit_version"]).grid(row=2, column=0, columnspan=3, sticky="w", pady=(16, 4))
        ttk.Label(self.components, text="使用基本信息中的新版本；排除原包同名文件，仅修改现有 /ea3_conf/soft/ext。",
                  wraplength=800).grid(row=3, column=0, columnspan=3, sticky="w", padx=24)
        ttk.Checkbutton(self.components, text="编译并加入启动器（含 launcher 和启动.exe）",
                        variable=self.flags["build_launcher"]).grid(row=4, column=0, columnspan=3, sticky="w", pady=(16, 4))
        ttk.Label(self.components, text="检查并预览时运行仓库 build.ps1；将 build 全部内容加入 source，Launcher.exe 重命名为启动.exe。\n编译会重建 build 目录；launcher 使用镜像更新，会移除安装目标中的多余文件。",
                  foreground="#9A4E00", wraplength=800).grid(row=5, column=0, columnspan=3, sticky="w", pady=(0, 10))
        ttk.Checkbutton(self.components, text="下载最新 asphyxia KFC 插件", variable=self.flags["asphyxia_enabled"]).grid(row=6, column=0, columnspan=3, sticky="w", pady=(12, 4))
        ttk.Label(self.components, text="从 22vv0/asphyxia_plugins 发布资产中选择最新的 kfc 开头 ZIP。\n解压至 source/asphyxia/plugins/sdvx@asphyxia；存在单一顶层目录时去掉这一层。",
                  wraplength=800).grid(row=7, column=0, columnspan=3, sticky="w")
        self.rule_tree = ttk.Treeview(self.rule_page, columns=("enabled", "kind", "target"), show="headings", selectmode="browse")
        for name, title, width in (("enabled", "启用", 60), ("kind", "操作", 140), ("target", "游戏内目标路径", 560)):
            self.rule_tree.heading(name, text=title)
            self.rule_tree.column(name, width=width, stretch=name == "target")
        self.rule_tree.pack(fill="both", expand=True)
        self.rule_tree.bind("<Double-1>", lambda event: self.edit_rule())
        self.rule_tree.bind("<Button-1>", self.click_rule)
        self.rule_tree.bind("<space>", lambda event: self.toggle_rule())
        controls = ttk.Frame(self.rule_page)
        controls.pack(fill="x", pady=8)
        for label, command in (("添加复制／删除", self.add_rule), ("添加 XML 规则", self.add_xml_rule), ("编辑", self.edit_rule), ("删除", self.delete_rule),
                               ("勾选 / 取消", self.toggle_rule), ("上移", lambda: self.move_rule(-1)),
                               ("下移", lambda: self.move_rule(1))):
            ttk.Button(controls, text=label, command=command).pack(side="left", padx=(0, 6))
        ttk.Label(self.rule_page, text="附加规则默认不启用；点击“启用”列勾选。按列表顺序在基础内容、组件和日期修改之后执行。",
                  wraplength=870).pack(anchor="w")
        self.preview_text = ScrolledText(self.preview_page, height=15, wrap="word", state="disabled")
        self.preview_text.pack(fill="both", expand=True)
        ttk.Label(self.preview_page, text="这里展示映射和完整 update；XPath 实际匹配与安装权限需要在游戏目录中由更新器预演检查。",
                  wraplength=870).pack(anchor="w", pady=6)
        self.status = tk.StringVar(value="请填写基本信息并预览。")
        ttk.Label(self, textvariable=self.status, wraplength=950).pack(anchor="w", pady=(10, 4))
        self.progress = ttk.Progressbar(self, mode="indeterminate")
        self.progress.pack(fill="x")
        self.log = ScrolledText(self, height=5, state="disabled", wrap="word")
        self.log.pack(fill="x", pady=8)
        buttons = ttk.Frame(self)
        buttons.pack(fill="x")
        self.preview_button = ttk.Button(buttons, text="检查并预览", command=self.preview)
        self.preview_button.pack(side="left")
        self.build_button = ttk.Button(buttons, text="生成更新包目录", command=self.generate)
        self.build_button.pack(side="left", padx=8)
        self.cancel_button = ttk.Button(buttons, text="取消任务", command=self.cancel, state="disabled")
        self.cancel_button.pack(side="left")
        self.open_button = ttk.Button(buttons, text="打开输出目录", command=self.open_output, state="disabled")
        self.open_button.pack(side="right")
        root.protocol("WM_DELETE_WINDOW", self.close)
        self.poll_id = root.after(80, self.poll)

    def edit_package(self):
        if not self.busy:
            from package_import_gui import PackageEditor
            dialog = PackageEditor(self.root)
            self.wait_window(dialog)

    def path_row(self, parent, row, label, name):
        ttk.Label(parent, text=label).grid(row=row, column=0, sticky="w", pady=10)
        ttk.Entry(parent, textvariable=self.variables[name]).grid(row=row, column=1, sticky="ew", padx=8)
        ttk.Button(parent, text="浏览", command=lambda: self.browse(name)).grid(row=row, column=2)

    def browse(self, name):
        value = filedialog.askdirectory(parent=self.root, title="选择目录", mustexist=True,
                                       initialdir=self.variables["original"].get() or None)
        if value:
            self.variables[name].set(value)
            if name == "original":
                self.variables["content_root"].set("")

    def collect(self):
        return Options(**{name: value.get().strip() for name, value in self.variables.items()},
                       **{name: value.get() for name, value in self.flags.items()}, rules=deepcopy(self.rules))

    def selected(self):
        selection = self.rule_tree.selection()
        return int(selection[0]) if selection else None

    def refresh_rules(self, selected=None):
        self.rule_tree.delete(*self.rule_tree.get_children())
        for index, rule in enumerate(self.rules):
            label = next(k for k, v in KINDS.items() if v == rule.kind)
            if rule.kind == "editXml" and rule.edits is not None:
                label = f"XML · {len(rule.edits)} 处修改"
            self.rule_tree.insert("", "end", iid=str(index), values=("☑" if rule.enabled else "☐", label, rule.target))
        if selected is not None and selected < len(self.rules):
            self.rule_tree.selection_set(str(selected))

    def add_rule(self):
        if self.busy:
            return
        dialog = RuleDialog(self.root)
        self.wait_window(dialog)
        if dialog.result:
            self.rules.append(dialog.result)
            self.refresh_rules(len(self.rules) - 1)

    def edit_rule(self):
        index = self.selected()
        if self.busy or index is None:
            return
        rule = self.rules[index]
        dialog = (XmlEditorDialog(self.root, rule, **self.xml_context()) if rule.reference_bytes is not None
                  else RuleDialog(self.root, rule))
        self.wait_window(dialog)
        if dialog.result:
            self.rules[index] = dialog.result
            self.refresh_rules(index)

    def xml_context(self):
        return {key: self.variables[key].get().strip() for key in ("content_root", "original")}

    def add_xml_rule(self):
        if self.busy:
            return
        dialog = XmlEditorDialog(self.root, **self.xml_context())
        self.wait_window(dialog)
        if dialog.result:
            self.rules.append(dialog.result)
            self.refresh_rules(len(self.rules) - 1)

    def delete_rule(self):
        index = self.selected()
        if not self.busy and index is not None:
            del self.rules[index]
            self.refresh_rules()

    def click_rule(self, event):
        row = self.rule_tree.identify_row(event.y)
        if row and self.rule_tree.identify_column(event.x) == "#1" and not self.busy:
            self.rule_tree.selection_set(row)
            self.toggle_rule()
            return "break"

    def toggle_rule(self):
        index = self.selected()
        if not self.busy and index is not None:
            self.rules[index].enabled = not self.rules[index].enabled
            self.refresh_rules(index)

    def move_rule(self, delta):
        index = self.selected()
        if not self.busy and index is not None and 0 <= index + delta < len(self.rules):
            self.rules[index], self.rules[index + delta] = self.rules[index + delta], self.rules[index]
            self.refresh_rules(index + delta)

    def set_busy(self, value):
        self.busy = value
        if value:
            self.states = []
            def disable(widget):
                for child in widget.winfo_children():
                    if isinstance(child, (ttk.Entry, ttk.Button, ttk.Checkbutton, ttk.Combobox)):
                        self.states.append((child, str(child.cget("state"))))
                        child.configure(state="disabled")
                    disable(child)
            disable(self.tabs)
            self.preview_button.configure(state="disabled")
            self.build_button.configure(state="disabled")
            self.cancel_button.configure(state="normal")
            self.progress.start(12)
        else:
            for widget, state in self.states:
                widget.configure(state=state)
            self.preview_button.configure(state="normal")
            self.build_button.configure(state="normal")
            self.cancel_button.configure(state="disabled")
            self.progress.stop()

    def start_worker(self, job, event):
        if self.busy:
            return
        self.stop.clear()
        self.set_busy(True)
        def checkpoint():
            if self.stop.is_set():
                raise Cancelled("已取消任务。")
        def worker():
            try:
                result = job(lambda text: self.events.put(("log", text)), checkpoint)
                self.events.put((event, result))
            except Cancelled:
                self.events.put(("cancelled", "任务已取消，原始文件保持不变。"))
            except Exception as error:
                self.events.put(("error", str(error)))
        threading.Thread(target=worker, daemon=False).start()

    def preview(self):
        options = self.collect()
        self.plan = None
        def job(report, cancel):
            if not options.content_root:
                candidates = content_candidates(options.original, cancel)
                if len(candidates) != 1:
                    report("候选内容目录：" + ("；".join(map(str, candidates)) or "未识别"))
                    raise ValueError("请在“基本信息”的“内容根目录”中手动选择游戏增量目录，再预览。")
            return prepare(options, report, cancel)
        self.start_worker(job, "preview")

    def generate(self):
        if self.plan is None or self.collect() != self.plan.options:
            messagebox.showinfo("需要预览", "请先点击“检查并预览”，查看当前映射与清单后再生成。", parent=self.root)
            return
        plan = self.plan
        self.start_worker(lambda report, cancel: build(plan, report, cancel), "complete")

    def cancel(self):
        self.stop.set()
        self.status.set("正在取消并清理本次暂存内容……网络读取最多等待 20 秒超时。")
        self.cancel_button.configure(state="disabled")

    def append_log(self, text):
        self.log.configure(state="normal")
        self.log.insert("end", text + "\n")
        if int(self.log.index("end-1c").split(".")[0]) > 600:
            self.log.delete("1.0", "101.0")
        self.log.see("end")
        self.log.configure(state="disabled")

    def poll(self):
        for _ in range(150):
            try:
                event, data = self.events.get_nowait()
            except queue.Empty:
                break
            if event == "log":
                self.append_log(data)
                if not self.stop.is_set():
                    self.status.set(data)
                continue
            self.set_busy(False)
            if self.closing:
                self.root.destroy()
                return
            if event == "preview":
                self.plan = data
                text = "输出目录：" + str(data.destination) + "\n\n" + "\n".join(data.mappings)
                text += "\n\n完整 update：\n" + json.dumps(data.manifest, ensure_ascii=False, indent=2)
                self.preview_text.configure(state="normal")
                self.preview_text.delete("1.0", "end")
                self.preview_text.insert("1.0", text)
                self.preview_text.configure(state="disabled")
                self.tabs.select(self.preview_page)
                self.status.set("预览完成。检查映射与清单后，可生成更新包目录。")
            elif event == "complete":
                self.last_output = data
                self.plan = None
                self.open_button.configure(state="normal")
                self.status.set("生成完成：" + str(data))
                self.append_log("生成完成：" + str(data))
                messagebox.showinfo("生成完成", "更新包目录已生成：\n" + str(data), parent=self.root)
            else:
                self.plan = None
                self.status.set("已取消" if event == "cancelled" else "任务失败，请查看日志。")
                self.append_log(data)
                if event == "error":
                    messagebox.showerror("任务失败", data, parent=self.root)
        self.poll_id = self.root.after(80, self.poll)

    def open_output(self):
        if self.last_output:
            try:
                os.startfile(self.last_output)
            except OSError as error:
                messagebox.showerror("无法打开目录", str(error), parent=self.root)

    def close(self):
        if self.busy:
            self.closing = True
            self.cancel()
        else:
            self.root.after_cancel(self.poll_id)
            self.root.destroy()
