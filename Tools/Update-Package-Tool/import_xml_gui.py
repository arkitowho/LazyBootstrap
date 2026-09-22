"""带步骤回放的 XML 编辑界面，节点表单沿用新建规则编辑器。"""

from copy import deepcopy
import json
from pathlib import Path
import tkinter as tk
from tkinter import filedialog, messagebox, ttk
from tkinter.scrolledtext import ScrolledText

from manifest import ACTIONS, Rule
from xml_editor import address, at_address
from xml_editor_gui import XmlEditorDialog, put_text
from xml_replay import ReplayEditor


class StepValueDialog(tk.Toplevel):
    def __init__(self, parent, edit):
        super().__init__(parent)
        self.title("修改步骤内容（保留原定位）")
        self.transient(parent)
        self.result = None
        self.edit = deepcopy(edit)
        frame = ttk.Frame(self, padding=16)
        frame.pack(fill="both", expand=True)
        ttk.Label(frame, text="动作：" + next(k for k, v in ACTIONS.items() if v == edit["action"])).pack(anchor="w")
        self.name = tk.StringVar(value=edit.get("name", ""))
        if edit["action"] == "addAttribute":
            ttk.Label(frame, text="属性名").pack(anchor="w")
            ttk.Entry(frame, textvariable=self.name).pack(fill="x")
        self.key = "xml" if "xml" in edit else "value" if "value" in edit else None
        if self.key:
            ttk.Label(frame, text="XML 片段" if self.key == "xml" else "新值（允许为空）").pack(anchor="w")
            self.text = ScrolledText(frame, width=75, height=15, wrap="word")
            self.text.pack(fill="both", expand=True)
            self.text.insert("1.0", edit[self.key])
        else:
            ttk.Label(frame, text="移除操作没有值；使用“重新绑定节点”更改目标。 ").pack()
        ttk.Button(frame, text="应用并回放", command=self.save).pack(side="right", pady=8)
        ttk.Button(frame, text="取消", command=self.destroy).pack(side="right", padx=8)
        self.grab_set()

    def save(self):
        if self.key:
            self.edit[self.key] = self.text.get("1.0", "end-1c")
        if self.edit["action"] == "addAttribute":
            self.edit["name"] = self.name.get().strip()
        self.result = self.edit
        self.destroy()


