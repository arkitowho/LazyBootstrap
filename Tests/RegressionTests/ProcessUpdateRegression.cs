using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LazyBootstrap.MediaUpdate;
using LazyBootstrap.FileSystem;

internal static partial class UpdateRegression
{
    public static int Worker(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 1 && args[0] == "wait")
        {
            Console.WriteLine("ready");
            Console.ReadLine();
            return 0;
        }
        try
        {
            if (args.Length == 2 && args[0] == "--basedir" && File.Exists(Path.Combine(AppContext.BaseDirectory, "restart-probe.enabled")))
            {
                string marker = Path.Combine(AppContext.BaseDirectory, "restart-observed.txt");
                File.WriteAllLines(marker + ".tmp", new[] { args[1], Environment.CurrentDirectory, Path.GetFileName(Environment.ProcessPath) });
                File.Move(marker + ".tmp", marker);
                return 0;
            }
            if (args.Length == 3 && args[0] == "verify") { Console.WriteLine(Verify(args[1], args[2])); return 0; }
            if (args.Length == 3 && args[0] == "apply")
            {
                using var engine = MediaUpdateEngine.Prepare(args[1], args[2]); engine.Apply(); return 0;
            }
            return 2;
        }
        catch (Exception ex) { Console.WriteLine(ex.Message); return 1; }
    }

    private sealed class WaitingChild : IDisposable
    {
        public Process Process { get; }
        public WaitingChild()
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "RegressionTests.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("wait");
            Process = Process.Start(start)!;
            if (Process.StandardOutput.ReadLine() != "ready") throw new Exception("等待测试进程未就绪");
        }
        public void Exit()
        {
            if (Process.HasExited) return;
            Process.StandardInput.WriteLine(); Process.StandardInput.Flush();
            if (!Process.WaitForExit(5000)) throw new Exception("测试进程未退出");
        }
        public void Dispose()
        {
            try { Exit(); }
            finally { if (!Process.HasExited) Process.Kill(); Process.Dispose(); }
        }
    }

    private static void RunProcessTests()
    {
        RunDashboardTests();
        Test("分离游戏目录后通过正确的外层 Launcher 重启并保留参数", root =>
        {
            var (game, package) = Pack(root, Delete("contents/missing"));
            string outer = Path.Combine(root, "独立启动器");
            string app = Path.Combine(outer, "launcher");
            Directory.CreateDirectory(app);
            Put(game, "Launcher.exe", "wrong-location");
            Reject(() => MediaUpdateRunner.CreateLauncherStartInfo(game, app));
            foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
                File.Copy(file, Path.Combine(outer, Path.GetFileName(file)));
            string worker = Path.Combine(outer, "RegressionTests.exe");
            File.Copy(worker, Path.Combine(outer, "Launcher.exe"));
            Check(Path.GetFileName(MediaUpdateRunner.CreateLauncherStartInfo(game, app).FileName) == "Launcher.exe", "英文入口不可用");
            File.Copy(worker, Path.Combine(outer, "启动.exe"));
            var start = MediaUpdateRunner.CreateLauncherStartInfo(game, app);
            Check(Path.GetFileName(start.FileName) == "启动.exe" && start.WorkingDirectory == outer, "外层启动位置或优先级错误");
            Check(start.ArgumentList.SequenceEqual(new[] { "--basedir", Path.GetFullPath(game) }), "重启丢失游戏目录参数");
            Put(outer, "restart-probe.enabled", "test fixture");
            using var parent = new WaitingChild(); int pid = parent.Process.Id; parent.Exit();
            var progress = new List<MediaUpdateProgress>();
            Check(MediaUpdateRunner.RunAsync(game, package, pid, null, progress: progress.Add, applicationDirectory: app).GetAwaiter().GetResult() == 0,
                "分离布局更新失败");
            string marker = Path.Combine(outer, "restart-observed.txt");
            Check(SpinWait.SpinUntil(() => File.Exists(marker), TimeSpan.FromSeconds(5)), "未启动外层 Launcher");
            Check(File.ReadAllLines(marker).SequenceEqual(new[] { Path.GetFullPath(game), outer, "启动.exe" }), "子进程收到错误的目录或入口");
            Check(progress[^1].Status == MediaUpdateStatus.Succeeded && progress[^1].Warning == null, "正常重启产生错误警告");
        });

        Test("仅等待指定启动器，退出后安装且不结束其他进程", root =>
        {
            var (game, staging) = Pack(root, Copy("source/a", "contents/a")); Put(staging, "source/a", "new"); Put(game, "contents/a", "old"); Seal(staging);
            string package = Verify(game, staging);
            using var parent = new WaitingChild(); using var other = new WaitingChild();
            var messages = new List<string>();
            var clock = Stopwatch.StartNew();
            var progressTimes = new List<TimeSpan>();
            var snapshots = new List<MediaUpdateProgress>();
            Task<int> task = MediaUpdateRunner.RunAsync(game, package, parent.Process.Id, message =>
            {
                messages.Add(message);
                progressTimes.Add(clock.Elapsed);
            }, progress: snapshots.Add);
            Check(!task.IsCompleted, "没有等待指定进程"); Equal(game, "contents/a", "old");
            parent.Exit(); Check(task.GetAwaiter().GetResult() == 0, "退出后安装失败");
            Equal(game, "contents/a", "new"); Check(!other.Process.HasExited, "结束了其他进程");
            Check(!Directory.Exists(staging), "成功未清理解压目录"); CheckNoTransaction(game);
            int success = messages.IndexOf("Update Successful!");
            int restart = messages.IndexOf("正在重新启动启动器。");
            Check(success > 0 && restart > success, "缺少成功提示或重启顺序错误");
            Check(progressTimes[restart] - progressTimes[success] >= TimeSpan.FromMilliseconds(4950), "成功提示未停留 5 秒");
            Check(snapshots.Where(p => p.RemainingSeconds > 0).Select(p => p.RemainingSeconds!.Value).SequenceEqual(new[] { 5, 4, 3, 2, 1 }), "成功倒计时顺序错误");
            Check(snapshots.Select(p => p.Stage).Distinct().SequenceEqual(new[] { MediaUpdateStage.Waiting, MediaUpdateStage.Preflight,
                MediaUpdateStage.Installing, MediaUpdateStage.Cleanup, MediaUpdateStage.Completed }), "阶段顺序错误");
            Check(snapshots[^1].Status == MediaUpdateStatus.Succeeded && snapshots[^1].RequiresAcknowledgement
                && snapshots[^1].Warning.Contains("手动启动"), "启动入口缺失时未保留成功提示并等待确认");
            string log = File.ReadAllText(Path.Combine(game, ".media-update/update_log.txt"));
            Check(log.Contains("Package SHA256 verification completed.") && log.Contains("Installing"), "校验与安装未保存在同一日志");
            SingleLogEvent(log, "Launcher restart failed");
            NoUpdateUiInLog(log);
        });
        Test("指定启动器超时不安装也不终止进程", root =>
        {
            var (game, package) = Pack(root, Delete("contents/a")); Put(game, "contents/a", "old"); Seal(package); Verify(game, package);
            using var parent = new WaitingChild(); var messages = new List<string>();
            int result = MediaUpdateRunner.RunAsync(game, package, parent.Process.Id, messages.Add).GetAwaiter().GetResult();
            Check(result != 0 && messages.Any(m => m.Contains("10 秒")), "超时没有报错");
            Check(!parent.Process.HasExited, "超时强制结束了进程"); Equal(game, "contents/a", "old"); Check(Directory.Exists(package), "失败清理解压目录");
            string log = ReadUpdateLog(game);
            SingleLogEvent(log, "Update failed");
            Check(log.Contains("stage=Waiting") && log.Contains("did not exit within 10 seconds"), "等待超时诊断丢失");
            NoUpdateUiInLog(log);
        });
        Test("启动器等待可取消", _ =>
        {
            using var parent = new WaitingChild(); using var cancel = new CancellationTokenSource();
            var task = MediaUpdateRunner.WaitForParentAsync(parent.Process.Id, cancel.Token); cancel.Cancel();
            try { task.GetAwaiter().GetResult(); throw new Exception("取消未生效"); } catch (OperationCanceledException) { }
            Check(!parent.Process.HasExited, "取消终止了进程");
        });
        Test("校验后更新器只预演一次且不复验摘要", root =>
        {
            var (game, package) = Pack(root, Copy("source/a", "contents/a")); Put(package, "source/a", "verified"); Put(package, "说明.txt", "readme"); Seal(package);
            Verify(game, package); Put(package, "source/a", "current");
            using var checksumLock = File.Open(Path.Combine(package, "checksums"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var readmeLock = File.Open(Path.Combine(package, "说明.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var messages = new List<string>(); using var engine = MediaUpdateEngine.Prepare(game, package, messages.Add); engine.Apply();
            Equal(game, "contents/a", "current"); Check(messages.Count(m => m == "正在预演更新...") == 1, "预演次数错误");
            Check(!messages.Any(m => m.StartsWith("正在校验更新包")), "更新器执行了包校验");
        });
        Test("安装失败保留目录且不尝试重启", root =>
        {
            var (game, package) = Pack(root, Copy("source/a", "contents/a"), Copy("source/b", "contents/b"));
            foreach (string name in new[] { "a", "b" }) { Put(package, "source/" + name, "new"); Put(game, "contents/" + name, "old"); }
            Seal(package); Verify(game, package);
            using var parent = new WaitingChild(); int pid = parent.Process.Id; parent.Exit();
            string target = Path.Combine(game, "contents/b"); var messages = new List<string>();
            var snapshots = new List<MediaUpdateProgress>();
            try
            {
                int result = MediaUpdateRunner.RunAsync(game, package, pid, message =>
                {
                    messages.Add(message);
                    if (message.StartsWith("预演通过")) File.SetAttributes(target, FileAttributes.ReadOnly);
                }, progress: snapshots.Add).GetAwaiter().GetResult();
                Check(result != 0 && Directory.Exists(package), "失败没有保留目录");
                Check(!messages.Any(m => m.Contains("重新启动") || m.Contains("手动启动")), "失败仍尝试启动");
                Check(!messages.Contains("Update Successful!"), "安装失败显示成功提示");
                Check(snapshots[^1].Status == MediaUpdateStatus.Failed && snapshots[^1].Path == "contents/b"
                    && snapshots[^1].Completed == 1 && snapshots[^1].Total == 2, "安装失败快照丢失文件或进度");
                Equal(game, "contents/a", "new"); Equal(game, "contents/b", "old");
                string log = ReadUpdateLog(game);
                SingleLogEvent(log, "Installation failed");
                Check(!log.Contains("Update failed:") && log.Contains("path=contents/b") && log.Contains("completed=1 total=2"), "安装错误重复或丢失停止位置");
                NoUpdateUiInLog(log);
            }
            finally { File.SetAttributes(target, FileAttributes.Normal); }
        });
        Test("解压清理失败仍报告成功并尝试启动", root =>
        {
            var (game, package) = Pack(root, Copy("source/a", "contents/a")); Put(package, "source/a", "new"); Seal(package); Verify(game, package);
            using var parent = new WaitingChild(); int pid = parent.Process.Id; parent.Exit();
            using var held = File.Open(Path.Combine(package, "source/a"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var messages = new List<string>();
            int result = MediaUpdateRunner.RunAsync(game, package, pid, messages.Add).GetAwaiter().GetResult();
            Check(result == 0 && messages.Any(m => m.Contains("清理失败")) && messages.Any(m => m.Contains("正在重新启动")), "清理错误改变了成功结果");
            Equal(game, "contents/a", "new");
            string log = ReadUpdateLog(game);
            SingleLogEvent(log, "Cleanup failed"); SingleLogEvent(log, "Launcher restart failed");
            NoUpdateUiInLog(log);
        });
    }
}
