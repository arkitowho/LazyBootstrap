@echo off
chcp 65001 >nul
cd /d "%~dp0"
where py >nul 2>nul
if not errorlevel 1 (
    py -3 main.py
) else (
    python main.py
)
if errorlevel 1 (
    echo 启动失败。请安装 Python 3.10 或更新的 64 位版本，并启用 Tcl/Tk。
    pause
)
