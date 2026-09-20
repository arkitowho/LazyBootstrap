"""通过官方 GitHub 发布 API 获取 spice64，网络访问仅在显式勾选后发生。"""

import json
from pathlib import Path, PurePosixPath
import stat
import urllib.request
import zipfile

from release_download import download_asset


API = "https://api.github.com/repos/spice2x/spice2x.github.io/releases"


def open_url(url):
    request = urllib.request.Request(url, headers={"User-Agent": "LazyBootstrap-Update-Package-Tool",
                                                   "Accept": "application/vnd.github+json"})
    return urllib.request.urlopen(request, timeout=20)


def select_release(include_prerelease, cancel, opener):
    cancel()
    url = API + ("?per_page=100" if include_prerelease else "/latest")
    with opener(url) as response:
        data = json.load(response)
    cancel()
    if include_prerelease:
        releases = [item for item in data if not item.get("draft") and item.get("published_at")]
        if not releases:
            raise ValueError("官方仓库没有可用的发布版本。")
        release = max(releases, key=lambda item: item["published_at"])
    else:
        release = data
        if release.get("draft") or release.get("prerelease"):
            raise ValueError("官方最新稳定版信息无效。")
    tag = release.get("tag_name", "")
    assets = [asset for asset in release.get("assets", [])
              if asset.get("name") == f"spice2x-{tag.removeprefix('spice2x-')}.zip"]
    if len(assets) != 1:
        raise ValueError("未找到唯一的普通版 spice2x ZIP 发布资产。")
    return tag, assets[0]


def download(destination: Path, workspace: Path, include_prerelease=False,
             progress=lambda message: None, cancel=lambda: None, *, opener=open_url) -> str:
    tag, asset = select_release(include_prerelease, cancel, opener)
    progress(f"正在下载 spice2x {tag}……")
    archive_path = workspace / "spice2x-download.zip"
    download_asset(asset, archive_path, "https://github.com/spice2x/spice2x.github.io/releases/download/",
                   f"spice2x {tag}", progress, cancel, opener)
    with zipfile.ZipFile(archive_path) as archive:
        entries = [entry for entry in archive.infolist()
                   if PurePosixPath(entry.filename.replace('\\', '/')).name.lower() == "spice64.exe"]
        if len(entries) != 1 or entries[0].is_dir():
            raise ValueError("下载包未包含唯一的 spice64.exe。")
        entry = entries[0]
        if stat.S_ISLNK(entry.external_attr >> 16) or entry.file_size == 0:
            raise ValueError("spice64.exe 不是有效的普通文件。")
        # 固定写入目的文件，不使用 ZIP 内的路径解压。
        with archive.open(entry) as source, destination.open("wb") as output:
            while True:
                cancel()
                block = source.read(1024 * 1024)
                if not block:
                    break
                output.write(block)
    progress(f"已加入 spice2x {tag}：source/contents/spice64.exe")
    return tag
