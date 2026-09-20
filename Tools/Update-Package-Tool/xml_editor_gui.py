"""XML 树形规则编辑界面；参考文件和原规则始终只读。"""

from copy import deepcopy
import json
from pathlib import Path
import queue
import threading
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from tkinter.scrolledtext import ScrolledText

from manifest import ENCODINGS
from package_builder import content_candidates
from xml_editor import XmlEditor, address, at_address, attributes, elements, infer_target, serialize, text_value


def put_text(widget, value, readonly=False):
    widget.configure(state="normal")
    widget.delete("1.0", "end")
    widget.insert("1.0", value)
    if readonly:
        widget.configure(state="disabled")


class ElementDialog(tk.Toplevel):
    """单元素表单与复杂片段输入共用同一模型校验。"""

    def __init__(self, parent, model, node, replace=False):
        super().__init__(parent)
        self.title("替换元素" if replace else "添加元素")
        self.transient(parent)
        self.result = None
        self.model = model
        self.bindings = model.bindings(node)
        self.name = tk.StringVar(value=node.tagName if replace else "")
        self.namespace = tk.StringVar(value=node.namespaceURI or "")
        def name_changed(*args):
            prefix, separator, _ = self.name.get().partition(":")
            if separator and prefix in self.bindings:
                self.namespace.set(self.bindings[prefix])
        self.name.trace_add("write", name_changed)
        self.geometry("660x520")
        self.minsize(540, 440)
        self.tabs = ttk.Notebook(self)
        self.tabs.pack(fill="both", expand=True, padx=12, pady=12)
        form = ttk.Frame(self.tabs, padding=10)
        fragment = ttk.Frame(self.tabs, padding=10)
        self.tabs.add(form, text="填写元素")
        self.tabs.add(fragment, text="粘贴 XML 片段")
        form.columnconfigure(1, weight=1)
        form.rowconfigure(2, weight=1)
        form.rowconfigure(3, weight=1)
        for row, label, variable in ((0, "元素名", self.name), (1, "命名空间 URI", self.namespace)):
            ttk.Label(form, text=label).grid(row=row, column=0, sticky="w", pady=4)
            ttk.Entry(form, textvariable=variable).grid(row=row, column=1, sticky="ew")
        ttk.Label(form, text="文本值").grid(row=2, column=0, sticky="nw", pady=8)
        self.value = ScrolledText(form, height=4, width=42, wrap="word")
        self.value.grid(row=2, column=1, sticky="nsew", pady=8)
        ttk.Label(form, text="属性\n每行 名称=值").grid(row=3, column=0, sticky="nw")
        self.attrs = ScrolledText(form, height=5, width=42, wrap="none")
        self.attrs.grid(row=3, column=1, sticky="nsew")
        ttk.Label(form, text="命名空间默认继承当前元素。带前缀名称自动使用已有映射。\n表单创建单元素；嵌套结构或多行属性值请使用 XML 片段。",
                  wraplength=500).grid(row=4, column=0, columnspan=2, sticky="w", pady=8)
        self.xml = ScrolledText(fragment, width=55, height=15, wrap="none")
        self.xml.pack(fill="both", expand=True)
        ttk.Label(fragment, text="一个完整元素，可包含子元素；请包含所需的命名空间声明。",
                  wraplength=540).pack(anchor="w", pady=8)
        if replace:
            put_text(self.value, text_value(node))
            put_text(self.attrs, "\n".join(a.name + "=" + a.value for a in attributes(node)))
            # 独立片段必须带上祖先的命名空间声明。
            clone = node.cloneNode(True)
            for prefix, uri in self.bindings.items():
                if prefix != "xml":
                    clone.setAttribute("xmlns:" + prefix if prefix else "xmlns", uri)
            if node.namespaceURI and not node.prefix:
                clone.setAttribute("xmlns", node.namespaceURI)
            put_text(self.xml, serialize(clone))
            if elements(node) or any("\n" in a.value or "\r" in a.value for a in attributes(node)):
                self.tabs.select(fragment)
        buttons = ttk.Frame(self, padding=12)
        buttons.pack(fill="x")
        ttk.Button(buttons, text="应用修改", command=self.save).pack(side="right")
        ttk.Button(buttons, text="取消", command=self.destroy).pack(side="right", padx=8)
        self.grab_set()

    def save(self):
        try:
            if self.tabs.index(self.tabs.select()) == 1:
                result = self.xml.get("1.0", "end-1c")
                self.model.validate_fragment(result)
            else:
                attrs = []
                for line in self.attrs.get("1.0", "end-1c").splitlines():
                    if not line.strip():
                        continue
                    name, separator, value = line.partition("=")
                    if not separator:
                        raise ValueError("属性每行填写 名称=值。")
                    attrs.append((name.strip(), value))
                result = self.model.fragment(self.name.get().strip(), self.value.get("1.0", "end-1c"),
                                             attrs, self.namespace.get().strip(), self.bindings)
            self.result = result
            self.destroy()
        except (ValueError, UnicodeError) as error:
            messagebox.showerror("元素无效", str(error), parent=self)


