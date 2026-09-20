"""为更新包生成 checksums；仅使用 Python 标准库。"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import sys
import tempfile


CHECKSUM_NAME = "checksums"


def validate_relative(path: str) -> None:
    for part in path.split("/"):
        if (not part or part in (".", "..") or part.endswith((" ", "."))
                or any(ord(c) < 32 or c in '<>:"|?*\\' for c in part)):
            raise ValueError(f"更新路径含有非法片段：{path}")
        stem = part.split(".")[0].upper()
        if (stem in {"CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$"}
                or (len(stem) == 4 and stem[:3] in {"COM", "LPT"}
                    and stem[3] in "123456789¹²³")):
            raise ValueError(f"更新路径不能使用 Windows 设备名：{path}")


def reject_link(path: Path) -> os.stat_result:
    info = path.lstat()
    if stat.S_ISLNK(info.st_mode) or getattr(info, "st_file_attributes", 0) & 0x400:
        raise ValueError(f"更新包不能包含符号链接或目录联接：{path}")
    if not stat.S_ISDIR(info.st_mode) and not stat.S_ISREG(info.st_mode):
        raise ValueError(f"更新包包含非常规文件：{path}")
    return info


def scan(root: Path, *, cancel=None) -> list[Path]:
    files = []
    seen = set()

    def visit(directory: Path) -> None:
        if cancel:
            cancel()
        reject_link(directory)
        for path in directory.iterdir():
            if cancel:
                cancel()
            relative = path.relative_to(root).as_posix()
            validate_relative(relative)
            key = relative.upper()
            if key in seen:
                raise ValueError(f"包内路径大小写冲突：{relative}")
            seen.add(key)
            info = reject_link(path)
            if stat.S_ISDIR(info.st_mode):
                visit(path)
            else:
                files.append(path)

    visit(root)
    return files


def fingerprint(path: Path) -> tuple:
    info = reject_link(path)
    return info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns


def generate(directory: str, *, progress=None, cancel=None) -> Path:
    """可选 progress 接收中文消息；cancel 在取消时应抛出异常。"""
    def report(message):
        if progress is None:
            print(message, flush=True)
        else:
            progress(message)

    def checkpoint():
        if cancel:
            cancel()

    checkpoint()
    root = Path(os.path.abspath(directory))
    for ancestor in [root, *root.parents]:
        reject_link(ancestor)
    files = scan(root) if cancel is None else scan(root, cancel=cancel)
    manifests = [path for path in files if path.name.lower() == "update"]
    if len(manifests) != 1:
        raise ValueError("更新包必须包含且仅包含一份 update。")
    package = manifests[0].parent
    if any(not path.is_relative_to(package) for path in files):
        raise ValueError("所有文件必须位于 update 所在的包根目录内。")
    existing = [path for path in files if path.parent == package and path.name.lower() == CHECKSUM_NAME]
    destination = existing[0] if existing else package / CHECKSUM_NAME
    payload = sorted((path for path in files if path != destination),
                     key=lambda path: path.relative_to(package).as_posix())
    entries = []
    stamps = {}
    for index, path in enumerate(payload, 1):
        checkpoint()
        relative = path.relative_to(package).as_posix()
        report(f"正在计算 SHA-256 {index}/{len(payload)}：{relative}")
        before = fingerprint(path)
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            while block := stream.read(1024 * 1024):
                checkpoint()
                digest.update(block)
        if fingerprint(path) != before:
            raise ValueError(f"计算期间文件发生变化：{relative}")
        stamps[path] = before
        entries.append({"path": relative, "sha256": digest.hexdigest()})
    rescanned = scan(root) if cancel is None else scan(root, cancel=cancel)
    current = [path for path in rescanned if path != destination]
    if set(current) != set(payload) or any(fingerprint(path) != stamps[path] for path in current):
        raise ValueError("计算期间更新包发生变化，请停止修改文件后重新生成。")
    document = {"algorithm": "SHA256", "files": entries}
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", newline="\n",
                                         prefix=".checksums-", suffix=".tmp", dir=package, delete=False) as stream:
            temporary = Path(stream.name)
            json.dump(document, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        checkpoint()
        os.replace(temporary, destination)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
    report(f"已生成校验清单，共 {len(entries)} 个文件：{destination}")
    return destination


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="扫描更新包全部文件，生成 SHA-256 校验清单。")
    parser.add_argument("directory", help="更新包目录（允许外套目录）")
    args = parser.parse_args()
    try:
        generate(args.directory)
        return 0
    except (OSError, ValueError) as error:
        print(f"生成失败：{error}", file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("已取消生成，原有校验清单保持不变。", file=sys.stderr)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
