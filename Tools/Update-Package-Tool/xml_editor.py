"""XML 可视化编辑模型：DOM 操作记录与 XPath 生成，永不写入参考文件。"""

import codecs
from copy import deepcopy
from pathlib import Path
import re
from xml.dom import minidom, Node
from xml.parsers import expat
from xml.sax.saxutils import escape, quoteattr

from manifest import Rule, XML_NAMESPACE, XMLNS_NAMESPACE, ncname, rule_operation, xml_edit


def encoding_name(value):
    aliases = {"utf-16le": "utf-16-le", "utf-16be": "utf-16-be", "unicodefffe": "utf-16-be",
               "shift-jis": "cp932", "shift_jis": "cp932", "windows-31j": "cp932", "gb2312": "gbk"}
    try:
        name = codecs.lookup(aliases.get(value.lower(), value)).name
    except LookupError as error:
        raise ValueError("不支持此 XML 编码：" + value) from error
    if name not in {"utf-8", "utf-16", "utf-16-le", "utf-16-be", "gbk", "cp932"}:
        raise ValueError("不支持此 XML 编码：" + value)
    return name


def decode_xml(data, requested="auto"):
    if data.startswith((b"\xff\xfe\0\0", b"\0\0\xfe\xff")):
        raise ValueError("不支持 UTF-32 XML。")
    bom = 3 if data.startswith(b"\xef\xbb\xbf") else 2 if data.startswith((b"\xff\xfe", b"\xfe\xff")) else 0
    physical = ("utf-8" if bom == 3 else "utf-16-le" if data.startswith((b"\xff\xfe", b"<\0"))
                else "utf-16-be" if data.startswith((b"\xfe\xff", b"\0<")) else None)
    desired = None if requested == "auto" else encoding_name(requested)
    prefix_codec = physical or (desired if desired in {"utf-16-le", "utf-16-be"} else "ascii")
    prefix_bytes = data[bom:bom + 4096]
    if prefix_codec in {"utf-16-le", "utf-16-be"}:
        prefix_bytes = prefix_bytes[:len(prefix_bytes) // 2 * 2]
    prefix = prefix_bytes.decode(prefix_codec, errors="replace")
    declared = None
    if re.match(r"<\?xml\s", prefix):
        end = prefix.find("?>")
        if end < 0:
            raise ValueError("XML 声明缺失结束标记或过长。")
        match = re.search(r'''\bencoding\s*=\s*(['"])(.*?)\1''', prefix[:end])
        if match:
            declared = encoding_name(match[2])
    chosen = desired or physical or declared or "utf-8"
    if chosen == "utf-16":
        chosen = "utf-16-le"
    if ((physical and physical != chosen)
            or (declared and declared != chosen and not (declared == "utf-16" and chosen.startswith("utf-16-")))):
        raise ValueError("XML 编码声明、BOM／字节序与指定编码不一致。")
    try:
        return data[bom:].decode(chosen, errors="strict"), chosen
    except UnicodeError as error:
        raise ValueError("无法按指定编码读取 XML，请检查文件编码。") from error


def parse_xml(text):
    # 先由 Expat 拒绝 DTD，避免 DOM 解析前展开实体或解析外部引用。
    parser = expat.ParserCreate()
    def reject(*args):
        raise ValueError("XML 不允许包含 DTD 或外部实体。")
    parser.StartDoctypeDeclHandler = reject
    parser.ExternalEntityRefHandler = reject
    try:
        # 输入已经严格解码，统一声明供 Python XML 解析器读取。
        text = re.sub(r'''^(<\?xml\s[^?]*?\bencoding\s*=\s*)(['"])(.*?)\2''',
                      lambda m: m[1] + '"utf-8"', text, count=1)
        parser.Parse(text, True)
        return minidom.parseString(text)
    except expat.ExpatError as error:
        raise ValueError(f"XML 解析失败，第 {error.lineno} 行、第 {error.offset} 列：{error}") from error


def elements(node):
    return [child for child in node.childNodes if child.nodeType == Node.ELEMENT_NODE]


def text_value(node):
    return "".join(child.data for child in node.childNodes if child.nodeType in {Node.TEXT_NODE, Node.CDATA_SECTION_NODE})


def attributes(node):
    return [attr for attr in node.attributes.values() if attr.namespaceURI != XMLNS_NAMESPACE
            and attr.name != "xmlns" and attr.prefix != "xmlns"]


def can_format(node):
    current = node
    while current is not None and current.nodeType == Node.ELEMENT_NODE:
        space = current.getAttributeNS(XML_NAMESPACE, "space")
        if space == "preserve":
            return False
        if space == "default":
            break
        current = current.parentNode
    return not any(c.nodeType in {Node.TEXT_NODE, Node.CDATA_SECTION_NODE} and c.data.strip() for c in node.childNodes)


def comparison_children(node, ignore_indent):
    result, text = [], []
    for child in node.childNodes:
        if child.nodeType in {Node.TEXT_NODE, Node.CDATA_SECTION_NODE}:
            if not (ignore_indent and child.nodeType == Node.TEXT_NODE and not child.data.strip()):
                text.append(child.data)
        else:
            if text and "".join(text):
                result.append("".join(text))
            text = []
            result.append(child)
    if text and "".join(text):
        result.append("".join(text))
    return result


def equivalent(left, right):
    """与更新器一致：比较完整内容，忽略属性顺序、前缀写法和元素间排版缩进。"""
    pending = [(left, right)]
    while pending:
        a, b = pending.pop()
        if isinstance(a, str) or isinstance(b, str):
            if a != b:
                return False
        elif a.nodeType == Node.ELEMENT_NODE and b.nodeType == Node.ELEMENT_NODE:
            if (a.namespaceURI, a.localName) != (b.namespaceURI, b.localName):
                return False
            def attrs(node):
                return {(attr.namespaceURI, attr.localName): attr.value for attr in attributes(node)}
            if attrs(a) != attrs(b):
                return False
            ignore_indent = can_format(a) and can_format(b) and bool(elements(a)) and bool(elements(b))
            ac, bc = comparison_children(a, ignore_indent), comparison_children(b, ignore_indent)
            if len(ac) != len(bc):
                return False
            pending.extend(zip(ac, bc))
        elif (a.nodeType, a.nodeName, a.nodeValue) != (b.nodeType, b.nodeName, b.nodeValue):
            return False
    return True


def already_present(target, inserted, action):
    if action == "replaceElement":
        return equivalent(target, inserted)
    if action == "appendChild":
        candidates = elements(target)
    else:
        siblings = elements(target.parentNode)
        index = siblings.index(target)
        candidates = siblings[:index] if action == "insertBefore" else siblings[index + 1:]
    return any(equivalent(candidate, inserted) for candidate in candidates)


def serialize(node):
    """跨 Python 版本保留属性换行；文本换行沿用更新器的原样输出行为。"""
    parts, stack = [], [node]
    while stack:
        item = stack.pop()
        if isinstance(item, str):
            parts.append(item)
        elif item.nodeType == Node.DOCUMENT_NODE:
            stack.extend(reversed(item.childNodes))
        elif item.nodeType == Node.ELEMENT_NODE:
            attrs = "".join(" " + a.name + "=" + quoteattr(a.value) for a in item.attributes.values())
            if item.childNodes:
                parts.append("<" + item.tagName + attrs + ">")
                stack.append("</" + item.tagName + ">")
                stack.extend(reversed(item.childNodes))
            else:
                parts.append("<" + item.tagName + attrs + "/>")
        elif item.nodeType == Node.TEXT_NODE:
            parts.append(escape(item.data))
        else:
            # 参考文档中的 CDATA、注释和处理指令不作为编辑目标。
            parts.append(item.toxml())
    return "".join(parts)


def xpath_literal(value):
    if "'" not in value:
        return "'" + value + "'"
    if '"' not in value:
        return '"' + value + '"'
    return "concat(" + ', "\'", '.join("'" + part + "'" for part in value.split("'")) + ")"


def address(node):
    parts = []
    while node.parentNode and node.parentNode.nodeType == Node.ELEMENT_NODE:
        parts.append(elements(node.parentNode).index(node))
        node = node.parentNode
    return tuple(reversed(parts))


def at_address(document, path):
    node = document.documentElement
    for index in path:
        node = elements(node)[index]
    return node


class XmlEditor:
    def __init__(self, data: bytes, requested="auto", reference_path=""):
        self.reference_bytes = data
        self.reference_path = reference_path
        self.encoding = requested
        self.original, self.codec = decode_xml(data, requested)
        self.document = parse_xml(self.original)
        self.actions = []
        self.position = 0
        self.edits = []
        self.namespaces = {}

    @classmethod
    def from_rule(cls, rule):
        if rule.reference_bytes is None:
            raise ValueError("此规则没有参考快照，请使用旧版高级编辑或重新选择 XML 文件。")
        model = cls(rule.reference_bytes, rule.encoding, rule.reference_path)
        model.actions = deepcopy(rule.editor_actions)
        model.position = len(model.actions)
        model._replay()
        if model.edits != rule.edits or model.namespaces != rule.namespaces:
            raise ValueError("规则与参考快照不一致，不能恢复可视化编辑。")
        return model

    def qname(self, node):
        uri = node.namespaceURI
        local = node.localName or node.nodeName
        if not uri:
            return local
        if uri == XML_NAMESPACE:
            return "xml:" + local
        for prefix, known in self.namespaces.items():
            if known == uri:
                return prefix + ":" + local
        prefix = f"ns{len(self.namespaces) + 1}"
        while prefix in self.namespaces:
            prefix += "_"
        self.namespaces[prefix] = uri
        return prefix + ":" + local

    def locator(self, node, attribute=None):
        chain = []
        current = node
        positional = False
        while current.nodeType == Node.ELEMENT_NODE:
            step = self.qname(current)
            siblings = [other for other in elements(current.parentNode)
                        if (other.namespaceURI, other.localName) == (current.namespaceURI, current.localName)]
            identity = None
            for key in ("id", "name", "key"):
                if current.hasAttribute(key):
                    value = current.getAttribute(key)
                    if sum(other.hasAttribute(key) and other.getAttribute(key) == value for other in siblings) == 1:
                        identity = f"[@{key}={xpath_literal(value)}]"
                        break
            if identity:
                step += identity
            elif len(siblings) > 1:
                step += f"[{siblings.index(current) + 1}]"
                positional = True
            chain.append(step)
            current = current.parentNode
        path = "/" + "/".join(reversed(chain))
        if attribute is not None:
            attr = node.getAttributeNode(attribute)
            if attr is None or attr not in attributes(node):
                raise ValueError("请选择可编辑的属性，不能修改命名空间声明。")
            path += "/@" + self.qname(attr)
        return path, positional

    def search(self, query):
        query = query.casefold().strip()
        if not query:
            return []
        matches = []
        stack = [(self.document.documentElement, ())]
        while stack:
            node, path = stack.pop()
            label = " ".join([node.tagName, text_value(node), *(a.name + "=" + a.value for a in attributes(node))])
            if query in label.casefold():
                matches.append(path)
            children = elements(node)
            stack.extend((child, path + (i,)) for i, child in reversed(list(enumerate(children))))
        return matches

    def fragment(self, name, value="", attrs=(), namespace="", bindings=None):
        """表单生成独立可解析的单元素片段，默认命名空间由界面继承。"""
        bindings = dict(bindings or {})
        bindings["xml"] = XML_NAMESPACE
        parts = name.split(":")
        if len(parts) > 2:
            raise ValueError("元素名无效。")
        for part in parts:
            ncname(part)
        if parts[0] == "xmlns" or namespace == XMLNS_NAMESPACE:
            raise ValueError("不能创建命名空间声明元素。")
        uri = namespace or (bindings.get(parts[0], "") if len(parts) == 2 else "")
        if len(parts) == 2 and not uri:
            raise ValueError("元素名前缀没有命名空间，请填写命名空间 URI。")
        document = minidom.Document()
        node = document.createElementNS(uri or None, name)
        document.appendChild(node)
        if uri:
            prefix = parts[0] if len(parts) == 2 else ""
            node.setAttribute("xmlns:" + prefix if prefix else "xmlns", uri)
            if prefix:
                bindings[prefix] = uri
        used = set()
        for attr_name, attr_value in attrs:
            attr_uri, attr_local = self.attribute_name(attr_name, bindings)
            if (attr_uri, attr_local) in used:
                raise ValueError("存在重复属性：" + attr_name)
            used.add((attr_uri, attr_local))
            node.setAttributeNS(attr_uri or None, attr_name, attr_value)
            if attr_uri and attr_uri != XML_NAMESPACE:
                node.setAttribute("xmlns:" + attr_name.split(":")[0], attr_uri)
        node.appendChild(document.createTextNode(value))
        fragment = serialize(node)
        self.validate_fragment(fragment)
        return fragment

    @staticmethod
    def bindings(node):
        bindings = {"xml": XML_NAMESPACE}
        chain = []
        while node.nodeType == Node.ELEMENT_NODE:
            chain.append(node)
            node = node.parentNode
        for element in reversed(chain):
            for attr in element.attributes.values():
                if attr.prefix == "xmlns":
                    bindings[attr.localName] = attr.value
                elif attr.name == "xmlns":
                    bindings[""] = attr.value
        return bindings

    @staticmethod
    def attribute_name(name, bindings):
        parts = name.split(":")
        if len(parts) > 2 or parts[0] == "xmlns":
            raise ValueError("属性名无效，不能直接编辑命名空间声明。")
        for part in parts:
            ncname(part)
        uri = bindings.get(parts[0]) if len(parts) == 2 else ""
        if uri is None or uri == XMLNS_NAMESPACE:
            raise ValueError("属性名前缀没有命名空间映射。")
        return uri, parts[-1]

    def validate_fragment(self, fragment):
        document = parse_xml(fragment)
        xml_edit(Rule(action="appendChild", xpath="/probe", xml=fragment))
        fragment.encode(self.codec, errors="strict")
        return document.documentElement

    def apply(self, node, action, *, attribute=None, value="", name="", xml=""):
        if node.ownerDocument is not self.document or at_address(self.document, address(node)) is not node:
            raise ValueError("所选节点已失效，请重新选择。")
        command = {"path": address(node), "action": action, "attribute": attribute,
                   "value": value, "name": name, "xml": xml}
        # 校验失败不记录历史；已完成操作始终保持可重放。
        namespaces = dict(self.namespaces)
        try:
            edit = self._execute(command)
        except Exception:
            self.namespaces = namespaces
            raise
        self.actions = self.actions[:self.position] + [command]
        self.position += 1
        self.edits.append(edit)

    def _execute(self, command):
        node = at_address(self.document, command["path"])
        action, attribute = command["action"], command["attribute"]
        xpath, _ = self.locator(node, attribute)
        edit = {"action": action, "xpath": xpath}
        value = command["value"]
        if attribute is not None and action not in {"setValue", "remove"}:
            raise ValueError("属性仅支持修改值或删除。")
        if action == "setValue":
            if attribute is None and elements(node):
                raise ValueError("含有子元素的节点不能直接修改文本，请选择子节点。")
            xml_edit(Rule(xpath=xpath, value=value, namespaces=self.namespaces))
            value.encode(self.codec, errors="strict")
            edit["value"] = value
            if (node.getAttribute(attribute) if attribute is not None else text_value(node)) == value:
                return edit
            if attribute is not None:
                node.getAttributeNode(attribute).value = value
            else:
                texts = [c for c in node.childNodes if c.nodeType in {Node.TEXT_NODE, Node.CDATA_SECTION_NODE}]
                new = self.document.createTextNode(value)
                if texts:
                    node.replaceChild(new, texts[0])
                    for child in texts[1:]:
                        node.removeChild(child)
                else:
                    node.insertBefore(new, node.firstChild)
            edit["value"] = value
        elif action == "addAttribute":
            name = command["name"]
            uri, local = self.attribute_name(name, self.bindings(node))
            attr = self.document.createAttributeNS(uri or None, name)
            wire_name = self.qname(attr)
            xml_edit(Rule(action=action, xpath=xpath, name=wire_name, value=value, namespaces=self.namespaces))
            value.encode(self.codec, errors="strict")
            edit.update(name=wire_name, value=value)
            if node.hasAttributeNS(uri or None, local):
                if node.getAttributeNS(uri or None, local) == value:
                    return edit
                raise ValueError("同名属性已存在且值不同，请选择该属性并修改值。")
            attr.value = value
            node.setAttributeNodeNS(attr)
            edit.update(name=wire_name, value=value)
        elif action == "remove":
            if attribute is not None:
                node.removeAttribute(attribute)
            else:
                if node.parentNode.nodeType != Node.ELEMENT_NODE:
                    raise ValueError("不能删除 XML 根元素。")
                node.parentNode.removeChild(node)
        elif action in {"appendChild", "insertBefore", "insertAfter", "replaceElement"}:
            if action in {"insertBefore", "insertAfter"} and node.parentNode.nodeType != Node.ELEMENT_NODE:
                raise ValueError("不能在根元素前后插入另一个根元素。")
            inserted = self.document.importNode(self.validate_fragment(command["xml"]), True)
            edit["xml"] = command["xml"]
            if already_present(node, inserted, action):
                return edit
            # minidom 不会像 XmlWriter 一样自动补上默认命名空间重置。
            # 无命名空间片段放入默认命名空间中仍必须保持无命名空间。
            if not inserted.hasAttribute("xmlns"):
                inserted.setAttributeNS(XMLNS_NAMESPACE, "xmlns", "")
            if action == "appendChild":
                node.appendChild(inserted)
            elif action == "replaceElement":
                node.parentNode.replaceChild(inserted, node)
            else:
                node.parentNode.insertBefore(inserted, node if action == "insertBefore" else node.nextSibling)
            edit["xml"] = command["xml"]
        else:
            raise ValueError("不支持的 XML 操作。")
        return edit

    def _replay(self):
        self.document = parse_xml(self.original)
        self.namespaces = {}
        self.edits = [self._execute(command) for command in self.actions[:self.position]]

    def undo(self):
        if self.position:
            self.position -= 1
            self._replay()

    def redo(self):
        if self.position < len(self.actions):
            self.position += 1
            self._replay()

    def to_rule(self, target, enabled=False):
        # 重新播放以移除仅因浏览节点而注册的命名空间。
        self._replay()
        rule = Rule(kind="editXml", target=target, enabled=enabled, encoding=self.encoding,
                    edits=deepcopy(self.edits), namespaces=dict(self.namespaces),
                    reference_bytes=self.reference_bytes, reference_path=self.reference_path,
                    editor_actions=deepcopy(self.actions[:self.position]))
        rule_operation(rule, 1)
        return rule

    def preview(self):
        return serialize(self.document)


def infer_target(reference, content_root):
    if not content_root:
        return ""
    try:
        relative = Path(reference).resolve().relative_to(Path(content_root).resolve())
        return "contents/" + relative.as_posix()
    except ValueError:
        return ""