class XmlEditorDialog(tk.Toplevel):
    PAGE = 200

    def __init__(self, parent, rule=None, *, content_root="", original=""):
        super().__init__(parent)
        self.title("XML 可视化规则编辑器")
        self.transient(parent)
        self.geometry("1100x800")
        self.minsize(880, 680)
        self.result = None
        self.model = None
        self.busy = False
        self.closed = False
        self.events = queue.Queue()
        self.content_root, self.original = content_root, original
        self.path = tk.StringVar(value=rule.reference_path if rule else "尚未选择参考 XML")
        self.target = tk.StringVar(value=rule.target if rule else "")
        self.enabled = tk.BooleanVar(value=rule.enabled if rule else False)
        self.encoding = tk.StringVar(value=rule.encoding if rule else "auto")
        self.query = tk.StringVar()
        self.attribute_name = tk.StringVar()
        self.status = tk.StringVar(value="选择 XML 后，在树中选择要修改的元素。")
        self.hint = tk.StringVar()
        self.advanced = tk.BooleanVar(value=False)
        self.matches = []
        self.match_index = -1
        top = ttk.Frame(self, padding=12)
        top.pack(fill="x")
        top.columnconfigure(1, weight=1)
        ttk.Button(top, text="选择参考 XML", command=self.pick_file).grid(row=0, column=0, padx=(0, 8))
        ttk.Entry(top, textvariable=self.path, state="readonly").grid(row=0, column=1, sticky="ew")
        ttk.Label(top, text="游戏内目标路径").grid(row=1, column=0, sticky="w", pady=8)
        ttk.Entry(top, textvariable=self.target).grid(row=1, column=1, sticky="ew")
        ttk.Label(top, text="参考文件不会被修改或自动加入载荷。原增量包已有的 XML 仍先复制，再执行规则。",
                  wraplength=1000).grid(row=2, column=0, columnspan=2, sticky="w")
        self.tabs = ttk.Notebook(self)
        self.tabs.pack(fill="both", expand=True, padx=12)
        edit_page = ttk.Frame(self.tabs)
        preview_page = ttk.Frame(self.tabs)
        self.tabs.add(edit_page, text="选择节点并修改")
        self.tabs.add(preview_page, text="结果预览")
        panes = ttk.Panedwindow(edit_page, orient="horizontal")
        panes.pack(fill="both", expand=True)
        left, inspector = ttk.Frame(panes, padding=6), ttk.Frame(panes, padding=6)
        panes.add(left, weight=1)
        panes.add(inspector, weight=2)
        # 高级详情展开或窗口缩小时，右侧表单仍可滚动到所有操作。
        canvas = tk.Canvas(inspector, highlightthickness=0, background=self.cget("background"))
        inspector_scroll = ttk.Scrollbar(inspector, orient="vertical", command=canvas.yview)
        canvas.configure(yscrollcommand=inspector_scroll.set)
        inspector_scroll.pack(side="right", fill="y")
        canvas.pack(fill="both", expand=True)
        right = ttk.Frame(canvas)
        panel = canvas.create_window((0, 0), window=right, anchor="nw")
        right.bind("<Configure>", lambda event: canvas.configure(scrollregion=canvas.bbox("all")))
        canvas.bind("<Configure>", lambda event: canvas.itemconfigure(panel, width=event.width))
        search = ttk.Frame(left)
        search.pack(fill="x", pady=(0, 6))
        entry = ttk.Entry(search, textvariable=self.query, width=18)
        entry.pack(side="left", fill="x", expand=True)
        entry.bind("<Return>", lambda event: self.search())
        ttk.Button(search, text="搜索", command=self.search, width=6).pack(side="left")
        ttk.Button(search, text="下一项", command=self.next_match, width=7).pack(side="left")
        self.tree = ttk.Treeview(left, show="tree", selectmode="browse")
        scroll = ttk.Scrollbar(left, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=scroll.set)
        scroll.pack(side="right", fill="y")
        self.tree.pack(fill="both", expand=True)
        self.tree.bind("<<TreeviewOpen>>", self.expand)
        self.tree.bind("<<TreeviewSelect>>", self.select)
        self.tree.bind("<Double-1>", self.more)
        self.tree.bind("<Return>", self.more)
        ttk.Label(right, textvariable=self.hint, wraplength=560).pack(anchor="w")
        ttk.Label(right, text="元素文本（含子元素时不可直接修改）").pack(anchor="w", pady=(8, 2))
        self.value = ScrolledText(right, height=3, width=45, wrap="word")
        self.value.pack(fill="x")
        self.text_button = ttk.Button(right, text="应用文本修改", command=self.set_text)
        self.text_button.pack(anchor="e", pady=4)
        ttk.Label(right, text="属性：选择后可在下方编辑完整值；名称和值也参与搜索").pack(anchor="w")
        self.attrs = ttk.Treeview(right, columns=("name", "value"), show="headings", height=4, selectmode="browse")
        self.attrs.heading("name", text="属性名")
        self.attrs.heading("value", text="当前值")
        self.attrs.column("name", width=130, stretch=False)
        self.attrs.column("value", width=330)
        self.attrs.pack(fill="both", expand=True, pady=4)
        self.attrs.bind("<<TreeviewSelect>>", self.select_attribute)
        ttk.Label(right, text="属性名（新增属性时填写）").pack(anchor="w")
        ttk.Entry(right, textvariable=self.attribute_name).pack(fill="x")
        ttk.Label(right, text="属性值（允许为空）").pack(anchor="w")
        self.attribute_value = ScrolledText(right, height=3, width=45, wrap="word")
        self.attribute_value.pack(fill="x", pady=4)
        bar = ttk.Frame(right)
        bar.pack(fill="x")
        for title, command in (("修改所选属性", self.set_attribute), ("新增属性", self.add_attribute),
                               ("删除所选属性", self.remove_attribute)):
            ttk.Button(bar, text=title, command=command).pack(side="left", padx=(0, 4))
        bar = ttk.Frame(right)
        bar.pack(fill="x", pady=(12, 4))
        for title, action in (("添加子元素", "appendChild"), ("插入前方", "insertBefore"), ("插入后方", "insertAfter")):
            ttk.Button(bar, text=title, command=lambda a=action: self.insert(a)).pack(side="left", padx=(0, 4))
        bar = ttk.Frame(right)
        bar.pack(fill="x")
        ttk.Button(bar, text="替换元素", command=lambda: self.insert("replaceElement")).pack(side="left", padx=(0, 4))
        ttk.Button(bar, text="删除元素", command=lambda: self.change("remove")).pack(side="left")
        self.preview = ScrolledText(preview_page, wrap="none", state="disabled")
        self.preview.pack(fill="both", expand=True)
        advanced_bar = ttk.Frame(self, padding=(12, 6))
        advanced_bar.pack(fill="x")
        ttk.Checkbutton(advanced_bar, text="高级详情（编码与自动生成的 XPath）", variable=self.advanced,
                        command=self.toggle_advanced).pack(anchor="w")
        self.details = ttk.Frame(advanced_bar)
        ttk.Label(self.details, text="读取编码（选择后应用）").pack(side="left")
        ttk.Combobox(self.details, textvariable=self.encoding, values=ENCODINGS, state="readonly", width=12).pack(side="left", padx=4)
        ttk.Button(self.details, text="应用编码", command=self.reencode).pack(side="left")
        self.wire = ScrolledText(advanced_bar, height=4, wrap="word", state="disabled")
        bottom = ttk.Frame(self, padding=(12, 0, 12, 12))
        bottom.pack(fill="x")
        ttk.Label(bottom, textvariable=self.status, wraplength=1040).pack(anchor="w")
        ttk.Label(bottom, text="预览仅针对参考文件；安装时由 MediaUpdater 检查实际文件、节点匹配和操作顺序。",
                  wraplength=1040).pack(anchor="w", pady=(4, 8))
        ttk.Checkbutton(bottom, text="启用整个规则组", variable=self.enabled).pack(side="left")
        self.undo_button = ttk.Button(bottom, text="撤销", command=lambda: self.history(False))
        self.undo_button.pack(side="left", padx=6)
        self.redo_button = ttk.Button(bottom, text="重做", command=lambda: self.history(True))
        self.redo_button.pack(side="left")
        ttk.Button(bottom, text="保存规则组", command=self.save).pack(side="right")
        ttk.Button(bottom, text="取消", command=self.destroy).pack(side="right", padx=8)
        self.protocol("WM_DELETE_WINDOW", self.destroy)
        self.poll_id = self.after(40, self.poll)
        self.grab_set()
        if rule:
            snapshot = deepcopy(rule)
            self.work(lambda: XmlEditor.from_rule(snapshot), self.loaded)

    def destroy(self):
        self.closed = True
        if hasattr(self, "poll_id"):
            self.after_cancel(self.poll_id)
        super().destroy()

    def work(self, function, callback):
        if self.busy or self.closed:
            return
        self.busy = True
        self.status.set("正在处理 XML，请稍候；可随时取消关闭。")
        def run():
            try:
                result = function()
                self.events.put((callback, result, None))
            except Exception as error:
                self.events.put((callback, None, error))
        threading.Thread(target=run, daemon=True).start()

    def poll(self):
        try:
            callback, result, error = self.events.get_nowait()
            self.busy = False
            if error:
                self.status.set("操作失败；未保存规则。")
                reason = "输入含有当前编码无法表示的字符。" if isinstance(error, UnicodeError) else str(error)
                messagebox.showerror("XML 操作失败", reason, parent=self)
            else:
                callback(result)
        except queue.Empty:
            pass
        if not self.closed:
            self.poll_id = self.after(40, self.poll)

    def pick_file(self):
        if self.busy:
            return
        path = filedialog.askopenfilename(parent=self, title="选择只读参考 XML", filetypes=(("XML 文件", "*.xml"), ("所有文件", "*.*")))
        if path:
            if self.model and self.model.position and not messagebox.askyesno("更换参考文件", "更换文件会放弃本次尚未保存的修改，是否继续？", parent=self):
                return
            self.load_file(path)

    def load_file(self, path):
        encoding = self.encoding.get()
        root, original = self.content_root, self.original
        def read():
            model = XmlEditor(Path(path).read_bytes(), encoding, str(path))
            candidate = root
            if not candidate and original:
                candidates = content_candidates(original)
                if len(candidates) == 1:
                    candidate = candidates[0]
            return model, infer_target(path, candidate)
        def complete(result):
            model, target = result
            self.target.set(target)
            self.loaded(model)
        self.work(read, complete)

    def loaded(self, model):
        self.model = model
        self.path.set(model.reference_path)
        self.encoding.set(model.encoding)
        self.refresh()

    @staticmethod
    def iid(path):
        return "n:" + ".".join(map(str, path))

    @staticmethod
    def decode(iid):
        return tuple(map(int, iid[2:].split("."))) if iid[2:] else ()

    def selected_node(self):
        selected = self.tree.selection()
        if self.busy or not self.model or not selected or not selected[0].startswith("n:"):
            return None
        return at_address(self.model.document, self.decode(selected[0]))

    def add_node(self, parent, node, path):
        iid = self.iid(path)
        label = node.tagName
        identity = next((node.getAttribute(k) for k in ("id", "name", "key") if node.hasAttribute(k)), "")
        if identity:
            label += " · " + identity[:80]
        self.tree.insert(parent, "end", iid=iid, text=label)
        if any(c.nodeType == c.ELEMENT_NODE for c in node.childNodes):
            self.tree.insert(iid, "end", text="展开以加载…")

    def populate(self, iid, offset=0):
        if self.busy or not iid.startswith("n:"):
            return
        path = self.decode(iid)
        self.tree.delete(*self.tree.get_children(iid))
        children = elements(at_address(self.model.document, path))
        if offset:
            self.tree.insert(iid, "end", iid=f"more:{iid}:{max(0, offset - self.PAGE)}", text="双击加载上一页元素…")
        for index in range(offset, min(len(children), offset + self.PAGE)):
            self.add_node(iid, children[index], path + (index,))
        if len(children) > offset + self.PAGE:
            self.tree.insert(iid, "end", iid=f"more:{iid}:{offset + self.PAGE}", text="双击加载更多元素…")

    def expand(self, event=None):
        if self.busy:
            return
        iid = self.tree.focus()
        children = self.tree.get_children(iid)
        if children and not children[0].startswith(("n:", "more:")):
            self.populate(iid)

    def more(self, event=None):
        if self.busy:
            return
        iid = self.tree.focus()
        if iid.startswith("more:"):
            parent, offset = iid[5:].rsplit(":", 1)
            self.tree.delete(iid)
            self.populate(parent, int(offset))

    def reveal(self, path):
        for depth in range(len(path)):
            parent = self.iid(path[:depth])
            child = self.iid(path[:depth + 1])
            if not self.tree.exists(child):
                # 搜索定位只加载目标所在页，避免创建成千上万条界面项。
                self.tree.delete(*self.tree.get_children(parent))
                self.populate(parent, path[depth] // self.PAGE * self.PAGE)
            self.tree.item(parent, open=True)
        iid = self.iid(path)
        self.tree.selection_set(iid)
        self.tree.focus(iid)
        self.tree.see(iid)
        self.select()

    def refresh(self, path=()):
        self.matches = []
        self.tree.delete(*self.tree.get_children())
        self.add_node("", self.model.document.documentElement, ())
        try:
            at_address(self.model.document, path)
        except IndexError:
            path = path[:-1]
        self.reveal(path)
        self.undo_button.configure(state="normal" if self.model.position else "disabled")
        self.redo_button.configure(state="normal" if self.model.position < len(self.model.actions) else "disabled")
        # 序列化较大的预览也在后台执行；界面只显示有界文本。
        model = self.model
        def preview():
            text = model.preview()
            suffix = "\n…预览较长，仅显示前 200,000 字符；保存使用完整编辑列表。" if len(text) > 200000 else ""
            wire = json.dumps({"edits": model.edits, "namespaces": model.namespaces}, ensure_ascii=False, indent=2)
            return text[:200000] + suffix, wire[:100000]
        def show(result):
            put_text(self.preview, result[0], True)
            put_text(self.wire, result[1], True)
            self.select()
            self.status.set(f"已修改 {model.position} 处；识别编码：{model.codec}。保存后仍需勾选规则组。")
        self.work(preview, show)

    def select(self, event=None):
        node = self.selected_node()
        if node is None:
            return
        _, positional = self.model.locator(node)
        self.hint.set(node.tagName + (" · 按同名节点序号定位，安装目标需具有相同结构。" if positional else " · 自动定位当前元素"))
        put_text(self.value, text_value(node), bool(elements(node)))
        self.text_button.configure(state="disabled" if elements(node) else "normal")
        self.attrs.delete(*self.attrs.get_children())
        for attr in attributes(node):
            self.attrs.insert("", "end", iid=attr.name, values=(attr.name, attr.value[:200]))
        self.attribute_name.set("")
        put_text(self.attribute_value, "")

    def select_attribute(self, event=None):
        node = self.selected_node()
        selected = self.attrs.selection()
        if node is not None and selected:
            self.attribute_name.set(selected[0])
            put_text(self.attribute_value, node.getAttribute(selected[0]))

    def change(self, action, **values):
        node = self.selected_node()
        if node is None:
            return
        path = address(node)
        def mutate():
            self.model.apply(node, action, **values)
        self.work(mutate, lambda result: self.refresh(path))

    def set_text(self):
        self.change("setValue", value=self.value.get("1.0", "end-1c"))

    def set_attribute(self):
        selected = self.attrs.selection()
        if selected:
            self.change("setValue", attribute=selected[0], value=self.attribute_value.get("1.0", "end-1c"))

    def add_attribute(self):
        self.change("addAttribute", name=self.attribute_name.get().strip(), value=self.attribute_value.get("1.0", "end-1c"))

    def remove_attribute(self):
        selected = self.attrs.selection()
        if selected:
            self.change("remove", attribute=selected[0])

    def insert(self, action):
        node = self.selected_node()
        if node is None:
            return
        context = node.parentNode if action in {"insertBefore", "insertAfter"} and node.parentNode.nodeType == node.ELEMENT_NODE else node
        dialog = ElementDialog(self, self.model, context, action == "replaceElement")
        self.wait_window(dialog)
        self.grab_set()
        if dialog.result is not None:
            self.change(action, xml=dialog.result)

    def history(self, redo):
        if not self.busy and self.model:
            self.work(self.model.redo if redo else self.model.undo, lambda result: self.refresh())

    def search(self):
        if not self.busy and self.model:
            query = self.query.get()
            def show(matches):
                self.matches, self.match_index = matches, -1
                self.next_match()
                self.status.set(f"搜索到 {len(matches)} 个元素；可按名称、属性和值搜索。")
            self.work(lambda: self.model.search(query), show)

    def next_match(self):
        if not self.busy and self.matches:
            self.match_index = (self.match_index + 1) % len(self.matches)
            self.reveal(self.matches[self.match_index])

    def toggle_advanced(self):
        if self.advanced.get():
            self.details.pack(fill="x", pady=4)
            self.wire.pack(fill="x")
        else:
            self.details.pack_forget()
            self.wire.pack_forget()

    def reencode(self):
        if self.busy or not self.model:
            return
        current, encoding = self.model, self.encoding.get()
        def reload():
            model = XmlEditor(current.reference_bytes, encoding, current.reference_path)
            model.actions, model.position = deepcopy(current.actions), current.position
            model._replay()
            return model
        self.work(reload, self.loaded)

    def save(self):
        if self.busy or not self.model:
            return
        if self.encoding.get() != self.model.encoding:
            messagebox.showerror("编码尚未应用", "请先应用所选编码，或恢复为当前编码后保存。", parent=self)
            return
        target, enabled = self.target.get().strip(), self.enabled.get()
        def finish(rule):
            self.result = rule
            self.destroy()
        self.work(lambda: self.model.to_rule(target, enabled), finish)
