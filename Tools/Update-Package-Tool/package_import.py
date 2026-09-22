"""现有更新包的只读导入、载荷覆盖层和带恢复的原子发布。"""

from copy import deepcopy
from dataclasses import dataclass, field
import hashlib
import json
from pathlib import Path
import re
import shutil
import tempfile
import uuid

from manifest import Options, PREFIX, Rule, checksums, package_name, rule_operation, target_path
from package_builder import absolute, inspect_path, within


def strict_json(data):
    def pairs(items):
        result = {}
        for key, value in items:
            if key in result or value is None:
                raise ValueError("JSON 字段重复或为 null：" + key)
            result[key] = value
        return result
    def invalid(value):
        raise ValueError("JSON 数值无效：" + value)
    try:
        if isinstance(data, bytes):
            data = data.decode("utf-8", errors="strict")
        return json.loads(data, object_pairs_hook=pairs, parse_constant=invalid)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise ValueError("JSON 编码或格式错误：" + str(error)) from error


def parse_operation(operation):
    if not isinstance(operation, dict):
        raise ValueError("清单操作必须为对象。")
    kind = operation.get("type")
    fields = {"type", "target"}
    if kind in {"copy", "mirror"}:
        fields.add("source")
    elif kind == "editXml":
        fields.update(("encoding", "namespaces", "edits"))
    elif kind != "delete":
        raise ValueError("未知更新操作：" + str(kind))
    if set(operation) - fields or not {"type", "target"} <= operation.keys():
        raise ValueError("清单操作包含未知或缺失字段。")
    if any(not isinstance(v, str) for k, v in operation.items() if k not in {"namespaces", "edits"}):
        raise ValueError("操作路径、编码及类型必须为字符串。")
    rule = Rule(kind=kind, enabled=True, target=operation["target"],
                package_source=operation.get("source", ""), encoding=operation.get("encoding", "auto"),
                namespaces=deepcopy(operation.get("namespaces", {})), edits=deepcopy(operation.get("edits")))
    if kind == "editXml":
        if (not isinstance(rule.namespaces, dict) or not isinstance(rule.edits, list)
                or any(not isinstance(edit, dict) or any(not isinstance(v, str) for v in edit.values())
                       for edit in rule.edits)):
            raise ValueError("XML 命名空间或编辑列表格式错误。")
    rule_operation(rule, 1)
    if kind == "editXml":
        from xml_replay import validate_xpath
        for edit in rule.edits:
            validate_xpath(edit["xpath"], rule.namespaces)
    rule.original_operation = deepcopy(operation)
    return rule


def operation_for(rule):
    result = rule_operation(rule, 1)
    # 未编辑的原操作逐字段保留，包括省略的 encoding / namespaces 和路径分隔符。
    if rule.original_operation is not None:
        original = deepcopy(rule.original_operation)
        normalized = deepcopy(original)
        normalized["target"] = target_path(normalized["target"], rule.kind)
        if rule.kind == "editXml":
            normalized.setdefault("encoding", "auto")
            if not normalized.get("namespaces"):
                normalized.pop("namespaces", None)
        if result == normalized:
            return original
    return result


def digest(path, cancel=lambda: None):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            cancel()
            result.update(chunk)
    return result.hexdigest()


@dataclass(frozen=True)
class Entry:
    directory: bool
    source: Path
    stamp: tuple
    sha256: str = ""

    def __deepcopy__(self, memo):
        # 所有成员均不可变；会话复制只需要复制覆盖层字典，不必重建数万个文件记录。
        return self


def scan_entries(root, progress=lambda message: None, cancel=lambda: None):
    inspect_path(root)
    result, seen = {}, set()
    stack = [(root, "")]
    while stack:
        path, relative = stack.pop()
        cancel()
        stamp = checksums.fingerprint(path)
        if relative:
            checksums.validate_relative(relative)
            key = relative.upper()
            if key in seen:
                raise ValueError("包内路径大小写冲突：" + relative)
            seen.add(key)
        directory = path.is_dir()
        if directory:
            stack.extend((child, relative + "/" + child.name if relative else child.name)
                         for child in path.iterdir())
        else:
            progress("正在读取并校验：" + (relative or path.name))
        sha = "" if directory else digest(path, cancel)
        if checksums.fingerprint(path) != stamp:
            raise ValueError("读取期间文件发生变化：" + str(path))
        result[relative] = Entry(directory, path, stamp, sha)
    return result


