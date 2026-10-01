@echo off
setlocal EnableExtensions DisableDelayedExpansion

pushd "%~dp0" >nul 2>&1
if errorlevel 1 exit /b 1
rem These fixed paths are always relative to this script's repository root.
call :build
set "buildResult=%errorlevel%"
if exist "build_tmp" rmdir /s /q "build_tmp" >nul 2>&1
if exist "build_tmp" set "buildResult=1"
popd >nul 2>&1
if "%buildResult%"=="0" pause
exit /b %buildResult%

:build
if exist "build" rmdir /s /q "build"
if exist "build" exit /b 1
if exist "build_tmp" rmdir /s /q "build_tmp"
if exist "build_tmp" exit /b 1
mkdir "build\launcher" "build_tmp"
if errorlevel 1 exit /b 1

dotnet publish "%~dp0LazyBootstrap.Launcher\LazyBootstrap.Launcher.csproj" -c Release -r win-x64 -o "build_tmp\launcher_publish"
if errorlevel 1 exit /b 1
dotnet publish "%~dp0LazyBootstrap\LazyBootstrap.csproj" -c Release -r win-x64 -o "build_tmp\main_publish"
if errorlevel 1 exit /b 1
dotnet publish "%~dp0LazyBootstrap.MediaUpdater\LazyBootstrap.MediaUpdater.csproj" -c Release -r win-x64 -o "build_tmp\mediaupdater_publish"
if errorlevel 1 exit /b 1

if not exist "build_tmp\launcher_publish\Launcher.exe" exit /b 1
if not exist "build_tmp\main_publish\LazyBootstrap.exe" exit /b 1
if not exist "build_tmp\mediaupdater_publish\MediaUpdater.exe" exit /b 1
copy /y "build_tmp\launcher_publish\Launcher.exe" "build\Launcher.exe" >nul
if errorlevel 1 exit /b 1
robocopy "build_tmp\main_publish" "build\launcher" /E /XF *.pdb *.log *.tmp *.bak /R:0 /W:0 >nul
if errorlevel 8 exit /b 1
robocopy "build_tmp\mediaupdater_publish" "build\launcher" /E /XF *.pdb *.log *.tmp *.bak /R:0 /W:0 >nul
if errorlevel 8 exit /b 1
if not exist "build\Launcher.exe" exit /b 1
if not exist "build\launcher\LazyBootstrap.exe" exit /b 1
if not exist "build\launcher\MediaUpdater.exe" exit /b 1
exit /b 0
