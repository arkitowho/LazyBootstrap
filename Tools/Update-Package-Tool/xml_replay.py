"""通过 XPath 1.0 定位导入步骤，复用可视化编辑器的修改与重复跳过行为。"""

from copy import deepcopy
from io import BytesIO
import re

from manifest import XML_NAMESPACE, XMLNS_NAMESPACE, rule_operation
from xml_editor import XmlEditor, address, at_address, serialize


def etree_module():
    try:
        from lxml import etree
        return etree
    except ImportError as error:
        raise ValueError("导入 XML 规则需要 lxml。请在工具目录执行：python -m pip install -r requirements.txt；程序不会自动联网安装。") from error


def validate_xpath(expression, namespaces):
    etree = etree_module()
    try:
        etree.XPath(expression, namespaces=namespaces, regexp=False)
    except etree.XPathError as error:
        raise ValueError("XPath 无效：" + str(error)) from error
    # lxml 会把未知前缀／函数的检查推迟到求值；更新器在 SetContext 时即拒绝。
    # 同时屏蔽 XSLT 扩展函数，避免回放接受 XPath 1.0 协议之外的表达式。
    bare = re.sub(r"'[^']*'|\"[^\"]*\"", "", expression)
    prefixes = re.findall(r"(?<![\w.:-])([\w.-]+):(?!:)(?:[\w.-]+|\*)", bare)
    if any(prefix not in namespaces and prefix != "xml" for prefix in prefixes):
        raise ValueError("XPath 前缀没有命名空间映射。")
    if "$" in bare:
        raise ValueError("更新器 XPath 不支持变量。")
    allowed = {"last", "position", "count", "local-name", "namespace-uri", "name", "string", "concat",
               "starts-with", "contains", "substring-before", "substring-after", "substring", "string-length",
               "normalize-space", "translate", "boolean", "not", "true", "false", "lang", "number", "sum",
               "floor", "ceiling", "round", "id", "node", "text", "comment", "processing-instruction"}
    if any(name not in allowed for name in re.findall(r"([\w.-]+(?::[\w.-]+)?)\s*\(", bare)):
        raise ValueError("更新器 XPath 仅支持 XPath 1.0 标准函数，不支持扩展函数。")


def locate(model, expression, namespaces):
    """XSLT 的 / 模板以文档节点为上下文，与 XmlDocument.SelectNodes 一致。

    不能直接使用 ElementTree.xpath：它的相对 XPath 从根元素开始，语义不同。
    结果只返回元素序号地址 / 属性名，所有实际修改仍在原 minidom 中完成。
    """
    etree = etree_module()
    validate_xpath(expression, namespaces)
    xsl = "http://www.w3.org/1999/XSL/Transform"
    prefix = "xslt"
    while prefix in namespaces:
        prefix += "_"
    style = etree.Element("{" + xsl + "}stylesheet", nsmap={prefix: xsl, **namespaces}, version="1.0")
    def tag(parent, local, **attrs):
        return etree.SubElement(parent, "{" + xsl + "}" + local, **attrs)
    template = tag(style, "template", match="/")
    result = etree.SubElement(template, "results")
    loop = tag(result, "for-each", select=expression)
    limited = tag(loop, "if", test="position() <= 2")
    hit = etree.SubElement(limited, "hit", name="{name()}", uri="{namespace-uri()}", local="{local-name()}")
    kind = tag(hit, "attribute", name="kind")
    choose = tag(kind, "choose")
    tag(choose, "when", test="self::*").text = "element"
    tag(choose, "when", test="count(. | ../@*) = count(../@*)").text = "attribute"
    tag(choose, "otherwise").text = "unsupported"
    steps = tag(hit, "for-each", select="ancestor-or-self::*")
    tag(steps, "value-of", select="count(preceding-sibling::*)")
    tag(steps, "text").text = "/"
    parser = etree.XMLParser(resolve_entities=False, load_dtd=False, no_network=True, huge_tree=False)
    try:
        transform = etree.XSLT(style, access_control=etree.XSLTAccessControl.DENY_ALL, regexp=False)
        data = serialize(model.document).encode("utf-8")
        tree = etree.parse(BytesIO(data), parser)
        result = transform(tree)
        hits = result.getroot().findall("hit")
    except (etree.Error, TypeError) as error:
        raise ValueError("XPath 回放失败：" + str(error)) from error
    if len(hits) != 1:
        raise ValueError("XPath 必须唯一匹配，当前" + ("没有匹配。" if not hits else "匹配多个节点。"))
    hit = hits[0]
    if hit.get("kind") == "unsupported":
        raise ValueError("仅支持元素或属性目标，不支持文本、注释、处理指令、命名空间或文档节点。")
    path = tuple(int(part) for part in (hit.text or "").split("/") if part)[1:]
    node = at_address(model.document, path)
    attribute = None
    if hit.get("kind") == "attribute":
        attr = node.getAttributeNodeNS(hit.get("uri") or None, hit.get("local"))
        if attr is None or attr.namespaceURI == XMLNS_NAMESPACE:
            raise ValueError("不能修改命名空间声明。")
        attribute = attr.name
    return node, attribute


