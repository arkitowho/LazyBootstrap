param([string] $PublishRoot = (Join-Path $PSScriptRoot '../artifacts/config-migration'))

$ErrorActionPreference = 'Stop'
$PublishRoot = [IO.Path]::GetFullPath($PublishRoot)
$fixture = Join-Path $PublishRoot ('smoke-' + [Guid]::NewGuid().ToString('N'))
$app = Join-Path $fixture 'launcher'
$oldCompatibility = $env:__COMPAT_LAYER
$oldGameDirectory = $env:LAZYBOOTSTRAP_BASEDIR

# Only inspect/close windows owned by the test processes created below.
Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class ConfigSmokeWindows {
    delegate bool EnumCallback(IntPtr handle, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumCallback callback, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr handle, StringBuilder text, int capacity);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr handle, uint message, IntPtr wparam, IntPtr lparam);
    public static string ReadAndDismiss(int processId) {
        var texts = new List<string>();
        EnumWindows((window, _) => {
            GetWindowThreadProcessId(window, out uint pid);
            if (pid != processId) return true;
            var title = new StringBuilder(4096); GetWindowText(window, title, title.Capacity);
            texts.Add(title.ToString());
            EnumChildWindows(window, (child, state) => {
                var text = new StringBuilder(4096); GetWindowText(child, text, text.Capacity);
                texts.Add(text.ToString()); return true;
            }, IntPtr.Zero);
            if (title.ToString().Contains("失败") || title.ToString() == "需要管理员权限")
                PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return string.Join("\n", texts);
    }
}
'@

function Assert-Condition([bool] $Value, [string] $Message) {
    if (-not $Value) { throw $Message }
}

function Stop-TestProcess($Process) {
    if (-not $Process.HasExited) {
        $null = $Process.CloseMainWindow()
        if (-not $Process.WaitForExit(3000)) { $Process.Kill(); $Process.WaitForExit() }
    }
    $Process.Dispose()
}

function Test-ErrorDialog([string] $Executable, [string] $Expected) {
    $process = Start-Process -FilePath $Executable -WorkingDirectory $PublishRoot -WindowStyle Hidden -PassThru
    try {
        $text = ''
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
            $text += [ConfigSmokeWindows]::ReadAndDismiss($process.Id)
            if ($text.Contains($Expected)) { break }
            Start-Sleep -Milliseconds 100
        }
        Assert-Condition ($text.Contains($Expected)) "未显示预期中文错误：$Expected；实际：$text"
        Assert-Condition ($process.WaitForExit(3000)) '错误提示关闭后进程未退出'
        Assert-Condition ($process.ExitCode -ne 0) '失败启动返回了成功'
    }
    finally { Stop-TestProcess $process }
}

