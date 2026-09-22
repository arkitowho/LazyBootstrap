"""目录预检、载荷复制和原子发布；无需启动图形界面即可测试。"""

from copy import deepcopy
from dataclasses import dataclass
import json
import os
from pathlib import Path
import shutil
import stat
import tempfile

from manifest import Options, checksums, make_manifest, package_name, target_path
import spice_download
import launcher_build
import asphyxia_download


class Cancelled(Exception):
    pass


@dataclass
class Payload:
    source: Path
    destination: str
    target: str
    kind: str
    entries: list
    exclude_ident: bool = False


@dataclass
class BuildPlan:
    options: Options
    destination: Path
    manifest: dict
    payloads: list[Payload]
    mappings: list[str]


def absolute(value: str) -> Path:
    if not value.strip():
        raise ValueError("必填路径不能为空。")
    return Path(os.path.abspath(value))


def inspect_path(path: Path) -> None:
    for ancestor in [*reversed(path.parents), path]:
        checksums.reject_link(ancestor)


def within(path: Path, root: Path) -> bool:
    return os.path.normcase(str(path)) == os.path.normcase(str(root)) or path.is_relative_to(root)


def content_candidates(original: str, cancel=lambda: None) -> list[Path]:
    root = absolute(original)
    inspect_path(root)
    if not root.is_dir():
        raise ValueError("原始更新包必须为已解压目录。")
    candidates = []

    def visit(folder, depth):
        cancel()
        checksums.reject_link(folder)
        children = list(folder.iterdir())
        marker = any(p.name.lower() in {"data", "modules", "prop"} and p.is_dir() for p in children)
        if marker or folder.name.lower() == "contents":
            candidates.append(folder)
        # 仅搜索外套目录，不进入游戏资源树。
        if depth < 3:
            for child in children:
                if child.is_dir() and (not marker or child.name.lower() == "contents"):
                    visit(child, depth + 1)

    visit(root, 0)
    return candidates


def inventory(source: Path, cancel=lambda: None) -> list:
    inspect_path(source)
    result = []
    seen = set()

    def visit(path, relative):
        cancel()
        info = checksums.reject_link(path)
        if relative:
            checksums.validate_relative(relative)
            key = relative.upper()
            if key in seen:
                raise ValueError("包内路径大小写冲突：" + relative)
            seen.add(key)
        directory = stat.S_ISDIR(info.st_mode)
        if not directory and path.name.lower() == "update":
            raise ValueError("载荷含有额外 update 清单，请选择原始游戏增量目录：" + str(path))
        result.append((relative, directory, checksums.fingerprint(path)))
        if directory:
            for child in sorted(path.iterdir(), key=lambda item: item.name):
                visit(child, (relative + "/" if relative else "") + child.name)

    visit(source, "")
    return result


def included(payload, relative):
    return not (payload.exclude_ident and relative.lower() == "prop/ea3-ident.xml")


