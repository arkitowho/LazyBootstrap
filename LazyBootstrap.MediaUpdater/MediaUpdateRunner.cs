using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.MediaUpdate;

internal static class MediaUpdateRunner
{
    internal const string SuccessMessage = "Update Successful!";

    public static async Task<int> RunAsync(string game, string package, int parentPid, Action<string> report,
        CancellationToken cancel = default, Action<MediaUpdateProgress> progress = null, string applicationDirectory = null)
    {
        var state = new MediaUpdateProgress(MediaUpdateStage.Waiting, "正在等待启动器退出...");
        void Publish(MediaUpdateProgress value)
        {
            state = value;
            MediaUpdateProgress.Send(progress, value);
        }
        void Record(string message)
        {
            try { using var log = new MediaUpdateLog(game, true); log.Write(message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        void Report(string message)
        {
            try { report?.Invoke(message); } catch { }
        }
        try
        {
            Record($"Parent wait started: pid={parentPid} timeoutSeconds=10 game={game} package={package}");
            Report("正在等待启动器退出...");
            Publish(state);
            await WaitForParentAsync(parentPid, cancel);
            Record($"Parent wait completed: pid={parentPid}");
            await Task.Run(() =>
            {
                using var engine = MediaUpdateEngine.Prepare(game, package, report, cancel, Publish);
                engine.Apply(cancel);
            }, cancel);
            // Only the fixed extraction directory is cleaned; old recovery material is left alone.
            string staging = MediaUpdateProtocol.GetUpdateStagingDirectoryPath(game);
            Report("正在清理更新临时目录...");
            Publish(state with { Stage = MediaUpdateStage.Cleanup, Message = "正在清理更新临时目录...", Path = null, Operation = null });
            try
            {
                if (DirectorySafety.IsWithin(package, staging) && Directory.Exists(staging))
                {
                    Record($"Cleanup started: path={staging}");
                    Directory.Delete(staging, true);
                    Record($"Cleanup completed: path={staging}");
                }
                else Record($"Cleanup skipped: path={staging} package={package}");
            }
            catch (Exception ex)
            {
                string warning = "更新成功，但解压目录清理失败：" + ex.Message;
                Record(MediaUpdateLog.FormatFailure("Cleanup failed", ex, $"path={staging}"));
                Report(warning);
                Publish(state with { Warning = warning });
            }
            Report(SuccessMessage);
            // Installation is complete; cancellation must not turn success into an installation failure.
            for (int seconds = 5; seconds > 0; seconds--)
            {
                Publish(state with { Stage = MediaUpdateStage.Completed, Status = MediaUpdateStatus.Succeeded,
                    Message = SuccessMessage, RemainingSeconds = seconds });
                await Task.Delay(1000);
            }
            Report("正在重新启动启动器。");
            Publish(state with { Message = "正在重新启动启动器。", RemainingSeconds = 0 });
            if (!StartLauncher(game, applicationDirectory ?? AppContext.BaseDirectory, message =>
            {
                Report(message);
                Publish(state with { Warning = string.IsNullOrEmpty(state.Warning) ? message : state.Warning + "\n" + message,
                    RemainingSeconds = null, RequiresAcknowledgement = true });
            }, Record)) Publish(state with { Message = SuccessMessage });
            return 0;
        }
        catch (OperationCanceledException)
        {
            const string message = "更新已取消，未进行安装。";
            if (state.Status != MediaUpdateStatus.Cancelled)
                Record($"Update cancelled: stage={state.Stage} pid={parentPid} completed={state.Completed} total={state.Total}");
            Report(message);
            Publish(state with { Status = MediaUpdateStatus.Cancelled, Message = message, Detail = null, RequiresAcknowledgement = true });
            return 1;
        }
        catch (Exception ex)
        {
            if (state.Status is not (MediaUpdateStatus.Failed or MediaUpdateStatus.Cancelled))
                Record(MediaUpdateLog.FormatFailure("Update failed", ex, $"stage={state.Stage} pid={parentPid}"));
            Report(ex.Message);
            Publish(state with { Status = state.Status == MediaUpdateStatus.Cancelled ? MediaUpdateStatus.Cancelled : MediaUpdateStatus.Failed,
                Message = ex.Message, Detail = state.Status is MediaUpdateStatus.Failed or MediaUpdateStatus.Cancelled ? state.Detail : ex.Message,
                RequiresAcknowledgement = true });
            return 1;
        }
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
            { throw new MediaUpdateException(null, $"Parent process {parentPid} did not exit within 10 seconds.", null,
                "启动器在 10 秒内未退出，未进行安装。请关闭启动器后重试。"); }
        }
    }

    internal static ProcessStartInfo CreateLauncherStartInfo(string game, string applicationDirectory)
    {
        string path = LauncherLocation.FindOuterLauncher(LauncherLocation.GetConfigurationDirectory(applicationDirectory));
        var start = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! };
        start.ArgumentList.Add("--basedir");
        start.ArgumentList.Add(Path.GetFullPath(game));
        return start;
    }

    private static bool StartLauncher(string game, string applicationDirectory, Action<string> report, Action<string> record)
    {
        try
        {
            record($"Launcher restart requested: applicationDirectory={applicationDirectory} game={game}");
            var start = CreateLauncherStartInfo(game, applicationDirectory);
            record($"Launcher process starting: executable={start.FileName} workingDirectory={start.WorkingDirectory} --basedir={Path.GetFullPath(game)}");
            using var process = Process.Start(start);
            if (process != null)
            {
                record($"Launcher process started: pid={process.Id}");
                return true;
            }
            record("Launcher restart failed: Process.Start returned null.");
        }
        catch (Exception ex)
        {
            record(MediaUpdateLog.FormatFailure("Launcher restart failed", ex, $"applicationDirectory={applicationDirectory}"));
            report("无法启动启动器：" + ex.Message);
        }
        report("请手动启动外层启动器（启动.exe 或 Launcher.exe）。");
        return false;
    }
}
