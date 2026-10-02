@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem Usage: build-preview.bat [-Run] [-Configuration Debug^|Release]
set "previewConfiguration=Debug"
set "previewRun=0"

:arguments
if "%~1"=="" goto ready
if /i "%~1"=="-Run" goto runArgument
if /i "%~1"=="-Configuration" goto configurationArgument
exit /b 2

:runArgument
set "previewRun=1"
shift
goto arguments

:configurationArgument
if /i "%~2"=="Debug" goto validConfiguration
if /i "%~2"=="Release" goto validConfiguration
exit /b 2

:validConfiguration
set "previewConfiguration=%~2"
shift
shift
goto arguments

:ready
pushd "%~dp0" >nul 2>&1
if errorlevel 1 exit /b 1
set "previewDirectory=%CD%\debug"
call :build
set "previewResult=%errorlevel%"
popd >nul 2>&1
if "%previewResult%"=="0" pause
exit /b %previewResult%

:build
dotnet publish "%~dp0LazyBootstrap.Launcher\LazyBootstrap.Launcher.csproj" -c %previewConfiguration% -r win-x64 -o "%previewDirectory%" --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:UseAppHost=true
if errorlevel 1 exit /b 1
dotnet publish "%~dp0LazyBootstrap\LazyBootstrap.csproj" -c %previewConfiguration% -r win-x64 -o "%previewDirectory%\launcher" --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:UseAppHost=true
if errorlevel 1 exit /b 1
dotnet publish "%~dp0LazyBootstrap.MediaUpdater\LazyBootstrap.MediaUpdater.csproj" -c %previewConfiguration% -r win-x64 -o "%previewDirectory%\launcher" --self-contained false -p:PublishAot=false -p:PublishTrimmed=false -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:UseAppHost=true
if errorlevel 1 exit /b 1
if not exist "%previewDirectory%\Launcher.exe" exit /b 1
if not exist "%previewDirectory%\launcher\LazyBootstrap.exe" exit /b 1
if not exist "%previewDirectory%\launcher\MediaUpdater.exe" exit /b 1
if "%previewRun%"=="1" (
    start "" /D "%previewDirectory%" "%previewDirectory%\Launcher.exe" --basedir "%previewDirectory%"
    if errorlevel 1 exit /b 1
)
exit /b 0