def prepare(options: Options, progress=lambda message: None, cancel=lambda: None, *, compile_launcher=True) -> BuildPlan:
    options = deepcopy(options)
    cancel()
    original = absolute(options.original)
    inspect_path(original)
    if not original.is_dir():
        raise ValueError("原始更新包必须为已解压目录。")
    output = absolute(options.output)
    inspect_path(output)
    if not output.is_dir():
        raise ValueError("请选择已经存在的输出位置。")
    destination = output / package_name(options)
    if destination.exists():
        raise ValueError("输出目录已经存在，请更换名称或位置：" + str(destination))
    if options.content_root:
        content = absolute(options.content_root)
        if not within(content, original):
            raise ValueError("内容根目录必须位于原始更新包目录内。")
    else:
        candidates = content_candidates(str(original), cancel)
        if len(candidates) != 1:
            raise ValueError("无法唯一识别内容根目录，请选择包含游戏增量文件的目录。")
        content = candidates[0]
    if not content.is_dir():
        raise ValueError("内容根目录必须为目录。")
    make_manifest(options)  # 在执行编译前检查版本和用户规则。
    sources = [(content, "source/contents", "contents", "copy", options.edit_version)]
    for index, rule in enumerate(options.rules, 1):
        if rule.enabled and rule.kind == "copy":
            sources.append((absolute(rule.source), f"source/extras/{index:04d}/payload",
                            target_path(rule.target), "copy", False))
    for source in [original, *(item[0] for item in sources)]:
        if within(output, source) or within(source, destination) or within(destination, source):
            raise ValueError("输出目录与输入不能互相包含：" + str(source))
    build_directory = launcher_build.REPO_ROOT / "build"
    if options.build_launcher:
        # build.ps1 会清空这两个目录，预先保护所有用户选择的输入和输出。
        for generated in (build_directory, launcher_build.REPO_ROOT / "build_tmp"):
            for ancestor in [*reversed(generated.parents), generated]:
                if ancestor.exists() or ancestor.is_symlink():
                    checksums.reject_link(ancestor)
            for selected in [original, output, *(item[0] for item in sources)]:
                if within(selected, generated) or within(generated, selected) and selected != output:
                    raise ValueError("编译会重建 build/build_tmp，所选输入或输出与其重叠：" + str(selected))
    payloads = []
    mappings = []
    for source, dest, target, kind, exclude in sources:
        progress("正在检查载荷：" + str(source))
        entries = inventory(source, cancel)
        for relative, directory, stamp in entries:
            target_path(target + ("/" + relative if relative else ""))
            if exclude and relative.lower() == "prop/ea3-ident.xml" and directory:
                raise ValueError("prop/ea3-ident.xml 必须是文件，不能是目录。")
        payload = Payload(source, dest, target, kind, entries, exclude)
        payloads.append(payload)
        count = sum(not directory and included(payload, relative) for relative, directory, _ in entries)
        mappings.append(f"{source} → {dest}（{count} 个文件）")
    if not any(not directory for _, directory, _ in payloads[0].entries):
        raise ValueError("原始增量目录没有文件。")
    if options.edit_version:
        mappings.append("排除原始 prop/ea3-ident.xml；安装时仅修改 /ea3_conf/soft/ext。")
    if options.download_spice:
        mappings.append("官方 spice2x → source/contents/spice64.exe（覆盖原包同名文件）")
    if options.asphyxia_enabled:
        mappings.append("最新 kfc 插件 ZIP → source/asphyxia/plugins/sdvx@asphyxia（生成时下载，移除单一顶层目录）")
    build_items = []
    if options.build_launcher:
        if compile_launcher:
            launcher_build.compile_launcher(progress, cancel)
        inspect_path(build_directory)
        if not (build_directory / "Launcher.exe").is_file() or not (build_directory / "launcher").is_dir():
            raise ValueError("编译产物缺少 build/Launcher.exe 或 build/launcher。")
        for required in ("LazyBootstrap.exe", "MediaUpdater.exe"):
            if not (build_directory / "launcher" / required).is_file():
                raise ValueError("编译产物缺少 build/launcher/" + required)
        inventory(build_directory, cancel)
        seen = set()
        for path in sorted(build_directory.iterdir(), key=lambda item: (item.name.lower() != "launcher", item.name.lower())):
            name = "启动.exe" if path.name.lower() == "launcher.exe" else path.name
            if name.lower() in seen or name.lower() in {"contents", "asphyxia", "extras"}:
                raise ValueError("编译产物与包内载荷路径冲突：" + name)
            seen.add(name.lower())
            entries = inventory(path, cancel)
            for relative, _, _ in entries:
                target_path(name + ("/" + relative if relative else ""))
            kind = "mirror" if name.lower() == "launcher" else "copy"
            payloads.append(Payload(path, "source/" + name, name, kind, entries))
            build_items.append(name)
            mappings.append(f"本次编译：{path} → source/{name}")
    manifest = make_manifest(options, build_items)
    plan = BuildPlan(options, destination, manifest, payloads, mappings)
    validate_known_conflicts(plan)
    cancel()
    return plan