def checksum_issues(root, entries):
    lookup = {key.upper(): key for key in entries}
    if "CHECKSUMS" not in lookup:
        return ["缺少 checksums。"]
    try:
        document = strict_json((root / lookup["CHECKSUMS"]).read_bytes())
        if (not isinstance(document, dict) or set(document) != {"algorithm", "files"}
                or document["algorithm"] != "SHA256" or not isinstance(document["files"], list)):
            raise ValueError("checksums 的字段或算法无效。")
        expected = {}
        for item in document["files"]:
            if (not isinstance(item, dict) or set(item) != {"path", "sha256"}
                    or not isinstance(item["path"], str) or not isinstance(item["sha256"], str)
                    or not re.fullmatch(r"[a-fA-F0-9]{64}", item["sha256"])):
                raise ValueError("checksums 文件条目无效。")
            key = item["path"].replace("\\", "/")
            checksums.validate_relative(key)
            key = key.upper()
            if key in expected or key == "CHECKSUMS":
                raise ValueError("checksums 含重复或自身引用。")
            expected[key] = item["sha256"].lower()
        actual = {key.upper(): entry.sha256 for key, entry in entries.items()
                  if not entry.directory and key.upper() != "CHECKSUMS"}
        return (["校验条目缺失：" + key for key in actual.keys() - expected.keys()]
                + ["校验引用的文件缺失：" + key for key in expected.keys() - actual.keys()]
                + ["SHA-256 不一致：" + key for key in actual.keys() & expected.keys()
                   if actual[key] != expected[key]])
    except (ValueError, OSError, TypeError) as error:
        return ["校验清单错误：" + str(error)]


def protected(path):
    checksums.validate_relative(path)
    if path.upper() in {"UPDATE", "CHECKSUMS"} or path.split("/")[-1].upper() == "UPDATE":
        raise ValueError("update 和根目录 checksums 由工具管理，不能通过载荷树修改。")


@dataclass
class PackageSession:
    root: Path
    snapshot: dict[str, Entry]
    entries: dict[str, Entry]
    rules: list[Rule]
    integrity_issues: list[str] = field(default_factory=list)
    integrity_accepted: bool = False
    _lookup_entries: dict | None = field(default=None, init=False, repr=False, compare=False)
    _lookup: dict = field(default_factory=dict, init=False, repr=False, compare=False)

    def find(self, path):
        if self._lookup_entries is not self.entries:
            self._lookup = {name.upper(): name for name in self.entries}
            self._lookup_entries = self.entries
        key = path.replace("\\", "/").upper()
        return self._lookup.get(key)

    def references(self, path):
        key = path.upper()
        return [i for i, rule in enumerate(self.rules) if rule.kind in {"copy", "mirror"}
                and (key == (src := rule.package_source.replace("\\", "/").upper())
                     or key.startswith(src + "/") or src.startswith(key + "/"))]

    def remove(self, path):
        protected(path)
        name = self.find(path)
        if name is None:
            raise ValueError("载荷不存在：" + path)
        self.entries = {k: v for k, v in self.entries.items() if k != name and not k.startswith(name + "/")}

    def put(self, source, destination, *, replace=False, progress=lambda message: None, cancel=lambda: None):
        destination = destination.replace("\\", "/")
        protected(destination)
        source = absolute(str(source))
        additions = scan_entries(source, progress, cancel)
        result = dict(self.entries)
        existing = self.find(destination)
        if existing is not None:
            if not replace:
                raise ValueError("载荷已存在，请使用替换：" + destination)
            # 替换沿用包内原大小写，避免更改共享引用。
            destination = existing
            result = {k: v for k, v in result.items() if k != existing and not k.startswith(existing + "/")}
        for relative, entry in additions.items():
            name = destination + ("/" + relative if relative else "")
            protected(name)
            result[name] = entry
        for parent in reversed(Path(destination).parents):
            key = parent.as_posix()
            if key != "." and key not in result:
                result[key] = Entry(True, source, (), "")
        validate_entries(result)
        cancel()
        self.entries = result

    def issues(self):
        errors = []
        for index, rule in enumerate(self.rules, 1):
            if not rule.enabled:
                continue
            try:
                operation_for(rule)
                if rule.kind in {"copy", "mirror"}:
                    name = self.find(rule.package_source)
                    if name is None:
                        raise ValueError("引用的载荷缺失：" + rule.package_source)
                    if rule.kind == "mirror" and not self.entries[name].directory:
                        raise ValueError("镜像源必须是目录。")
            except (ValueError, TypeError) as error:
                errors.append(f"操作 {index}：{error}")
        return errors

    def manifest(self):
        return {"operations": [operation_for(rule) for rule in self.rules if rule.enabled]}

    def check_snapshot(self, progress=lambda message: None, cancel=lambda: None):
        try:
            current = scan_entries(self.root, progress, cancel)
        except (OSError, ValueError) as error:
            raise ValueError("原更新包已被外部修改或无法读取，请重新导入：" + str(error)) from error
        if current != self.snapshot:
            raise ValueError("原更新包已被外部修改，请重新导入。")

    def naming(self):
        suffix = self.root.name.removeprefix(PREFIX)
        match = re.fullmatch(r"(\d{10}) to (\d{10})", suffix)
        return (match.group(1), match.group(2), "") if match else ("", "", suffix)