def execute(model, edit, namespaces):
    node, attribute = locate(model, edit["xpath"], namespaces)
    name = edit.get("name", "")
    # 操作声明的前缀不必已存在于参考文件；引入局部声明使复用模型与 XmlWriter 一致。
    if edit["action"] == "addAttribute" and ":" in name:
        prefix = name.split(":", 1)[0]
        uri = namespaces.get(prefix, XML_NAMESPACE if prefix == "xml" else None)
        if uri is None:
            raise ValueError("属性名前缀没有命名空间映射。")
        if prefix != "xml" and model.bindings(node).get(prefix) != uri:
            if node.prefix == prefix or any(a.prefix == prefix and a.namespaceURI != uri for a in node.attributes.values()):
                # 不能重绑定正在被其他名称使用的前缀，改用新的等价前缀。
                prefix = "attr"
                while prefix in model.bindings(node):
                    prefix += "_"
                name = prefix + ":" + name.split(":", 1)[1]
            node.setAttributeNS(XMLNS_NAMESPACE, "xmlns:" + prefix, uri)
    model._execute({"path": address(node), "action": edit["action"], "attribute": attribute,
                    "value": edit.get("value", ""), "name": name, "xml": edit.get("xml", "")})


class ReplayEditor:
    """编辑完整 wire 步骤列表；历史和参考文件均仅存在于会话中。"""
    def __init__(self, rule, data, reference_path=""):
        self.rule = deepcopy(rule)
        self.reference_bytes = data
        self.reference_path = reference_path or rule.reference_path
        self.encoding = rule.encoding
        self.edits = deepcopy(rule.edits or [])
        self.namespaces = dict(rule.namespaces)
        self.history = [(deepcopy(self.edits), dict(self.namespaces), self.encoding)]
        self.position = 0
        self.failure = None
        self.before = []
        self.final = None
        self.replay()

    def replay(self):
        model = XmlEditor(self.reference_bytes, self.encoding, self.reference_path)
        model.namespaces = dict(self.namespaces)
        self.before, self.failure = [], None
        for index, edit in enumerate(self.edits):
            self.before.append(serialize(model.document))
            try:
                execute(model, edit, self.namespaces)
            except Exception as error:
                from xml_editor import parse_xml
                model.document = parse_xml(self.before[-1])
                self.failure = (index, str(error))
                break
        self.final = model
        return self.failure

    def state_before(self, index):
        if index < 0 or index > len(self.edits):
            raise ValueError("步骤序号无效。")
        if self.failure and index > self.failure[0]:
            raise ValueError(f"第 {self.failure[0] + 1} 步失败，尚不能预览之后的步骤。")
        text = self.before[index] if index < len(self.before) else serialize(self.final.document)
        # 直接恢复 DOM，保留原始编码能力验证。
        from xml_editor import parse_xml
        model = XmlEditor(self.reference_bytes, self.encoding, self.reference_path)
        model.document = parse_xml(text)
        model.namespaces = dict(self.namespaces)
        return model

    def commit(self, edits, namespaces=None, encoding=None):
        self.edits = deepcopy(edits)
        self.namespaces = dict(self.namespaces if namespaces is None else namespaces)
        self.encoding = encoding or self.encoding
        self.history = self.history[:self.position + 1] + [(deepcopy(self.edits), dict(self.namespaces), self.encoding)]
        self.position += 1
        self.replay()

    def undo(self):
        if self.position:
            self.position -= 1
            self.restore()

    def redo(self):
        if self.position + 1 < len(self.history):
            self.position += 1
            self.restore()

    def restore(self):
        self.edits, self.namespaces, self.encoding = deepcopy(self.history[self.position])
        self.replay()

    def change(self, index, *, edit=None, node_path=None, attribute=None, insert=False):
        model = self.state_before(index)
        replacement = deepcopy(edit if edit is not None else self.edits[index])
        if node_path is not None:
            replacement["xpath"] = model.locator(at_address(model.document, node_path), attribute)[0]
        edits = deepcopy(self.edits)
        if insert:
            edits.insert(index, replacement)
        else:
            edits[index] = replacement
        self.commit(edits, model.namespaces)

    def to_rule(self, target, enabled):
        self.replay()
        if self.failure:
            raise ValueError(f"第 {self.failure[0] + 1} 步回放失败：{self.failure[1]}")
        rule = deepcopy(self.rule)
        rule.target, rule.enabled, rule.encoding = target, enabled, self.encoding
        rule.edits, rule.namespaces = deepcopy(self.edits), dict(self.namespaces)
        rule.reference_bytes, rule.reference_path = self.reference_bytes, self.reference_path
        rule.editor_actions, rule.visual_verified = [], True
        rule_operation(rule, 1)
        return rule


