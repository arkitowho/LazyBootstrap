using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using LazyBootstrap.FileSystem;
using LazyBootstrap.Launcher;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

const int ErrorCancelled = 1223;
string baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
string targetExe = Path.Combine(baseDirectory, "launcher", "LazyBootstrap.exe");
string gameDirectory = string.Empty;

if (!File.Exists(targetExe))
{
    ShowError($"未找到主程序：\r\n{targetExe}");
    return;
}

var startInfo = new ProcessStartInfo
{
    FileName = targetExe,
    UseShellExecute = true,
    WorkingDirectory = Path.GetDirectoryName(targetExe) ?? baseDirectory
};

foreach (var arg in args)
{
    startInfo.ArgumentList.Add(arg);
}

try
{
    gameDirectory = LauncherLocation.ResolveBaseDirectory(args,
        Environment.GetEnvironmentVariable(LauncherLocation.BaseDirEnvironmentVariable),
        baseDirectory);
    LauncherConfigPreparation.Prepare(baseDirectory, gameDirectory);
    // Resolve relative --basedir arguments before the child's working directory changes.
    startInfo.ArgumentList.Insert(0, gameDirectory);
    startInfo.ArgumentList.Insert(0, "--basedir");
    using var process = Process.Start(startInfo);
    if (process == null) throw new IOException("未能创建主程序进程。");
}
catch (ConfigCreationPermissionException ex)
{
    Environment.ExitCode = 1;
    using var identity = WindowsIdentity.GetCurrent();
    if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
    {
        ShowError(ex.Message);
        return;
    }

    int result = NativeMethods.MessageBoxW(nint.Zero,
        $"{ex.Message}\r\n\r\n创建配置文件需要管理员权限。点击“确定”申请权限并重试，或点击“取消”退出。",
        "需要管理员权限", 0x00000001 | 0x00000030);
    if (result != 1) return;
    try
    {
        var elevatedStart = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory
        };
        // Keep the resolved game directory even if UAC uses a different account/environment.
        elevatedStart.ArgumentList.Add("--basedir");
        elevatedStart.ArgumentList.Add(gameDirectory);
        foreach (string arg in args) elevatedStart.ArgumentList.Add(arg);
        using var elevated = Process.Start(elevatedStart);
        if (elevated == null) throw new IOException("未能启动管理员进程。");
        Environment.ExitCode = 0;
    }
    catch (Win32Exception elevationError) when (elevationError.NativeErrorCode == ErrorCancelled) { }
    catch (Exception elevationError) { ShowError($"申请管理员权限失败。\r\n{elevationError.Message}"); }
}
catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
{
    Environment.ExitCode = 1;
}
catch (Win32Exception ex)
{
    ShowError($"启动主程序失败。\r\n\r\n错误码：{ex.NativeErrorCode}\r\n{ex.Message}");
}
catch (Exception ex)
{
    ShowError($"启动主程序失败。\r\n\r\n{ex.Message}");
}

static void ShowError(string message)
{
    Environment.ExitCode = 1;
    NativeMethods.MessageBoxW(nint.Zero, message, "启动失败", NativeMethods.MbOk | NativeMethods.MbIconError);
}

internal static class NativeMethods
{
    internal const uint MbOk = 0x00000000;
    internal const uint MbIconError = 0x00000010;

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    internal static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);
}