def validate_entries(entries):
    seen = {}
    for name, entry in entries.items():
        if not name:
            continue
        checksums.validate_relative(name)
        key = name.upper()
        if key in seen:
            raise ValueError("载荷路径大小写冲突：" + name)
        seen[key] = entry
    for key in seen:
        for parent in Path(key).parents:
            if parent.as_posix() in seen and not seen[parent.as_posix()].directory:
                raise ValueError("载荷父路径是文件：" + key)


def import_package(directory, progress=lambda message: None, cancel=lambda: None):
    root = absolute(str(directory))
    if not root.is_dir():
        raise ValueError("请选择已解压的更新包目录。")
    all_entries = scan_entries(root, progress, cancel)
    manifests = [entry.source for name, entry in all_entries.items()
                 if not entry.directory and Path(name).name.lower() == "update"]
    if len(manifests) != 1:
        raise ValueError(f"导入目录必须包含唯一的 update，实际找到 {len(manifests)} 份。")
    root = manifests[0].parent
    entries = {entry.source.relative_to(root).as_posix(): entry for entry in all_entries.values()
               if entry.source != root and entry.source.is_relative_to(root)}
    entries[""] = all_entries["" if root == absolute(str(directory)) else root.relative_to(absolute(str(directory))).as_posix()]
    if any(name.upper() == "CHECKSUMS" and entry.directory for name, entry in entries.items()):
        raise ValueError("包根目录 checksums 必须是文件，不能是目录；请调整后重新导入。")
    document = strict_json(manifests[0].read_bytes())
    if not isinstance(document, dict) or set(document) != {"operations"} or not isinstance(document["operations"], list) or not document["operations"]:
        raise ValueError("update 必须包含非空 operations 列表且没有额外字段。")
    rules = []
    for operation in document["operations"]:
        cancel()
        rules.append(parse_operation(operation))
    session = PackageSession(root, entries, {k: v for k, v in entries.items() if k and k.upper() not in {"UPDATE", "CHECKSUMS"}}, rules,
                             checksum_issues(root, entries))
    session.check_snapshot(progress, cancel)
    return session


@dataclass
class ImportPlan:
    session: PackageSession
    destination: Path
    backup: Path | None
    manifest: dict
    components: Options
    component_prefix: str
    additions: dict[str, Entry]


def prepare_import(session, options, overwrite=False, progress=lambda message: None, cancel=lambda: None):
    session = deepcopy(session)
    if session.integrity_issues and not session.integrity_accepted:
        raise ValueError("请先确认校验问题并明确选择继续编辑。")
    errors = session.issues()
    if errors:
        raise ValueError("\n".join(errors))
    output = session.root.parent if overwrite else absolute(options.output)
    inspect_path(output)
    if not output.is_dir():
        raise ValueError("输出位置必须是已存在的目录。")
    destination = session.root if overwrite else output / package_name(options)
    if not overwrite and destination.exists():
        raise ValueError("输出目录已存在，请更换名称或位置。")
    if not overwrite and (within(destination, session.root) or within(session.root, destination)):
        raise ValueError("输出和原更新包目录不能互相包含。")
    for entry in session.entries.values():
        if not overwrite and within(entry.source, destination):
            raise ValueError("输出目录不能包含输入载荷。")
    session.check_snapshot(progress, cancel)
    prefix = "source/components-" + uuid.uuid4().hex[:12]
    while session.find(prefix) is not None:
        prefix = "source/components-" + uuid.uuid4().hex[:12]
    manifest = session.manifest()
    additions = {}
    if options.build_launcher:
        import launcher_build
        build_dir = launcher_build.REPO_ROOT / "build"
        for reserved in (build_dir, launcher_build.REPO_ROOT / "build_tmp"):
            if within(session.root, reserved) or within(reserved, session.root) or within(destination, reserved):
                raise ValueError("原包或输出与编译目录重叠。")
            if any(within(e.source, reserved) or (e.directory and within(reserved, e.source)) for e in session.entries.values()):
                raise ValueError("输入载荷与编译目录重叠。")
        launcher_build.compile_launcher(progress, cancel)
        for required in ("Launcher.exe", "launcher/LazyBootstrap.exe", "launcher/MediaUpdater.exe"):
            if not (build_dir / required).is_file():
                raise ValueError("编译产物缺失：" + required)
        scanned = scan_entries(build_dir, progress, cancel)
        for relative, entry in scanned.items():
            if relative:
                name = "启动.exe" if relative.lower() == "launcher.exe" else relative
                protected(prefix + "/launcher-build/" + name)
                additions[prefix + "/launcher-build/" + name] = entry
        for child in sorted(build_dir.iterdir(), key=lambda p: (p.name.lower() != "launcher", p.name.lower())):
            name = "启动.exe" if child.name.lower() == "launcher.exe" else child.name
            manifest["operations"].append({"type": "mirror" if name.lower() == "launcher" else "copy",
                                            "source": prefix + "/launcher-build/" + name, "target": target_path(name)})
    if options.download_spice:
        manifest["operations"].append({"type": "copy", "source": prefix + "/spice/spice64.exe", "target": "contents/spice64.exe"})
    if options.asphyxia_enabled:
        manifest["operations"].append({"type": "copy", "source": prefix + "/plugin", "target": "asphyxia/plugins/sdvx@asphyxia"})
    if not manifest["operations"]:
        raise ValueError("至少启用一个操作。")
    planned_entries = {**session.entries, **additions}
    if options.download_spice:
        planned_entries[prefix + "/spice/spice64.exe"] = Entry(False, Path(), ())
    if options.asphyxia_enabled:
        planned_entries[prefix + "/plugin"] = Entry(True, Path(), ())
    validate_entries(planned_entries)
    backup = session.root.with_name(session.root.name + ".backup-" + uuid.uuid4().hex[:12]) if overwrite else None
    cancel()
    return ImportPlan(session, destination, backup, manifest, deepcopy(options), prefix, additions)


