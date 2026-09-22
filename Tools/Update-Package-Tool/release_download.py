"""GitHub 发布资产的流式下载与完整性校验。"""

import hashlib
from pathlib import Path
import re


def download_asset(asset: dict, destination: Path, url_prefix: str, label: str,
                   progress, cancel, opener) -> None:
    cancel()
    url = asset.get("browser_download_url", "")
    if not url.startswith(url_prefix):
        raise ValueError("发布资产下载地址不属于指定仓库。")
    size = asset.get("size")
    if not isinstance(size, int) or isinstance(size, bool) or size <= 0:
        raise ValueError("发布资产大小无效。")
    digest = asset.get("digest")
    if digest and not re.fullmatch(r"sha256:[0-9a-fA-F]{64}", digest):
        raise ValueError("发布资产的 SHA-256 格式无效。")
    hasher = hashlib.sha256()
    total = 0
    with opener(url) as response, destination.open("xb") as stream:
        content_length = response.headers.get("Content-Length")
        if content_length and int(content_length) != size:
            raise ValueError("下载响应长度与发布资产不一致。")
        while True:
            cancel()
            block = response.read(1024 * 1024)
            if not block:
                break
            total += len(block)
            if total > size:
                raise ValueError("下载内容超出发布资产大小。")
            hasher.update(block)
            stream.write(block)
            progress(f"正在下载 {label}：{total * 100 // size}%")
    if total != size or (digest and hasher.hexdigest() != digest[7:].lower()):
        raise ValueError(label + " 下载长度或 SHA-256 校验失败。")
    if not digest:
        progress("发布方未提供资产 SHA-256，已核对下载长度。")
    cancel()
