"""更新清单模型与静态校验，不模拟游戏目录或 XPath 执行。"""

from dataclasses import dataclass, field
from copy import deepcopy
from datetime import datetime
from pathlib import Path
import re
import sys
from xml.dom import minidom, Node
from xml.sax.saxutils import escape

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import generate_update_checksums as checksums


PREFIX = "UPDATE_LAZY_KFC_"
ENCODINGS = ("auto", "utf-8", "utf-16le", "utf-16be", "gbk", "shift-jis")
ACTIONS = {
    "修改值": "setValue", "新增属性": "addAttribute", "替换元素": "replaceElement",
    "追加子元素": "appendChild", "前方插入": "insertBefore", "后方插入": "insertAfter",
    "移除节点": "remove",
}
XML_NAMESPACE = "http://www.w3.org/XML/1998/namespace"
XMLNS_NAMESPACE = "http://www.w3.org/2000/xmlns/"


@dataclass
class Rule:
    kind: str = "copy"
    enabled: bool = False
    source: str = ""
    target: str = ""
    action: str = "setValue"
    xpath: str = ""
    value: str = ""
    name: str = ""
    xml: str = ""
    encoding: str = "auto"
    namespaces: dict[str, str] = field(default_factory=dict)
    edits: list[dict] | None = None
    reference_bytes: bytes | None = None
    reference_path: str = ""
    editor_actions: list[dict] = field(default_factory=list)
    package_source: str = ""
    original_operation: dict | None = None
    visual_verified: bool = False


@dataclass
class Options:
    original: str = ""
    output: str = ""
    content_root: str = ""
    old_version: str = ""
    new_version: str = ""
    suffix: str = ""
    download_spice: bool = False
    include_prerelease: bool = False
    edit_version: bool = False
    build_launcher: bool = False
    asphyxia_enabled: bool = False
    rules: list[Rule] = field(default_factory=list)


def validate_version(value: str) -> str:
    if not re.fullmatch(r"[0-9]{10}", value):
        raise ValueError("版本号必须为 10 位 YYYYMMDDNN，例如 2026080500。")
    try:
        datetime.strptime(value[:8], "%Y%m%d")
    except ValueError as error:
        raise ValueError("版本号中的日期无效：" + value) from error
    return value


def package_name(options: Options) -> str:
    for version in (options.old_version, options.new_version):
        if version:
            validate_version(version)
    suffix = options.suffix
    if not suffix:
        suffix = f"{validate_version(options.old_version)} to {validate_version(options.new_version)}"
    if "/" in suffix or "\\" in suffix or not suffix.strip():
        raise ValueError("自定义名称必须为单个目录名。")
    result = PREFIX + suffix
    checksums.validate_relative(result)
    if len(result) > 200:
        raise ValueError("更新包目录名过长（最多 200 字符）。")
    return result


def target_path(value: str, kind: str = "copy") -> str:
    value = value.replace("\\", "/")
    checksums.validate_relative(value)
    parts = value.lower().split("/")
    if (parts[0] in {"tmp", ".media-update", "update_log.txt"}
            or any(part.startswith(".media-update-") for part in parts)
            or value.lower() == "launcher/mediaupdater.exe.pending"
            or value.lower().startswith("launcher/mediaupdater.exe.pending/")):
        raise ValueError("更新目标属于更新器保留路径：" + value)
    if kind in {"delete", "editXml"} and value.lower() == "launcher/mediaupdater.exe":
        raise ValueError("不能删除或编辑 MediaUpdater.exe。")
    return value


def ncname(value: str) -> None:
    if not value or ":" in value or any(c.isspace() for c in value):
        raise ValueError("XML 名称无效：" + value)
    try:
        minidom.parseString(f"<{value}/>")
    except Exception as error:
        raise ValueError("XML 名称无效：" + value) from error


