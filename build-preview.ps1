<#
.SYNOPSIS
构建无需 AOT 的 win-x64 预览版本。
.DESCRIPTION
默认使用 Debug，关闭 NativeAOT、裁剪和自包含发布，使用本机 .NET 10 运行时。
输出位于 artifacts/preview，保留预览配置，重复执行使用增量构建。
.PARAMETER Run
构建成功后通过外层 Launcher 打开预览。
.EXAMPLE
pwsh -File ./build-preview.ps1 -Run
.EXAMPLE
pwsh -File ./build-preview.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [switch] $Run,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$previewRoot = $PSScriptRoot
$previewDirectory = Join-Path $previewRoot 'artifacts/preview'
$previewLauncherDirectory = Join-Path $previewDirectory 'launcher'

$previewProjects = @(
    @{ Path = 'LazyBootstrap.Launcher/LazyBootstrap.Launcher.csproj'; Output = $previewDirectory },
    @{ Path = 'LazyBootstrap/LazyBootstrap.csproj'; Output = $previewLauncherDirectory },
    @{ Path = 'LazyBootstrap.MediaUpdater/LazyBootstrap.MediaUpdater.csproj'; Output = $previewLauncherDirectory }
)

foreach ($project in $previewProjects) {
    $projectPath = Join-Path $previewRoot $project.Path
    & dotnet publish $projectPath -c $Configuration -r win-x64 -o $project.Output `
        --self-contained false -p:PublishAot=false -p:PublishTrimmed=false `
        -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:UseAppHost=true
    if ($LASTEXITCODE -ne 0) {
        throw "预览构建失败：$projectPath（退出码 $LASTEXITCODE）。若预览程序正在运行，请关闭后重试。"
    }
}

$previewExecutable = Join-Path $previewDirectory 'Launcher.exe'
foreach ($requiredFile in @(
    $previewExecutable,
    (Join-Path $previewLauncherDirectory 'LazyBootstrap.exe'),
    (Join-Path $previewLauncherDirectory 'MediaUpdater.exe')
)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "预览构建缺少输出文件：$requiredFile"
    }
}

Write-Host "预览构建完成（$Configuration，无 AOT、无裁剪）：$previewDirectory"
Write-Host "启动预览：$previewExecutable"

if ($Run) {
    $previewStartInfo = [System.Diagnostics.ProcessStartInfo]::new($previewExecutable)
    $previewStartInfo.UseShellExecute = $true
    $previewStartInfo.WorkingDirectory = $previewDirectory
    $previewStartInfo.ArgumentList.Add('--basedir')
    $previewStartInfo.ArgumentList.Add($previewDirectory)
    $previewProcess = [System.Diagnostics.Process]::Start($previewStartInfo)
    if ($null -eq $previewProcess) {
        throw '未能启动预览程序。'
    }
    $previewProcess.Dispose()
}
