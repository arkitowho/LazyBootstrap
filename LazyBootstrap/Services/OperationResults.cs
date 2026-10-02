using System.Collections.Generic;
using System.Linq;

namespace LazyBootstrap.Services;

internal sealed record ProcessTerminationResult(string ProcessName, int TerminatedCount, bool Succeeded, bool PermissionDenied = false);

internal sealed record ManualStopResult(IReadOnlyList<ProcessTerminationResult> Processes, int PendingDisplayCount)
{
    public bool Succeeded => PendingDisplayCount == 0 && Processes.All(result => result.Succeeded);

    public string Message
    {
        get
        {
            if (Succeeded) return "游戏已停止。";
            var messages = new List<string>();
            var failed = Processes.Where(result => !result.Succeeded).ToArray();
            if (failed.Length > 0)
            {
                messages.Add($"未能确认 {string.Join("、", failed.Select(result => result.ProcessName))} 已停止。");
                messages.Add(failed.Any(result => result.PermissionDenied)
                    ? "请以管理员身份重试停止，或在任务管理器中结束进程。"
                    : "请重试停止，或在任务管理器中检查进程。详情请查看日志。");
            }
            if (PendingDisplayCount > 0)
                messages.Add("部分显示器设置尚未还原，请再次点击停止重试。");
            return string.Join("\n", messages);
        }
    }
}

internal sealed class RuntimeInstallResult
{
    public List<string> Installed { get; } = new();
    public List<string> Failed { get; } = new();
    public List<string> Missing { get; } = new();
    public bool Cancelled { get; private set; }
    public bool Succeeded => !Cancelled && Failed.Count == 0 && Missing.Count == 0 && Installed.Count > 0;
    public bool ShouldNotify => !Cancelled || Failed.Count > 0;

    public void Record(string name, int exitCode)
    {
        if (exitCode == -1223) Cancelled = true;
        else if (exitCode == 0) Installed.Add(name);
        else Failed.Add(name);
    }

    public string Message
    {
        get
        {
            var messages = new List<string>();
            if (Installed.Count > 0) messages.Add($"已安装：{string.Join("、", Installed)}。");
            if (Failed.Count > 0) messages.Add($"安装未完成：{string.Join("、", Failed)}。请重试安装，详情请查看日志。");
            if (Missing.Count > 0) messages.Add($"缺少安装程序：{string.Join("、", Missing)}。请补齐运行库安装文件后重试。");
            return string.Join("\n", messages);
        }
    }
}
