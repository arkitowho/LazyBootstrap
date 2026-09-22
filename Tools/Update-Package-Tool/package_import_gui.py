"""现有包编辑窗口：会话覆盖层、统一操作列表和安全保存预览。"""

from copy import deepcopy
import json
import os
from pathlib import Path
import queue
import threading
import time
import tkinter as tk
from tkinter import filedialog, messagebox, simpledialog, ttk
from tkinter.scrolledtext import ScrolledText

from manifest import Options, Rule
from package_builder import Cancelled
from package_import import import_package, parse_operation, prepare_import, save_import
from import_xml_gui import ImportedXmlDialog
from xml_editor_gui import put_text
from xml_replay import infer_reference


LABELS = {"copy": "复制", "mirror": "镜像（移除多余文件）", "delete": "删除目标", "editXml": "XML 规则组"}


class IntegrityDialog(tk.Toplevel):
    def __init__(self, parent, issues):
        super().__init__(parent)
        self.title("更新包校验存在问题")
        self.transient(parent)
        self.result = False
        frame = ttk.Frame(self, padding=16)
        frame.pack(fill="both", expand=True)
        ttk.Label(frame, text="以下问题可能来自载荷修改或缺失。仅在确认需要修复此包时继续编辑；保存时会重新生成校验。",
                  wraplength=740).pack(anchor="w", pady=8)
        text = ScrolledText(frame, width=95, height=20, wrap="word")
        text.pack(fill="both", expand=True)
        put_text(text, "\n".join(issues), True)
        ttk.Button(frame, text="继续编辑", command=self.accept).pack(side="right", pady=8)
        ttk.Button(frame, text="取消导入", command=self.destroy).pack(side="right", padx=8)
        self.grab_set()

    def accept(self):
        self.result = True
        self.destroy()


class PackageRuleDialog(tk.Toplevel):
    def __init__(self, parent, rule=None, selected_source=""):
        super().__init__(parent)
        self.title("编辑包内操作")
        self.transient(parent)
        self.result = None
        self.rule = deepcopy(rule or Rule())
        self.kind = tk.StringVar(value=LABELS[self.rule.kind])
        self.source = tk.StringVar(value=self.rule.package_source or selected_source)
        self.target = tk.StringVar(value=self.rule.target)
        self.enabled = tk.BooleanVar(value=self.rule.enabled)
        frame = ttk.Frame(self, padding=16)
        frame.pack(fill="both", expand=True)
        frame.columnconfigure(1, weight=1)
        for row, (label, variable) in enumerate((("操作类型", self.kind), ("包内源路径", self.source), ("游戏内目标路径", self.target))):
            ttk.Label(frame, text=label).grid(row=row, column=0, sticky="w", padx=6, pady=8)
            widget = (ttk.Combobox(frame, textvariable=variable, values=list(LABELS.values())[:3], state="readonly", width=60)
                      if row == 0 else ttk.Entry(frame, textvariable=variable, width=65))
            widget.grid(row=row, column=1, sticky="ew")
        ttk.Label(frame, text="源路径相对于包目录，须位于 source 内；可先在文件树选择载荷，再添加操作。\n删除操作不使用源路径。镜像操作会移除游戏目标目录中的多余文件！",
                  foreground="#9A4E00", wraplength=660).grid(row=3, column=0, columnspan=2, sticky="w", pady=8)
        ttk.Checkbutton(frame, text="启用操作", variable=self.enabled).grid(row=4, column=0, columnspan=2, sticky="w")
        ttk.Button(frame, text="保存操作", command=self.save).grid(row=5, column=1, sticky="e", pady=8)
        self.grab_set()

    def save(self):
        try:
            kind = next(k for k, v in LABELS.items() if v == self.kind.get())
            operation = {"type": kind, "target": self.target.get().strip()}
            if kind in {"copy", "mirror"}:
                operation["source"] = self.source.get().strip()
            rule = parse_operation(operation)
            rule.enabled = self.enabled.get()
            self.result = rule
            self.destroy()
        except (ValueError, TypeError) as error:
            messagebox.showerror("操作无效", str(error), parent=self)


