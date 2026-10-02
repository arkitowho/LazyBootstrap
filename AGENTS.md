# AGENTS.md

## Overview
LazyBootstrap is an application for arcade rhythm game SOUND VOLTEX's lazy package, designed as a companion tool for Spice2x, providing additional system-level integration and configuration options, making the experience more plug-and-play, not a replacement.

The launcher relies heavily on Spice2x's functionality

## Tech Stack
### Core
- Language: C#
- Framework: Avalonia UI 11
- Runtime: .NET 10
- Build: dotnet

### UI
- SukiUI 6.1.1

### Other
- NAudio.Asio
- Serilog
- Tomlyn

## Structure


## Conventions

- Only use Chinese to reply
- All UI interface text need to use Chinese, except Serilog and any logs outside the UI
- Platform: only win-x64
- Use NativeAOT + Trim
- (Only Windows) Use `pwsh` to execute commands, not `powershell`
- SukiUI: Refer directly to the source code for development (SukiUI/), do not search its docs/wiki online
- Always prioritize checking if SukiUI has a relevant implementation; if not, fall back to AvaloniaUI
- If codegraph initialized in folder, use it

## Anti

- 禁止修改、缩短或改写环境检测异常弹窗中的 Near／Noah 长串提示（`MainWindow.Shell.cs` 的 `ShowEnvironmentScanErrorDialogAsync`）；完整原文、颜文字和换行是程序特色，必须保留。
- Do not use MSBuild, only `dotnet`
- Do not create or introduce a dependency injection (DI) system or container; construct the fixed application object graph explicitly in `ApplicationComposition`
- Do not edit any submodules code
- Do not separate MainWindow.axaml to standalone AXAMLs
- Do not use `GlobalUsings.cs`

## Commands

```
# Build
dotnet build LazyBootstrap.sln -c Release

# Package
build.bat
```