def validate_known_conflicts(plan, downloaded_payloads=()):
    """只检查本包能确定的类型冲突，安装目录和 XPath 仍由更新器预演。"""
    known = {}

    def forget(target):
        for key in list(known):
            if key == target or key.startswith(target + "/"):
                del known[key]

    def add(path, directory):
        key = path.lower()
        if key in known and known[key] != directory:
            raise ValueError("规则中的文件与目录类型冲突：" + path)
        for parent in Path(key).parents:
            name = parent.as_posix()
            if name != ".":
                if known.get(name) is False:
                    raise ValueError("规则目标的父路径是文件：" + path)
                known[name] = True
        known[key] = directory

    for operation in plan.manifest["operations"]:
        kind, target = operation["type"], operation["target"].lower()
        if kind == "delete":
            forget(target)
        elif kind in {"copy", "mirror"}:
            if kind == "mirror":
                forget(target)
            payload = next((p for p in [*plan.payloads, *downloaded_payloads] if p.destination == operation["source"]), None)
            if payload is None:
                if plan.options.asphyxia_enabled and operation["source"] == asphyxia_download.PLUGIN_SOURCE:
                    add(target, True)  # 下载前只知道插件根目录；解压后再次检查全部文件。
                    continue
                raise ValueError("清单缺少对应载荷：" + operation["source"])
            for relative, directory, _ in payload.entries:
                if included(payload, relative):
                    add(target + ("/" + relative if relative else ""), directory)
            if payload.destination == "source/contents" and plan.options.download_spice:
                add("contents/spice64.exe", False)
        elif kind == "editXml" and known.get(target) is True:
            raise ValueError("XML 编辑目标不能是目录：" + target)


def build(plan: BuildPlan, progress=lambda message: None, cancel=lambda: None, *, downloader=None, plugin_downloader=None) -> Path:
    # 重新预检：预览后文件和路径可能发生变化。
    fresh = prepare(plan.options, progress, cancel, compile_launcher=False)
    if fresh != plan:
        raise ValueError("预览后输入内容发生变化，请重新预览。")
    output = plan.destination.parent
    workspace = Path(tempfile.mkdtemp(prefix=".update-package-", dir=output))
    package = workspace / "package"
    package.mkdir()
    try:
        for payload in plan.payloads:
            for relative, directory, stamp in payload.entries:
                cancel()
                if not included(payload, relative):
                    continue
                source = payload.source / relative if relative else payload.source
                destination = package / payload.destination / relative if relative else package / payload.destination
                if checksums.fingerprint(source) != stamp:
                    raise ValueError("复制前输入发生变化：" + str(source))
                if directory:
                    destination.mkdir(parents=True, exist_ok=True)
                    continue
                progress("正在复制：" + destination.relative_to(package).as_posix())
                destination.parent.mkdir(parents=True, exist_ok=True)
                with source.open("rb") as src, destination.open("xb") as dst:
                    while True:
                        cancel()
                        block = src.read(1024 * 1024)
                        if not block:
                            break
                        dst.write(block)
                if checksums.fingerprint(source) != stamp:
                    raise ValueError("复制期间输入发生变化：" + str(source))
        if plan.options.download_spice:
            (downloader or spice_download.download)(package / "source/contents/spice64.exe", workspace,
                                                    plan.options.include_prerelease, progress, cancel)
        if plan.options.asphyxia_enabled:
            plugin = package / asphyxia_download.PLUGIN_SOURCE
            (plugin_downloader or asphyxia_download.download)(plugin, workspace, progress, cancel)
            entries = inventory(plugin, cancel)
            validate_known_conflicts(plan, [Payload(plugin, asphyxia_download.PLUGIN_SOURCE,
                                                   asphyxia_download.PLUGIN_TARGET, "copy", entries)])
        cancel()
        (package / "update").write_text(json.dumps(plan.manifest, ensure_ascii=False, indent=2) + "\n",
                                         encoding="utf-8", newline="\n")
        checksums.generate(str(package), progress=progress, cancel=cancel)
        for payload in plan.payloads:
            if inventory(payload.source, cancel) != payload.entries:
                raise ValueError("生成期间输入目录发生变化，请重新预览。")
        cancel()
        inspect_path(output)
        if plan.destination.exists():
            raise ValueError("输出目录已经存在，请更换名称或位置。")
        # Windows rename 不覆盖既有目录；暂存和最终目录在同一卷。
        package.rename(plan.destination)
        return plan.destination
    finally:
        # 仅删除本次创建且位于输出目录内的暂存树。
        resolved = workspace.resolve()
        if resolved.parent == output.resolve() and workspace.name.startswith(".update-package-"):
            shutil.rmtree(workspace)