def save_import(plan, progress=lambda message: None, cancel=lambda: None, *, downloader=None, plugin_downloader=None):
    session = plan.session
    output = plan.destination.parent
    inspect_path(output)
    session.check_snapshot(progress, cancel)
    workspace = Path(tempfile.mkdtemp(prefix=".update-package-", dir=output))
    package = workspace / "package"
    package.mkdir()
    try:
        for name, entry in sorted({**session.entries, **plan.additions}.items(), key=lambda pair: (pair[0].count("/"), pair[0])):
            cancel()
            destination = package / name
            if entry.stamp and checksums.fingerprint(entry.source) != entry.stamp:
                raise ValueError("载荷发生变化，请重新选择或导入：" + str(entry.source))
            if entry.directory:
                destination.mkdir(parents=True, exist_ok=True)
                continue
            progress("正在复制载荷：" + name)
            destination.parent.mkdir(parents=True, exist_ok=True)
            hashed = hashlib.sha256()
            with entry.source.open("rb") as src, destination.open("xb") as dst:
                while chunk := src.read(1024 * 1024):
                    cancel()
                    hashed.update(chunk)
                    dst.write(chunk)
            if hashed.hexdigest() != entry.sha256 or checksums.fingerprint(entry.source) != entry.stamp:
                raise ValueError("复制期间载荷发生变化：" + name)
        if plan.components.download_spice:
            import spice_download
            (downloader or spice_download.download)(package / plan.component_prefix / "spice/spice64.exe", workspace,
                                                    plan.components.include_prerelease, progress, cancel)
        if plan.components.asphyxia_enabled:
            import asphyxia_download
            (plugin_downloader or asphyxia_download.download)(package / plan.component_prefix / "plugin", workspace, progress, cancel)
        (package / "update").write_text(json.dumps(plan.manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        checksums.generate(str(package), progress=progress, cancel=cancel)
        # 验证实际发布载荷；下载失败、镜像类型错误等不能产生半成品。
        staged = import_package(package, progress, cancel)
        if staged.integrity_issues or staged.issues():
            raise ValueError("生成包校验失败：" + "；".join(staged.integrity_issues + staged.issues()))
        session.check_snapshot(progress, cancel)
        for entry in [*session.entries.values(), *plan.additions.values()]:
            cancel()
            if entry.stamp and checksums.fingerprint(entry.source) != entry.stamp:
                raise ValueError("生成期间输入载荷发生变化，请重新预览。")
        inspect_path(output)
        cancel()
        if plan.backup is None:
            if plan.destination.exists():
                raise ValueError("输出目录已经存在。")
            package.rename(plan.destination)
        else:
            if plan.backup.exists():
                raise ValueError("预定备份位置已存在，请重新预览。")
            # 此阶段不响应取消，确保完成发布或恢复。
            session.root.rename(plan.backup)
            try:
                package.rename(plan.destination)
            except BaseException:
                plan.backup.rename(session.root)
                progress("发布失败，已恢复原更新包。")
                raise
            progress("原更新包备份已保留：" + str(plan.backup))
        progress("更新包已发布：" + str(plan.destination))
        return plan.destination
    finally:
        if workspace.resolve().parent == output.resolve() and workspace.name.startswith(".update-package-"):
            shutil.rmtree(workspace)