def infer_reference(session, index, cancel=lambda: None):
    """顺序模拟对目标 XML 可确定的复制、镜像、删除、编辑，遇到未知即放弃推断。"""
    from package_import import operation_for
    target = session.rules[index].target.replace("\\", "/")
    key = target.upper()
    data, origin = None, ""
    for rule in session.rules[:index]:
        cancel()
        if not rule.enabled:
            continue
        operation = operation_for(rule)
        dest = operation["target"].replace("\\", "/")
        affected = key == dest.upper() or key.startswith(dest.upper() + "/")
        if rule.kind == "delete" and affected:
            data, origin = None, ""
        elif rule.kind in {"copy", "mirror"} and affected:
            source = operation["source"].replace("\\", "/")
            relative = target[len(dest):]
            name = session.find(source + relative)
            if name is not None and not session.entries[name].directory:
                entry = session.entries[name]
                if entry.stamp:
                    from manifest import checksums
                    if checksums.fingerprint(entry.source) != entry.stamp:
                        raise ValueError("参考载荷已改变，请重新导入或替换。")
                data, origin = entry.source.read_bytes(), str(entry.source)
            elif rule.kind == "mirror" or session.find(source) is None:
                data, origin = None, ""
        elif rule.kind == "editXml" and key == dest.upper() and data is not None:
            previous = ReplayEditor(rule, data, origin)
            if previous.failure:
                data, origin = None, ""
            else:
                # 后续 auto 识别仍应沿用真实文件编码，声明与字节保持一致。
                codec = previous.final.codec
                declaration = {"utf-16-le": "utf-16le", "utf-16-be": "utf-16be", "cp932": "shift_jis"}.get(codec, codec)
                text = serialize(previous.final.document)
                data = (f'<?xml version="1.0" encoding="{declaration}"?>' + text).encode(codec)
    return data, origin