def xml_edit(rule: Rule) -> dict:
    if rule.encoding not in ENCODINGS or not rule.xpath.strip():
        raise ValueError("XML 规则需要 XPath 和有效编码。")
    for prefix, uri in rule.namespaces.items():
        ncname(prefix)
        if (not isinstance(uri, str) or not uri.strip() or prefix == "xmlns"
                or uri == XMLNS_NAMESPACE or (prefix == "xml") != (uri == XML_NAMESPACE)):
            raise ValueError("XML 命名空间映射无效。")
    edit = {"action": rule.action, "xpath": rule.xpath}
    if rule.action in {"setValue", "addAttribute"}:
        try:
            minidom.parseString("<value>" + escape(rule.value) + "</value>")
        except Exception as error:
            raise ValueError("XML 值包含无效字符。") from error
        edit["value"] = rule.value
        if rule.action == "addAttribute":
            parts = rule.name.split(":")
            if len(parts) > 2 or "xmlns" in parts:
                raise ValueError("XML 属性名无效。")
            for part in parts:
                ncname(part)
            if len(parts) == 2 and parts[0] not in {"xml", *rule.namespaces}:
                raise ValueError("属性名前缀没有命名空间映射。")
            edit["name"] = rule.name
    elif rule.action in {"replaceElement", "appendChild", "insertBefore", "insertAfter"}:
        if re.search(r"<\?(?:xml)\b|<!DOCTYPE|<!ENTITY", rule.xml, re.I):
            raise ValueError("XML 片段不能包含声明或 DTD。")
        try:
            doc = minidom.parseString(rule.xml)
            if any(node.nodeType != Node.ELEMENT_NODE for node in doc.childNodes):
                raise ValueError("XML 片段只能包含一个根元素。")
        except Exception as error:
            raise ValueError("XML 片段必须是单个完整元素，不含顶层注释或处理指令。") from error
        edit["xml"] = rule.xml
    elif rule.action != "remove":
        raise ValueError("不支持的 XML 动作。")
    return edit


def rule_operation(rule: Rule, index: int) -> dict:
    if rule.kind not in {"copy", "mirror", "delete", "editXml"}:
        raise ValueError("不支持的附加操作。")
    operation = {"type": rule.kind, "target": target_path(rule.target, rule.kind)}
    if rule.kind in {"copy", "mirror"}:
        if not rule.source and not rule.package_source:
            raise ValueError("复制规则需要选择本地源文件或目录。")
        operation["source"] = rule.package_source or f"source/extras/{index:04d}/payload"
        checksums.validate_relative(operation["source"].replace("\\", "/"))
        if operation["source"].replace("\\", "/").split("/")[0].lower() != "source":
            raise ValueError("复制载荷必须位于 source 内。")
    elif rule.kind == "editXml":
        if rule.edits is None:
            edits = [xml_edit(rule)]
        else:
            if not rule.edits:
                raise ValueError("XML 规则组至少需要一处修改。")
            edits = []
            for edit in rule.edits:
                if not isinstance(edit, dict) or set(edit) - {"action", "xpath", "value", "name", "xml"}:
                    raise ValueError("XML 编辑包含无效字段。")
                item = Rule(kind="editXml", encoding=rule.encoding, namespaces=rule.namespaces,
                            action=edit.get("action", ""), xpath=edit.get("xpath", ""),
                            value=edit.get("value", ""), name=edit.get("name", ""), xml=edit.get("xml", ""))
                checked = xml_edit(item)
                if checked != edit:
                    raise ValueError("XML 编辑包含缺失或不适用的字段。")
                edits.append(deepcopy(checked))
        operation.update(encoding=rule.encoding, edits=edits)
        if rule.namespaces:
            operation["namespaces"] = dict(rule.namespaces)
    return operation


def make_manifest(options: Options, build_items=()) -> dict:
    operations = [{"type": "copy", "source": "source/contents", "target": "contents"}]
    if options.build_launcher:
        for name in build_items:
            operations.append({"type": "mirror" if name.lower() == "launcher" else "copy",
                               "source": "source/" + name, "target": name})
    if options.asphyxia_enabled:
        operations.append({"type": "copy", "source": "source/asphyxia/plugins/sdvx@asphyxia",
                           "target": "asphyxia/plugins/sdvx@asphyxia"})
    if options.edit_version:
        operations.append({"type": "editXml", "target": "contents/prop/ea3-ident.xml",
                           "encoding": "auto", "edits": [{"action": "setValue",
                           "xpath": "/ea3_conf/soft/ext", "value": validate_version(options.new_version)}]})
    operations.extend(rule_operation(rule, index) for index, rule in enumerate(options.rules, 1) if rule.enabled)
    return {"operations": operations}