try {
    New-Item -ItemType Directory -Path (Join-Path $app 'Libs') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PublishRoot 'outer/Launcher.exe') -Destination $fixture
    Copy-Item -LiteralPath (Join-Path $PublishRoot 'regression/RegressionTests.exe') -Destination (Join-Path $app 'LazyBootstrap.exe')

    # The outer Launcher must start normally without requesting elevation.
    $env:__COMPAT_LAYER = $null
    $env:LAZYBOOTSTRAP_BASEDIR = $null
    $outer = Join-Path $fixture 'Launcher.exe'
    $config = Join-Path $fixture 'config.toml'
    $marker = Join-Path $fixture 'child.txt'
    $process = Start-Process -FilePath $outer -ArgumentList @('--launcher-smoke-child', ('"' + $marker + '"')) -WorkingDirectory $PublishRoot -WindowStyle Hidden -PassThru
    try {
        Assert-Condition ($process.WaitForExit(15000)) 'Launcher 未退出'
        Assert-Condition ($process.ExitCode -eq 0) 'Launcher 启动失败'
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        while (-not (Test-Path -LiteralPath $marker) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        $lines = Get-Content -LiteralPath $marker
        Assert-Condition ($lines[0] -eq $config -and [IO.Path]::TrimEndingDirectorySeparator($lines[1]) -eq $fixture) '子进程读取了错误的配置或游戏目录'
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        try { $isElevated = ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
        finally { $identity.Dispose() }
        Assert-Condition ($lines[3] -eq $isElevated.ToString()) 'Launcher 改变了默认进程权限'
        Assert-Condition (Test-Path -LiteralPath $config) 'Launcher 未创建配置'
        Assert-Condition (-not (Test-Path -LiteralPath (Join-Path $app 'config.toml'))) 'Launcher 创建了旧位置配置'
        Write-Output '通过：NativeAOT Launcher 初始化、传参、启动子进程'
    }
    finally { Stop-TestProcess $process }

    # Wait until the child has released its executable before replacing the test stub.
    Start-Sleep -Milliseconds 500
    # Main-program UI checks do not exercise its separate administrator requirement.
    $env:__COMPAT_LAYER = 'RunAsInvoker'
    Copy-Item -Path (Join-Path $PublishRoot 'launcher/*') -Destination $app -Recurse -Force
    $main = Join-Path $app 'LazyBootstrap.exe'
    $seed = [IO.File]::ReadAllText($config).Replace('compatlayer = "false"', 'compatlayer = "true"').Replace('maindisplayid = ""', "maindisplayid = `"`"`r`nmainscreen = `"0`"")
    [IO.File]::WriteAllText($config, $seed)
    $process = Start-Process -FilePath $main -WorkingDirectory $PublishRoot -WindowStyle Hidden -PassThru
    try {
        $log = Join-Path $app 'LazyBootstrap.log'
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $content = ''
        do {
            Start-Sleep -Milliseconds 200
            if (Test-Path -LiteralPath $log) {
                $reader = [IO.StreamReader]::new([IO.File]::Open($log, 'Open', 'Read', 'ReadWrite'))
                try { $content = $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
        } while (-not $process.HasExited -and -not $content.Contains('Display configuration change handled.') -and [DateTime]::UtcNow -lt $deadline)
        Assert-Condition ($content.Contains('Display configuration change handled.')) '主程序未完成显示器状态加载'
        Start-Sleep -Milliseconds 1000
        Assert-Condition ([IO.File]::ReadAllText($config) -ceq $seed) '主程序启动或硬件检测改写了配置'
        Write-Output '通过：NativeAOT 主程序启动、显卡与显示器加载不改写配置'
    }
    finally { Stop-TestProcess $process }

    Remove-Item -LiteralPath $config
    Test-ErrorDialog $main '请通过外层 Launcher'
    Assert-Condition (-not (Test-Path -LiteralPath $config)) '主程序创建了缺失配置'
    [IO.File]::WriteAllText($config, '[broken')
    Test-ErrorDialog $main '请通过外层 Launcher'
    Assert-Condition ([IO.File]::ReadAllText($config) -eq '[broken') '主程序修复了损坏配置'
    Write-Output '通过：主程序配置缺失、损坏时中文提示并停止'

    [IO.File]::WriteAllText($config, $seed)
    $env:__COMPAT_LAYER = $null
    [IO.File]::SetAttributes($config, [IO.FileAttributes]::ReadOnly)
    try { Test-ErrorDialog $outer '检查配置读写权限失败' }
    finally { [IO.File]::SetAttributes($config, [IO.FileAttributes]::Normal) }
    Write-Output '通过：Launcher 配置准备失败时中文提示并停止'
    if (-not $isElevated) {
        Remove-Item -LiteralPath $config
        $originalAcl = Get-Acl -LiteralPath $fixture
        $changedAcl = Get-Acl -LiteralPath $fixture
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        try { $rule = [Security.AccessControl.FileSystemAccessRule]::new($identity.User, 'CreateFiles', 'Deny') }
        finally { $identity.Dispose() }
        $changedAcl.AddAccessRule($rule)
        try {
            Set-Acl -LiteralPath $fixture -AclObject $changedAcl
            Test-ErrorDialog $outer '创建配置文件需要管理员权限'
            Assert-Condition (-not (Test-Path -LiteralPath $config)) '取消提权后仍创建了配置'
        }
        finally {
            $restoreAcl = Get-Acl -LiteralPath $fixture
            $restoreAcl.SetSecurityDescriptorBinaryForm($originalAcl.GetSecurityDescriptorBinaryForm(), [Security.AccessControl.AccessControlSections]::Access)
            Set-Acl -LiteralPath $fixture -AclObject $restoreAcl
        }
        Write-Output '通过：仅创建权限不足时提示提权，取消后退出'
    }
    Write-Output "启动测试目录：$fixture"
}
finally {
    $env:__COMPAT_LAYER = $oldCompatibility
    $env:LAZYBOOTSTRAP_BASEDIR = $oldGameDirectory
}
