"""调用仓库的发布脚本，转发日志并支持取消整个编译进程树。"""

from pathlib import Path
import queue
import shutil
import subprocess
import threading


REPO_ROOT = Path(__file__).resolve().parents[2]


def compile_launcher(progress=lambda message: None, cancel=lambda: None):
    cancel()
    executable = shutil.which("pwsh")
    if executable is None:
        raise ValueError("未找到 pwsh，请安装 PowerShell 7 并加入 PATH。")
    script = REPO_ROOT / "build.ps1"
    if not script.is_file():
        raise ValueError("未找到仓库根目录的 build.ps1。")
    progress("正在执行 build.ps1，编译启动器、主程序和更新器……")
    messages = queue.Queue()
    with subprocess.Popen([executable, "-NoProfile", "-NonInteractive", "-File", str(script)],
                          cwd=REPO_ROOT, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                          stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
                          creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0)) as process:
        def read_output():
            try:
                for line in process.stdout:
                    messages.put(line.rstrip())
            finally:
                messages.put(None)

        reader = threading.Thread(target=read_output, daemon=True)
        reader.start()
        try:
            ended = False
            while not ended or process.poll() is None:
                cancel()
                try:
                    message = messages.get(timeout=0.1)
                except queue.Empty:
                    continue
                if message is None:
                    ended = True
                elif message:
                    progress("编译输出：" + message)
            if process.wait() != 0:
                raise ValueError(f"build.ps1 编译失败（退出代码 {process.returncode}），未生成更新包。")
            cancel()
        finally:
            if process.poll() is None:
                # 只结束本次启动的 pwsh 及其 dotnet / NativeAOT 子进程。
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                               creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0), check=False)
                process.wait()
            reader.join(timeout=2)
    progress("启动器编译完成，正在检查 build 目录。")
