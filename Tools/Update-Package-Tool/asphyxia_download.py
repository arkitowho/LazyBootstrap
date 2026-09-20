"""选择 KFC 发布资产，安全解压插件并移除单一外层目录。"""

import json
from pathlib import Path
import stat
import zipfile

from manifest import checksums, target_path
from release_download import download_asset
from spice_download import open_url


API = "https://api.github.com/repos/22vv0/asphyxia_plugins/releases"
DOWNLOAD_PREFIX = "https://github.com/22vv0/asphyxia_plugins/releases/download/"
PLUGIN_TARGET = "asphyxia/plugins/sdvx@asphyxia"
PLUGIN_SOURCE = "source/" + PLUGIN_TARGET


def select_asset(cancel=lambda: None, opener=open_url):
    candidates = []
    page = 1
    while True:
        cancel()
        with opener(f"{API}?per_page=100&page={page}") as response:
            releases = json.load(response)
            more = 'rel="next"' in response.headers.get("Link", "")
        cancel()
        if not isinstance(releases, list):
            raise ValueError("asphyxia 发布列表无效。")
        for release in releases:
            cancel()
            if release.get("draft") or not release.get("published_at"):
                continue
            for asset in release.get("assets", []):
                name = asset.get("name", "").lower()
                if name.startswith("kfc") and name.endswith(".zip") and asset.get("state", "uploaded") == "uploaded":
                    # 按资产更新时间选择最新文件，发布时间和名称用于稳定排序。
                    date = asset.get("updated_at") or asset.get("created_at") or release["published_at"]
                    candidates.append(((date, release["published_at"], name), release.get("tag_name", ""), asset))
        if not more or not releases:
            break
        page += 1
    if not candidates:
        raise ValueError("指定仓库的发布资产中没有 kfc 开头的 ZIP 插件。")
    _, tag, asset = max(candidates, key=lambda item: item[0])
    return tag, asset


def extraction_entries(archive, cancel):
    entries = []
    seen = {}
    explicit = set()
    for entry in archive.infolist():
        cancel()
        name = entry.orig_filename.replace("\\", "/")
        directory = name.endswith("/")
        relative = name[:-1] if directory else name
        checksums.validate_relative(relative)
        mode = stat.S_IFMT(entry.external_attr >> 16)
        if (mode not in {0, stat.S_IFREG, stat.S_IFDIR}
                or mode == stat.S_IFDIR and not directory
                or mode == stat.S_IFREG and directory
                or entry.external_attr & 0x400):
            raise ValueError("插件压缩包包含链接或非常规文件：" + relative)
        if entry.flag_bits & 1:
            raise ValueError("不支持加密的插件压缩包。")
        parts = relative.split("/")
        # 同时检查隐含目录，避免先写文件 a 再写 a/b，或 A/b 与 a/c。
        for index in range(1, len(parts) + 1):
            path = "/".join(parts[:index])
            key = path.upper()
            is_directory = directory or index < len(parts)
            previous = seen.get(key)
            if previous is not None and previous != (path, is_directory):
                raise ValueError("插件压缩包存在路径大小写或文件类型冲突：" + path)
            seen[key] = (path, is_directory)
        if relative.upper() in explicit:
            raise ValueError("插件压缩包包含重复路径：" + relative)
        explicit.add(relative.upper())
        entries.append((entry, parts, directory))
    if not any(not directory for _, _, directory in entries):
        raise ValueError("插件压缩包没有文件。")
    roots = {parts[0] for _, parts, _ in entries}
    wrapped = len(roots) == 1 and all(directory or len(parts) > 1 for _, parts, directory in entries)
    result = []
    for entry, parts, directory in entries:
        if wrapped:
            parts = parts[1:]
        if not parts:
            continue  # 外层目录自身不写入目标。
        relative = "/".join(parts)
        target_path(PLUGIN_TARGET + "/" + relative)
        if not directory and parts[-1].lower() == "update":
            raise ValueError("插件载荷不能包含额外的 update 清单。")
        result.append((entry, relative, directory))
    return result, next(iter(roots)) if wrapped else None


def download(destination: Path, workspace: Path, progress=lambda message: None,
             cancel=lambda: None, *, opener=open_url) -> str:
    progress("正在查找最新的 asphyxia KFC 插件……")
    tag, asset = select_asset(cancel, opener)
    label = "asphyxia " + asset["name"]
    progress(f"已选择插件：{asset['name']}（发布 {tag}）")
    archive_path = workspace / "asphyxia-plugin.zip"
    download_asset(asset, archive_path, DOWNLOAD_PREFIX, label, progress, cancel, opener)
    with zipfile.ZipFile(archive_path) as archive:
        entries, wrapper = extraction_entries(archive, cancel)
        progress("去除插件顶层目录：" + wrapper if wrapper else "插件无单一顶层目录，直接保留内容结构。")
        # 构建器提供全新的暂存目标；不向已有目录合并不可信 ZIP 路径。
        for ancestor in reversed(destination.parents):
            if ancestor.exists() or ancestor.is_symlink():
                checksums.reject_link(ancestor)
        destination.mkdir(parents=True, exist_ok=False)
        for entry, relative, directory in entries:
            cancel()
            target = destination / relative
            if directory:
                target.mkdir(parents=True, exist_ok=True)
                continue
            progress("正在解压插件：" + relative)
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(entry) as source, target.open("xb") as output:
                while True:
                    cancel()
                    block = source.read(1024 * 1024)
                    if not block:
                        break
                    output.write(block)
    progress(f"已加入 {label}：{PLUGIN_SOURCE}")
    return asset["name"]
