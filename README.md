# LazyBootstrap

> [!CAUTION]
> **DO NOT talk about/mentioned this project or name outside this repo!**

This is a lazy launcher for a “Woosh Woosh” game, need combined with Spice2x.

This application is designed as a companion tool for Spice2x, providing additional system-level integration and configuration options. It is not a replacement for Spice2x. This project relies on Spice2x to function and does not alter or modify any of its features. Please do not treat this application as an alternative or equivalent project to Spice2x.

## Build

Install .NET SDK 10.0 and click `build.bat`

## Third Party

- SukiUI - 6.1.1

## 更新包

更新包使用 `update.json` 声明覆盖合并、删除、镜像及XML 结构修改（XPath 定位），由 MediaUpdater 自行执行，不再使用 `sync.bat` 或 robocopy。支持重复安装及内容修改后重新生成校验清单再安装，每次都会重新校验并执行操作。`config.toml` 按普通文件处理，更新器自身通过 `.pending` 在下次启动替换。不再备份、回滚或启动恢复。

更新包必须同时包含 `update.json` 与 SHA-256 校验清单 `checksums.json`。制作完成后运行 `python Tools/generate_update_checksums.py "更新包目录"` 自动生成校验清单；缺失、额外文件或摘要不符均拒绝更新。

更新流程为：启动器解压、校验 → 更新器预演、安装。预演检查全部变更的占用和访问权限，失败则不安装；安装开始后发生错误会保留已完成修改并停止。更新器不再复验摘要；解压文件和同一次更新日志存放在 `.media-update/`。成功后立即重新启动，失败保留解压目录。启动器与 MediaUpdater 需一并更新。

参见 [更新包制作说明](Packaging/README.md)、[清单模板](Packaging/update.json)、[操作 JSON Schema](Packaging/update.schema.json) 和 [校验清单 JSON Schema](Packaging/checksums.schema.json)。首次使用需部署支持新协议的 Launcher、主程序及 MediaUpdater。需要示例更新 ZIP 时，单独运行 `pwsh Packaging/New-ExamplePackages.ps1`。

## Regression tests

Run `dotnet run --project Tests/RegressionTests/RegressionTests.csproj -c Release` on Windows with .NET 10 and `pwsh`. Tests use isolated temporary directories and exercise save migration, failed-copy preservation, manifest-driven updates, XPath-based XML editing, crash/rollback recovery, and link/path containment. They do not start the game, terminate launcher processes, or install anything.
