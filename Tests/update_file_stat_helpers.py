"""模拟目录枚举引起的大小变化，不改变目录内容或其他元数据。"""

from contextlib import contextmanager
from pathlib import Path
import stat
from types import SimpleNamespace
from unittest import mock


@contextmanager
def directory_size_changes():
    original_lstat = Path.lstat
    original_iterdir = Path.iterdir
    sizes = {}

    def lstat(path, *args, **kwargs):
        info = original_lstat(path, *args, **kwargs)
        if not stat.S_ISDIR(info.st_mode):
            return info
        values = {name: getattr(info, name) for name in dir(info) if name.startswith("st_")}
        values["st_size"] = sizes.get(path, 0)
        return SimpleNamespace(**values)

    def iterdir(path):
        children = list(original_iterdir(path))
        sizes[path] = 4096 if sizes.get(path, 0) == 0 else 0
        return iter(children)

    with mock.patch.object(Path, "lstat", lstat), mock.patch.object(Path, "iterdir", iterdir):
        yield