class ImportedXmlDialog(XmlEditorDialog):
    def __init__(self, parent, rule, reference=None, reference_path=""):
        self.group = None
        self.current = 0
        self.saved_rule = deepcopy(rule)
        super().__init__(parent)
        self.title("XML 规则步骤编辑器")
        self.geometry("1150x900")
        self.target.set(rule.target)
        self.enabled.set(rule.enabled)
        self.encoding.set(rule.encoding)
        panel = ttk.Frame(self, padding=(12, 0, 12, 8))
        panel.pack(fill="x", before=self.tabs)
        ttk.Label(panel, text="选中步骤显示执行前的树。树中操作会插入新步骤；修改已有步骤使用下方按钮。 ").pack(anchor="w")
        self.steps = ttk.Treeview(panel, columns=("action", "detail"), show="headings", height=4, selectmode="browse")
        self.steps.heading("action", text="步骤")
        self.steps.heading("detail", text="内容 / 状态")
        self.steps.column("action", width=160, stretch=False)
        self.steps.column("detail", width=750)
        scrollbar = ttk.Scrollbar(panel, command=self.steps.yview)
        self.steps.configure(yscrollcommand=scrollbar.set)
        scrollbar.pack(side="right", fill="y")
        self.steps.pack(fill="x")
        self.steps.bind("<<TreeviewSelect>>", self.choose_step)
        bar = ttk.Frame(panel)
        bar.pack(fill="x", pady=4)
        for label, command in (("修改值 / 片段", self.edit_step), ("重新绑定到所选节点", self.rebind),
                               ("删除步骤", self.delete_step), ("上移", lambda: self.move_step(-1)),
                               ("下移", lambda: self.move_step(1))):
            ttk.Button(bar, text=label, command=command).pack(side="left", padx=(0, 5))
        self.status.set("请选择该规则组执行前的参考 XML；原规则可取消编辑并原样保留。")
        data = reference if reference is not None else rule.reference_bytes
        if data is not None:
            self.work(lambda: ReplayEditor(rule, data, reference_path or rule.reference_path), self.load_group)

    def pick_file(self):
        if self.busy:
            return
        path = filedialog.askopenfilename(parent=self, title="选择该规则组执行前的参考 XML", filetypes=[("XML 文件", "*.xml"), ("所有文件", "*.*")])
        if path:
            self.load_file(path)

    def load_file(self, path):
        rule = deepcopy(self.saved_rule)
        if self.group:
            rule.edits, rule.namespaces, rule.encoding = deepcopy(self.group.edits), dict(self.group.namespaces), self.group.encoding
        self.work(lambda: ReplayEditor(rule, Path(path).read_bytes(), str(path)), self.load_group)

    def load_group(self, group):
        self.group = group
        self.path.set(group.reference_path or "包内前序操作推导的参考快照")
        self.encoding.set(group.encoding)
        self.current = group.failure[0] if group.failure else 0
        self.refresh_steps()

    def refresh_steps(self):
        self.steps.delete(*self.steps.get_children())
        for index, edit in enumerate(self.group.edits):
            failed = self.group.failure and self.group.failure[0] == index
            label = next((k for k, v in ACTIONS.items() if v == edit["action"]), edit["action"])
            text = ("失败：" + self.group.failure[1] if failed else edit.get("value", edit.get("xml", "")))
            self.steps.insert("", "end", iid=str(index), values=(f"{index + 1}. {label}", text[:250]))
        self.steps.insert("", "end", iid=str(len(self.group.edits)), values=("末尾添加步骤", "显示最终结果，节点操作追加到末尾"))
        self.current = min(self.current, len(self.group.edits))
        if self.group.failure and self.current > self.group.failure[0]:
            self.current = self.group.failure[0]
        self.steps.selection_set(str(self.current))
        self.steps.see(str(self.current))
        self.show_state()

    def choose_step(self, event=None):
        if self.busy or not self.group or not self.steps.selection():
            return
        index = int(self.steps.selection()[0])
        if index == self.current:
            return
        if self.group.failure and index > self.group.failure[0]:
            self.status.set(f"第 {self.group.failure[0] + 1} 步失败，请先修复该步骤。")
            self.steps.selection_set(str(self.current))
            return
        self.current = index
        self.show_state()

    def show_state(self):
        index = self.current
        def prepare():
            model = self.group.state_before(index)
            text = self.group.final.preview()
            if self.group.failure:
                text = "回放失败，以下仅为停止前的部分结果。\n" + text
            wire = json.dumps({"edits": self.group.edits, "namespaces": self.group.namespaces}, ensure_ascii=False, indent=2)
            return model, text[:200000], wire[:100000]
        def show(result):
            self.model = result[0]
            self.matches = []
            self.tree.delete(*self.tree.get_children())
            self.add_node("", self.model.document.documentElement, ())
            self.reveal(())
            put_text(self.preview, result[1], True)
            put_text(self.wire, result[2], True)
            self.undo_button.configure(state="normal" if self.group.position else "disabled")
            self.redo_button.configure(state="normal" if self.group.position + 1 < len(self.group.history) else "disabled")
            self.status.set((f"第 {self.group.failure[0] + 1} 步失败：{self.group.failure[1]}" if self.group.failure
                             else "所有步骤回放成功。") + f" 当前显示第 {index + 1} 步执行前的树。")
        self.work(prepare, show)

    def edit_step(self):
        if self.busy or not self.group or self.current >= len(self.group.edits):
            return
        dialog = StepValueDialog(self, self.group.edits[self.current])
        self.wait_window(dialog)
        self.grab_set()
        if dialog.result is not None:
            index, edit = self.current, dialog.result
            self.work(lambda: self.group.change(index, edit=edit), lambda result: self.refresh_steps())

    def rebind(self):
        node = self.selected_node()
        if node is None or self.current >= len(self.group.edits):
            return
        index, path = self.current, address(node)
        selected = self.attrs.selection()
        attribute = selected[0] if selected else None
        self.work(lambda: self.group.change(index, node_path=path, attribute=attribute), lambda result: self.refresh_steps())

    def delete_step(self):
        if self.busy or not self.group or self.current >= len(self.group.edits):
            return
        edits = deepcopy(self.group.edits)
        del edits[self.current]
        self.work(lambda: self.group.commit(edits), lambda result: self.refresh_steps())

    def move_step(self, delta):
        if self.busy or not self.group:
            return
        index = self.current
        other = index + delta
        if index >= len(self.group.edits) or not 0 <= other < len(self.group.edits):
            return
        edits = deepcopy(self.group.edits)
        edits[index], edits[other] = edits[other], edits[index]
        self.current = other
        self.work(lambda: self.group.commit(edits), lambda result: self.refresh_steps())

    def change(self, action, **values):
        node = self.selected_node()
        if node is None or not self.group:
            return
        index, path = self.current, address(node)
        def mutate():
            model = self.group.state_before(index)
            model.apply(at_address(model.document, path), action, **values)
            edits = deepcopy(self.group.edits)
            edits.insert(index, model.edits[-1])
            self.group.commit(edits, model.namespaces)
        def complete(result):
            self.current += 1
            self.refresh_steps()
        self.work(mutate, complete)

    def history(self, redo):
        if not self.busy and self.group:
            self.work(self.group.redo if redo else self.group.undo, lambda result: self.refresh_steps())

    def reencode(self):
        if self.busy or not self.group:
            return
        encoding = self.encoding.get()
        def apply():
            # 先验证读取，失败时不更改历史。
            from xml_editor import XmlEditor
            XmlEditor(self.group.reference_bytes, encoding)
            self.group.commit(self.group.edits, encoding=encoding)
        self.work(apply, lambda result: self.refresh_steps())

    def save(self):
        if self.busy or not self.group:
            return
        if self.encoding.get() != self.group.encoding:
            messagebox.showerror("编码尚未应用", "请先应用所选编码。", parent=self)
            return
        target, enabled = self.target.get().strip(), self.enabled.get()
        def finish(rule):
            self.result = rule
            self.destroy()
        self.work(lambda: self.group.to_rule(target, enabled), finish)