class PackageEditor(tk.Toplevel):
    PAGE = 200

    def __init__(self, parent):
        super().__init__(parent)
        self.title("编辑现有更新包")
        self.geometry("1120x850")
        self.minsize(940, 700)
        self.transient(parent)
        self.session = None
        self.plan = None
        self.revision = 0
        self.preview_revision = None
        self.last_output = None
        self.busy = False
        self.closing = False
        self.stop = threading.Event()
        self.events = queue.Queue()
        self.states = []
        self.children_map = {}
        self.root_path = tk.StringVar(value="尚未导入更新包")
        self.variables = {name: tk.StringVar() for name in ("output", "old_version", "new_version", "suffix")}
        self.flags = {name: tk.BooleanVar(value=False) for name in ("download_spice", "include_prerelease", "asphyxia_enabled", "build_launcher")}
        self.overwrite = tk.BooleanVar(value=False)
        self.status = tk.StringVar(value="导入已解压的更新包目录。载荷与规则只在编辑会话中修改。")
        self.references = tk.StringVar()
        outer = ttk.Frame(self, padding=12)
        outer.pack(fill="both", expand=True)
        top = ttk.Frame(outer)
        top.pack(fill="x")
        self.import_button = ttk.Button(top, text="导入更新包目录", command=self.choose_import)
        self.import_button.pack(side="left")
        ttk.Entry(top, textvariable=self.root_path, state="readonly").pack(side="left", fill="x", expand=True, padx=8)
        self.tabs = ttk.Notebook(outer)
        self.tabs.pack(fill="both", expand=True, pady=10)
        self.rules_page, self.files_page, self.settings, self.preview_page = [ttk.Frame(self.tabs, padding=10) for _ in range(4)]
        for page, title in ((self.rules_page, "全部操作"), (self.files_page, "包内文件"), (self.settings, "保存与组件"), (self.preview_page, "完整生成预览")):
            self.tabs.add(page, text=title)
        self.rule_tree = ttk.Treeview(self.rules_page, columns=("enabled", "kind", "target", "state"), show="headings", selectmode="browse")
        for name, label, width in (("enabled", "启用", 50), ("kind", "操作", 160), ("target", "游戏内目标路径", 400), ("state", "状态", 320)):
            self.rule_tree.heading(name, text=label)
            self.rule_tree.column(name, width=width, stretch=name != "enabled")
        scroll = ttk.Scrollbar(self.rules_page, command=self.rule_tree.yview)
        self.rule_tree.configure(yscrollcommand=scroll.set)
        scroll.pack(side="right", fill="y")
        self.rule_tree.pack(fill="both", expand=True)
        self.rule_tree.bind("<Double-1>", lambda event: self.edit_rule())
        self.rule_tree.bind("<space>", lambda event: self.toggle_rule())
        self.rule_tree.bind("<Button-1>", self.click_rule)
        bar = ttk.Frame(self.rules_page)
        bar.pack(fill="x", pady=8)
        for title, command in (("添加复制 / 镜像 / 删除", self.add_rule), ("添加 XML", self.add_xml), ("编辑", self.edit_rule),
                               ("启用 / 停用", self.toggle_rule), ("删除", self.remove_rule), ("上移", lambda: self.move_rule(-1)), ("下移", lambda: self.move_rule(1))):
            ttk.Button(bar, text=title, command=command).pack(side="left", padx=(0, 5))
        ttk.Label(self.rules_page, text="导入操作默认启用，新增操作默认停用；严格按列表顺序执行。未验证的原 XML 规则可保留导出。",
                  wraplength=970).pack(anchor="w")
        self.file_tree = ttk.Treeview(self.files_page, columns=("type",), selectmode="browse")
        self.file_tree.heading("#0", text="包内路径（按需展开）")
        self.file_tree.heading("type", text="类型")
        self.file_tree.column("type", width=100, stretch=False)
        file_scroll = ttk.Scrollbar(self.files_page, command=self.file_tree.yview)
        self.file_tree.configure(yscrollcommand=file_scroll.set)
        file_scroll.pack(side="right", fill="y")
        self.file_tree.pack(fill="both", expand=True)
        self.file_tree.bind("<<TreeviewOpen>>", self.expand_files)
        self.file_tree.bind("<<TreeviewSelect>>", self.select_file)
        self.file_tree.bind("<Double-1>", self.more_files)
        bar = ttk.Frame(self.files_page)
        bar.pack(fill="x", pady=8)
        for title, command in (("添加文件", lambda: self.add_file(False)), ("添加目录", lambda: self.add_file(True)),
                               ("替换所选载荷", self.replace_file), ("移除所选载荷", self.remove_file)):
            ttk.Button(bar, text=title, command=command).pack(side="left", padx=(0, 6))
        ttk.Label(self.files_page, textvariable=self.references, wraplength=970).pack(anchor="w")
        ttk.Label(self.files_page, text="保留未引用文件和空目录；update 与 checksums 由工具生成。移除载荷不会自动移除引用操作。").pack(anchor="w")
        self.settings.columnconfigure(1, weight=1)
        for row, (label, name) in enumerate((("另存为位置", "output"), ("旧版本", "old_version"), ("新版本", "new_version"), ("自定义名称后缀", "suffix"))):
            ttk.Label(self.settings, text=label).grid(row=row, column=0, sticky="w", padx=6, pady=6)
            ttk.Entry(self.settings, textvariable=self.variables[name]).grid(row=row, column=1, sticky="ew")
        ttk.Button(self.settings, text="选择输出位置", command=self.choose_output).grid(row=0, column=2, padx=8)
        ttk.Checkbutton(self.settings, text="覆盖原目录（成功后保留同级备份）", variable=self.overwrite, command=self.save_mode).grid(row=4, column=0, columnspan=3, sticky="w", pady=10)
        ttk.Label(self.settings, text="另存为名称固定以 UPDATE_LAZY_KFC_ 开头。覆盖前请检查预览中的原目录与备份位置。",
                  wraplength=950).grid(row=5, column=0, columnspan=3, sticky="w")
        for row, (name, label) in enumerate((("download_spice", "下载最新 spice2x（仅 spice64.exe）"), ("include_prerelease", "spice2x 包含预发布"),
                                            ("asphyxia_enabled", "下载最新 asphyxia KFC 插件"), ("build_launcher", "运行 build.ps1 编译启动器（含 launcher 和启动.exe）")), 6):
            ttk.Checkbutton(self.settings, text=label, variable=self.flags[name]).grid(row=row, column=0, columnspan=3, sticky="w", pady=6)
        ttk.Label(self.settings, text="组件全部默认关闭。所选组件使用独立载荷路径，操作追加到当前清单末尾。\n启动器在预览时编译，会重建仓库 build；launcher 镜像会移除游戏目标的多余文件。\n版本日期请使用 XML 规则编辑；导入模式不会排除 ea3-ident.xml。",
                  wraplength=940, foreground="#9A4E00").grid(row=10, column=0, columnspan=3, sticky="w", pady=8)
        self.preview_text = ScrolledText(self.preview_page, wrap="none", state="disabled")
        self.preview_text.pack(fill="both", expand=True)
        ttk.Label(outer, textvariable=self.status, wraplength=1060).pack(anchor="w")
        self.progress = ttk.Progressbar(outer, mode="indeterminate")
        self.progress.pack(fill="x", pady=4)
        self.log = ScrolledText(outer, height=5, wrap="word", state="disabled")
        self.log.pack(fill="x", pady=4)
        controls = ttk.Frame(outer)
        controls.pack(fill="x")
        self.preview_button = ttk.Button(controls, text="检查并预览", command=self.prepare)
        self.preview_button.pack(side="left")
        self.generate_button = ttk.Button(controls, text="另存为新更新包", command=self.generate)
        self.generate_button.pack(side="left", padx=8)
        self.cancel_button = ttk.Button(controls, text="取消任务", command=self.stop.set, state="disabled")
        self.cancel_button.pack(side="left")
        self.open_button = ttk.Button(controls, text="打开输出目录", command=self.open_output)
        self.open_button.pack(side="right")
        self.protocol("WM_DELETE_WINDOW", self.close)
        self.poll_id = self.after(60, self.poll)
        self.grab_set()

    def options(self):
        return Options(**{name: var.get().strip() for name, var in self.variables.items()}, **{name: var.get() for name, var in self.flags.items()})

    def log_message(self, text):
        self.log.configure(state="normal")
        self.log.insert("end", text + "\n")
        # 限制界面缓冲区，避免大包校验的逐文件日志拖慢 Tk。
        if int(self.log.index("end-1c").split(".")[0]) > 4000:
            self.log.delete("1.0", "1000.0")
        self.log.see("end")
        self.log.configure(state="disabled")

    def set_busy(self, busy):
        self.busy = busy
        if busy:
            self.states = []
            def disable(widget):
                for child in widget.winfo_children():
                    if isinstance(child, (ttk.Button, ttk.Checkbutton, ttk.Entry, ttk.Combobox, ttk.Treeview)) and child is not self.cancel_button:
                        self.states.append((child, child.state()))
                        child.state(["disabled"])
                    disable(child)
            disable(self)
            self.cancel_button.configure(state="normal")
            self.progress.start(15)
        else:
            for widget, state in self.states:
                widget.state(["!disabled", *state])
            self.states = []
            self.cancel_button.configure(state="disabled")
            self.progress.stop()

    def work(self, job, complete):
        if self.busy or self.closing:
            return
        self.stop.clear()
        self.set_busy(True)
        def check():
            if self.stop.is_set():
                raise Cancelled("已取消；原包与当前编辑会话保持不变。")
        def run():
            last_progress = 0.0
            def report(message):
                nonlocal last_progress
                # 大包逐文件检查可产生数十万条消息；限频进度，始终保留结果与备份日志。
                bulk = message.startswith(("正在读取并校验", "正在计算 SHA-256", "正在复制载荷"))
                now = time.monotonic()
                if not bulk or now - last_progress >= .08:
                    self.events.put(("log", message))
                    if bulk:
                        last_progress = now
            try:
                result = job(report, check)
                self.events.put(("done", (complete, result)))
            except Exception as error:
                self.events.put(("error", error))
        threading.Thread(target=run, daemon=False).start()

    def poll(self):
        for _ in range(150):
            try:
                kind, value = self.events.get_nowait()
            except queue.Empty:
                break
            if kind == "log":
                self.log_message(value)
            else:
                self.set_busy(False)
                if kind == "error":
                    self.status.set(str(value))
                    self.log_message(str(value))
                    if not isinstance(value, Cancelled) and not self.closing:
                        messagebox.showerror("操作失败", str(value), parent=self)
                elif not self.closing:
                    try:
                        callback, result = value
                        callback(result)
                    except Exception as error:
                        self.log_message(str(error))
                        messagebox.showerror("操作失败", str(error), parent=self)
                if self.closing:
                    self.destroy()
                    return
        self.poll_id = self.after(60, self.poll)

    def choose_import(self):
        path = filedialog.askdirectory(parent=self, title="选择已解压更新包目录")
        if path:
            self.load_package(path)

    def load_package(self, path):
        def imported(session):
            if session.integrity_issues:
                dialog = IntegrityDialog(self, session.integrity_issues)
                self.wait_window(dialog)
                self.grab_set()
                if not dialog.result:
                    self.status.set("已取消导入；保留原编辑会话。")
                    return
                session.integrity_accepted = True
            self.session = session
            self.root_path.set(str(session.root))
            self.variables["output"].set(str(session.root.parent))
            for name, value in zip(("old_version", "new_version", "suffix"), session.naming()):
                self.variables[name].set(value)
            self.overwrite.set(False)
            for flag in self.flags.values():
                flag.set(False)
            self.save_mode()
            self.changed()
            self.log_message(f"已导入 {len(session.rules)} 个操作；原包不会在编辑期间被修改。")
        self.work(lambda progress, cancel: import_package(path, progress, cancel), imported)

    def changed(self, selected=None):
        self.revision += 1
        self.plan = None
        self.refresh_rules(selected)
        self.refresh_files()
        errors = self.session.issues()
        self.status.set((f"存在 {len(errors)} 个载荷问题，修复前不能生成：" + errors[0]) if errors else "会话已更新，请检查并预览后生成。")

    def selected_rule(self):
        selected = self.rule_tree.selection()
        return int(selected[0]) if selected else None

    def refresh_rules(self, selected=None):
        self.rule_tree.delete(*self.rule_tree.get_children())
        for index, rule in enumerate(self.session.rules):
            state = ""
            if rule.kind in {"copy", "mirror"}:
                state = "引用载荷：" + rule.package_source
                if self.session.find(rule.package_source) is None:
                    state = "错误：载荷缺失 — " + rule.package_source
            elif rule.kind == "editXml":
                state = f"{len(rule.edits or [])} 步 · " + ("参考快照已回放" if rule.visual_verified else "尚未可视化验证，可原样保留")
            self.rule_tree.insert("", "end", iid=str(index), values=("☑" if rule.enabled else "☐", LABELS[rule.kind], rule.target, state))
        if selected is not None and selected < len(self.session.rules):
            self.rule_tree.selection_set(str(selected))
            self.rule_tree.see(str(selected))

    def selected_file(self):
        selected = self.file_tree.selection()
        return selected[0][2:] if selected and selected[0].startswith("f:") else ""

    def refresh_files(self):
        self.file_tree.delete(*self.file_tree.get_children())
        self.children_map = {}
        for name in self.session.entries:
            parent = name.rpartition("/")[0]
            self.children_map.setdefault(parent, []).append(name)
        for names in self.children_map.values():
            names.sort(key=lambda p: (not self.session.entries[p].directory, p.upper()))
        self.populate_files("")
        for name in ("update", "checksums"):
            self.file_tree.insert("", "end", text=name, values=("工具管理",))
        self.references.set("")

    def populate_files(self, parent, offset=0):
        iid = "f:" + parent if parent else ""
        children = self.children_map.get(parent, [])
        if not offset:
            self.file_tree.delete(*self.file_tree.get_children(iid))
        for name in children[offset:offset + self.PAGE]:
            entry = self.session.entries[name]
            child = "f:" + name
            self.file_tree.insert(iid, "end", iid=child, text=Path(name).name, values=("目录" if entry.directory else "文件",))
            if self.children_map.get(name):
                self.file_tree.insert(child, "end", text="展开加载…")
        if len(children) > offset + self.PAGE:
            self.file_tree.insert(iid, "end", iid=f"more:{offset + self.PAGE}:{parent}", text="双击加载更多…")

    def expand_files(self, event=None):
        if self.busy:
            return
        iid = self.file_tree.focus()
        children = self.file_tree.get_children(iid)
        if iid.startswith("f:") and children and not children[0].startswith(("f:", "more:")):
            self.populate_files(iid[2:])

    def more_files(self, event=None):
        iid = self.file_tree.focus()
        if not self.busy and iid.startswith("more:"):
            _, offset, parent = iid.split(":", 2)
            self.file_tree.delete(iid)
            self.populate_files(parent, int(offset))

    def select_file(self, event=None):
        path = self.selected_file()
        if path and self.session:
            refs = self.session.references(path)
            self.references.set("引用此载荷的操作：" + ("、".join(str(index + 1) for index in refs) if refs else "无（仍会随包保留）"))

    def add_file(self, directory):
        if self.busy or not self.session:
            return
        source = (filedialog.askdirectory(parent=self, title="添加载荷目录") if directory else filedialog.askopenfilename(parent=self, title="添加载荷文件"))
        if not source:
            return
        selected = self.selected_file()
        parent = selected if selected and self.session.entries[selected].directory else "source"
        destination = simpledialog.askstring("包内路径", "添加到包内的相对路径：", initialvalue=parent + "/" + Path(source).name, parent=self)
        if destination:
            self.put_payload(source, destination, False)

    def replace_file(self):
        path = self.selected_file()
        if self.busy or not path:
            return
        entry = self.session.entries[path]
        source = (filedialog.askdirectory(parent=self, title="选择替换目录") if entry.directory else filedialog.askopenfilename(parent=self, title="选择替换文件"))
        if source:
            self.put_payload(source, path, True)

    def put_payload(self, source, destination, replace):
        def update(progress, cancel):
            session = deepcopy(self.session)
            session.put(source, destination, replace=replace, progress=progress, cancel=cancel)
            return session
        def finish(result):
            self.session = result
            self.changed()
        self.work(update, finish)

    def remove_file(self):
        path = self.selected_file()
        if self.busy or not path:
            return
        def remove(progress, cancel):
            session = deepcopy(self.session)
            session.remove(path)
            cancel()
            return session
        def finish(result):
            self.session = result
            self.changed()
        self.work(remove, finish)

    def add_rule(self):
        if self.busy or not self.session:
            return
        dialog = PackageRuleDialog(self, selected_source=self.selected_file())
        self.wait_window(dialog)
        self.grab_set()
        if dialog.result:
            self.session.rules.append(dialog.result)
            self.changed(len(self.session.rules) - 1)

    def add_xml(self):
        if not self.busy and self.session:
            self.open_xml(Rule(kind="editXml", edits=[]))

    def open_xml(self, rule, index=None, reference=None, path=""):
        dialog = ImportedXmlDialog(self, rule, reference, path)
        self.wait_window(dialog)
        self.grab_set()
        if dialog.result:
            if index is None:
                self.session.rules.append(dialog.result)
                index = len(self.session.rules) - 1
            else:
                self.session.rules[index] = dialog.result
            self.changed(index)

    def edit_rule(self):
        index = self.selected_rule()
        if self.busy or index is None:
            return
        rule = self.session.rules[index]
        if rule.kind == "editXml":
            def inferred(result):
                data, path = result
                self.open_xml(rule, index, data, path)
            self.work(lambda progress, cancel: infer_reference(self.session, index, cancel), inferred)
        else:
            dialog = PackageRuleDialog(self, rule)
            self.wait_window(dialog)
            self.grab_set()
            if dialog.result:
                self.session.rules[index] = dialog.result
                self.changed(index)

    def toggle_rule(self):
        index = self.selected_rule()
        if not self.busy and index is not None:
            self.session.rules[index].enabled = not self.session.rules[index].enabled
            self.changed(index)

    def click_rule(self, event):
        if self.rule_tree.identify_column(event.x) == "#1":
            row = self.rule_tree.identify_row(event.y)
            if row:
                self.rule_tree.selection_set(row)
                self.toggle_rule()
                return "break"

    def remove_rule(self):
        index = self.selected_rule()
        if not self.busy and index is not None:
            del self.session.rules[index]
            self.changed()

    def move_rule(self, delta):
        index = self.selected_rule()
        if self.busy or index is None or not 0 <= index + delta < len(self.session.rules):
            return
        other = index + delta
        self.session.rules[index], self.session.rules[other] = self.session.rules[other], self.session.rules[index]
        self.changed(other)

    def choose_output(self):
        path = filedialog.askdirectory(parent=self, title="选择另存为位置")
        if path:
            self.variables["output"].set(path)

    def save_mode(self):
        self.plan = None
        self.generate_button.configure(text="确认覆盖并生成" if self.overwrite.get() else "另存为新更新包")

    def prepare(self):
        if self.busy or not self.session:
            return
        options, overwrite, revision = self.options(), self.overwrite.get(), self.revision
        def prepared(plan):
            self.plan = plan
            self.preview_revision = revision
            text = "输出位置：" + str(plan.destination) + "\n"
            if plan.backup:
                text += "覆盖原目录：" + str(plan.session.root) + "\n成功后保留备份：" + str(plan.backup) + "\n"
            text += "组件载荷位置：" + plan.component_prefix + "\n" if any((options.download_spice, options.build_launcher, options.asphyxia_enabled)) else ""
            text += "\n静态检查不是安装验证；实际匹配由 MediaUpdater 预演。\n\n"
            text += json.dumps(plan.manifest, ensure_ascii=False, indent=2)
            put_text(self.preview_text, text, True)
            self.tabs.select(self.preview_page)
            self.status.set("预览已准备完成，请核对操作顺序与保存位置。")
        self.work(lambda progress, cancel: prepare_import(self.session, options, overwrite, progress, cancel), prepared)

    def generate(self):
        if self.busy:
            return
        if (self.plan is None or self.preview_revision != self.revision or self.options() != self.plan.components
                or self.overwrite.get() != (self.plan.backup is not None)):
            messagebox.showinfo("需要预览", "请先检查并预览当前修改。", parent=self)
            return
        plan = self.plan
        def complete(path):
            self.last_output = path
            self.plan = None
            self.status.set("已生成：" + str(path) + ("；覆盖后请重新导入再继续编辑。" if plan.backup else ""))
            messagebox.showinfo("生成完成", self.status.get(), parent=self)
        self.work(lambda progress, cancel: save_import(plan, progress, cancel), complete)

    def open_output(self):
        if self.last_output and self.last_output.is_dir():
            os.startfile(self.last_output)

    def close(self):
        if self.busy:
            self.closing = True
            self.stop.set()
            self.status.set("正在取消；目录切换阶段会先完成发布或恢复。")
        else:
            self.destroy()

    def destroy(self):
        if hasattr(self, "poll_id"):
            self.after_cancel(self.poll_id)
        super().destroy()
