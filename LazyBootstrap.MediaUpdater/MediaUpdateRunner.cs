using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.MediaUpdate;

internal static class MediaUpdateRunner
{
    public static async Task<int> RunAsync(string game, string package, int parentPid, Action<string> report,
        CancellationToken cancel = default)
    {
        void Record(string message)
        {
            try { using var log = new MediaUpdateLog(game, true); log.Write(message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { report?.Invoke(message); } catch { }
        }
        try
        {
            Record("正在等待启动器退出...");
            await WaitForParentAsync(parentPid, cancel);
            await Task.Run(() =>
            {
                using var engine = MediaUpdateEngine.Prepare(game, package, report, cancel);
                engine.Apply(cancel);
            }, cancel);
            // Only the fixed extraction directory is cleaned; old recovery material is left alone.
            string staging = MediaUpdateProtocol.GetUpdateStagingDirectoryPath(game);
            try
            {
                if (DirectorySafety.IsWithin(package, staging) && Directory.Exists(staging)) Directory.Delete(staging, true);
            }
            catch (Exception ex) { Record("更新成功，但解压目录清理失败：" + ex.Message); }
            Record("更新成功，正在重新启动启动器。");
            StartLauncher(game, Record);
            return 0;
        }
        catch (OperationCanceledException) { Record("更新已取消，未进行安装。"); return 1; }
        catch (Exception ex) { Record(ex.Message); return 1; }
    }

    internal static async Task WaitForParentAsync(int parentPid, CancellationToken cancel)
    {
        if (parentPid <= 0) throw new IOException("启动器进程 ID 无效。");
        Process parent;
        try { parent = Process.GetProcessById(parentPid); }
        catch (ArgumentException) { return; }
        using (parent)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { await parent.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            { throw new IOException("启动器在 10 秒内未退出，未进行安装。请关闭启动器后重试。"); }
        }
    }

    private static void StartLauncher(string game, Action<string> report)
    {
        try
        {
            string path = LauncherLocation.FindOuterLauncher(game);
            using var process = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path)!
            });
            if (process != null) return;
        }
        catch (Exception ex) { report("无法启动启动器：" + ex.Message); }
        report("请从游戏根目录手动启动启动器。");
    }
}
