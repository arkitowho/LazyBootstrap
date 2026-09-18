using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LazyBootstrap.MediaUpdate;
using LazyBootstrap.MediaUpdater;

internal static partial class UpdateRegression
{
    private static string FrameText(DashboardFrame frame) => string.Join("\n", frame.Lines.Select(row => string.Concat(row.Select(s => s.Text))));

    private static void RunDashboardTests()
    {
        Test("居中卡片保留留白，正常与成功页隐藏辅助路径", _ =>
        {
            var running = new MediaUpdateProgress(MediaUpdateStage.Installing, "安装中")
            { Path = "contents/example.bin", Operation = MediaUpdateAction.Write, Completed = 6, Total = 10 };
            foreach (var (width, height) in new[] { (80, 24), (120, 40), (60, 18) })
            {
                var frame = MediaUpdateDashboard.Build(running, "GAME_SENTINEL", "LOG_SENTINEL", TimeSpan.Zero, width, height, 0, 0);
                string[] rows = frame.Lines.Select(row => string.Concat(row.Select(s => s.Text))).ToArray();
                int cardWidth = Math.Min(72, width - 4);
                int top = (height - 15) / 2, left = (width - cardWidth) / 2;
                Check(rows[top].IndexOf('┌') == left && rows[top + 14].IndexOf('└') == left, "卡片未居中或尺寸错误");
                Check(rows.Take(top).Concat(rows.Skip(top + 15)).All(string.IsNullOrEmpty), "卡片上下没有留白");
                Check(!FrameText(frame).Contains("SENTINEL"), "正常更新显示了辅助路径");
                Check(FrameText(frame).Contains("Media Updater") && !FrameText(frame).Contains("LazyBootstrap"), "标题未更新");
                Check(frame.Lines.SelectMany(r => r).Any(s => s.Text == "等待启动器" && s.Color == ConsoleColor.Gray), "已完成阶段未使用灰色");
                var done = running with { Stage = MediaUpdateStage.Completed, Status = MediaUpdateStatus.Succeeded,
                    Message = MediaUpdateRunner.SuccessMessage, RemainingSeconds = 5, Completed = 10 };
                var success = MediaUpdateDashboard.Build(done, "GAME_SENTINEL", "LOG_SENTINEL", TimeSpan.Zero, width, height, 0, 0);
                string text = FrameText(success);
                Check(!text.Contains("SENTINEL") && !text.Contains("example.bin") && !text.Contains('█') && !text.Contains("预演检查"), "成功页残留更新信息");
                Check(success.Lines[top + 3].All(s => !s.Text.Contains("等待启动器")), "成功页阶段导航未清除");
                var warning = done with { Warning = "清理失败", RequiresAcknowledgement = true };
                var error = MediaUpdateDashboard.Build(warning, "GAME_SENTINEL", "LOG_SENTINEL", TimeSpan.Zero, width, height, 0, 0);
                Check(FrameText(error).Contains("GAME_SENTINEL") && FrameText(error).Contains("LOG_SENTINEL"), "异常卡片缺少辅助路径");
                Check(FrameText(error).Split("GAME_SENTINEL").Length == 2 && FrameText(error).Split("LOG_SENTINEL").Length == 2, "异常卡片重复显示辅助路径");
                Check(error.Lines.SelectMany(r => r).Any(s => s.Text.Contains("清理失败") && s.Color == ConsoleColor.Yellow), "警告未使用黄色");
            }
        });
        Test("仪表盘中文长路径、控制字符及不同尺寸不越界", _ =>
        {
            const string path = "contents/中文目录/非常长的子目录/日本語/e\u0301/文件.bin";
            Check(MediaUpdateDashboard.Width("中a文") == 5 && MediaUpdateDashboard.Width("e\u0301") == 1, "字符宽度不正确");
            Check(MediaUpdateDashboard.Ellipsize(path, 24).EndsWith("文件.bin"), "省略路径未保留文件名");
            var state = new MediaUpdateProgress(MediaUpdateStage.Installing, "安装中\u001b[31m\t\r\n")
            { Path = path + "\u202e\a", Operation = MediaUpdateAction.Write, Completed = 62, Total = 100 };
            foreach (var (width, height) in new[] { (100, 28), (60, 18), (59, 17), (32, 10), (15, 5), (1, 1) })
            {
                var frame = MediaUpdateDashboard.Build(state, "D:/游戏", "D:/日志", TimeSpan.FromSeconds(18), width, height, 1, 0);
                Check(frame.Lines.Length == height, "帧高度错误");
                foreach (var row in frame.Lines)
                {
                    string text = string.Concat(row.Select(s => s.Text));
                    Check(MediaUpdateDashboard.Width(text) <= width && !text.Any(char.IsControl) && !text.Contains('\u202e'), "帧越界或包含控制字符");
                }
                if (width >= 60) Check(FrameText(frame).Contains("62%") && FrameText(frame).Contains("已完成 62 / 100"), "安装统计错误");
            }
        });
        Test("等待预演不伪造百分比，成功使用绿色并显示倒计时", _ =>
        {
            foreach (var stage in new[] { MediaUpdateStage.Waiting, MediaUpdateStage.Preflight })
            {
                var state = new MediaUpdateProgress(stage, "处理中");
                var frame = MediaUpdateDashboard.Build(state, "game", "log", TimeSpan.Zero, 80, 24, 1, 0);
                Check(!FrameText(frame).Contains('%'), "预演显示虚构进度");
                string[] animation = [".", "..", "...", "....", ".....", "."];
                for (int tick = 0; tick < animation.Length; tick++)
                {
                    var animated = MediaUpdateDashboard.Build(state, "game", "log", TimeSpan.Zero, 80, 24, tick, 0);
                    Check(animated.Lines.SelectMany(row => row).Any(span => span.Text == "处理中 " + animation[tick]), "等待动画应逐帧切换一至五个英文句点");
                }
            }
            var done = new MediaUpdateProgress(MediaUpdateStage.Completed, MediaUpdateRunner.SuccessMessage)
            { Status = MediaUpdateStatus.Succeeded, Completed = 10, Total = 10, RemainingSeconds = 5 };
            var success = MediaUpdateDashboard.Build(done, "game", "log", TimeSpan.Zero, 80, 24, 1, 0);
            Check(success.Lines.SelectMany(r => r).Any(s => s.Text == "Update Successful!" && s.Color == ConsoleColor.Green), "成功提示不是绿色");
            Check(FrameText(success).Contains("5 秒后"), "缺少重启倒计时");
        });
        Test("异常正文省略重复标题并保留具体原因", _ =>
        {
            var cases = new[]
            {
                (MediaUpdateStage.Preflight, MediaUpdateStatus.Cancelled, "更新已取消，未进行安装"),
                (MediaUpdateStage.Installing, MediaUpdateStatus.Cancelled, "安装未完成，部分文件可能已更新"),
                (MediaUpdateStage.Installing, MediaUpdateStatus.Failed, "安装未完成，部分文件可能已更新"),
                (MediaUpdateStage.Preflight, MediaUpdateStatus.Failed, "预演失败，未进行安装")
            };
            foreach (var (stage, status, title) in cases)
            foreach (string message in new[] { title, title + "。文件被占用", "A completely different localized log message" })
            foreach (int height in new[] { 24, 17 })
            {
                var state = new MediaUpdateProgress(stage, message)
                {
                    Status = status, Path = "contents/example.bin", Detail = "文件被占用", RequiresAcknowledgement = true
                };
                string text = FrameText(MediaUpdateDashboard.Build(state, "GAME", "LOG", TimeSpan.Zero, 80, height, 0, 0));
                Check(text.Split(title).Length == 2, "状态标题应仅出现一次");
                Check(text.Contains("contents/example.bin"), "失败路径必须保留");
                Check(text.Split("文件被占用").Length == 2, "具体原因必须只显示一次");
                Check(!text.Contains("localized"), "界面依赖完整日志文案");
                Check(state.Message == message, "呈现不应修改原始错误信息");
            }
        });

        Test("错误详情可完整滚动且保持失败颜色", _ =>
        {
            string reason = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"第 {i} 行错误详情：中文路径无法写入"));
            var state = new MediaUpdateProgress(MediaUpdateStage.Installing, reason)
            { Status = MediaUpdateStatus.Failed, Detail = reason, RequiresAcknowledgement = true, Path = "contents/文件.bin", Completed = 1, Total = 3 };
            var first = MediaUpdateDashboard.Build(state, "game", "log", TimeSpan.Zero, 80, 24, 1, 0);
            var last = MediaUpdateDashboard.Build(state, "game", "log", TimeSpan.Zero, 80, 24, 1, int.MaxValue);
            Check(last.MaximumDetailOffset > 0 && last.DetailOffset == last.MaximumDetailOffset, "详情不能滚动到结尾");
            Check(!FrameText(first).Contains("第 40 行") && FrameText(last).Contains("第 40 行"), "长详情被截断或滚动无效");
            Check(last.Lines.SelectMany(r => r).Any(s => s.Text.Contains("第 40 行") && s.Color == ConsoleColor.Red), "错误未使用红色");
            Check(FrameText(last).Contains("部分文件可能已更新"), "失败提示丢失部分更新语义");
        });
        Test("XML 预演错误分开提供目标与原因且界面只显示一次路径", root =>
        {
            var (game, package) = Pack(root, Edit("contents/config.xml", Value("/missing", "x")));
            Put(game, "contents/config.xml", "<config/>");
            var progress = new List<MediaUpdateProgress>();
            Reject(() => MediaUpdateEngine.Prepare(game, package, progress: progress.Add));
            var state = progress[^1];
            Check(state.Status == MediaUpdateStatus.Failed && state.Path == "contents/config.xml", "XML 错误没有结构化路径");
            Check(state.Detail.Contains("XPath") && !state.Detail.Contains(state.Path), "XML 原因仍混有路径");
            Check(state.Message.Contains(state.Path) && state.Message.Contains("预演失败"), "完整文本错误信息丢失");
            string frame = FrameText(MediaUpdateDashboard.Build(state, "game", "log", TimeSpan.Zero, 100, 28, 0, 0));
            Check(frame.Split(state.Path).Length == 2, "XML 失败路径重复显示");
            Equal(game, "contents/config.xml", "<config/>");
        });
        foreach (string access in new[] { "readonly", "locked-source" })
        Test("访问错误分开提供受影响路径与原因：" + access, root =>
        {
            var (game, package) = Pack(root, Copy("source/file.bin", "contents/file.bin"));
            Put(game, "contents/file.bin", "old"); Put(package, "source/file.bin", "new");
            string affected = access == "readonly" ? Path.Combine(game, "contents/file.bin") : Path.Combine(package, "source/file.bin");
            using var held = access == "locked-source" ? File.Open(affected, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            try
            {
                if (access == "readonly") File.SetAttributes(affected, FileAttributes.ReadOnly);
                var progress = new List<MediaUpdateProgress>();
                Reject(() => MediaUpdateEngine.Prepare(game, package, progress: progress.Add));
                var state = progress[^1];
                Check(access == "readonly" ? state.Path == "contents/file.bin"
                    : Path.GetFullPath(state.Path) == Path.GetFullPath(affected), "未报告实际受影响的文件：" + state.Path);
                Check(!state.Detail.Contains("file.bin") && state.Message.Contains("file.bin"), "路径未与具体原因分开");
                Equal(game, "contents/file.bin", "old");
            }
            finally { if (access == "readonly") File.SetAttributes(affected, FileAttributes.Normal); }
        });
        Test("安装系统错误使用错误码提供原因并保留完整日志消息", root =>
        {
            var (game, package) = Pack(root, Copy("source/a.bin", "contents/a.bin"));
            Put(game, "contents/a.bin", "old"); Put(package, "source/a.bin", "new");
            var progress = new List<MediaUpdateProgress>();
            using var engine = MediaUpdateEngine.Prepare(game, package, progress: progress.Add);
            using var held = File.Open(Path.Combine(game, "contents/a.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
            Reject(() => engine.Apply());
            var state = progress[^1];
            Check(state.Status == MediaUpdateStatus.Failed && state.Path == "contents/a.bin", "安装失败没有目标路径");
            Check(state.Detail.Contains("Windows 错误") && !state.Detail.Contains("a.bin"), "系统错误原因重复了路径");
            Check(state.Message.Contains("a.bin") && state.Message.Contains("部分文件可能已更新"), "完整失败消息丢失");
        });
        Test("安装快照准确计数且更新器延迟替换仍在最后", root =>
        {
            var (game, package) = Pack(root, Delete("contents/old"), Copy("source/a", "contents/new/a"),
                Copy("source/updater", "launcher/MediaUpdater.exe"));
            Put(game, "contents/old", "old"); Put(game, "launcher/MediaUpdater.exe", "old-updater");
            Put(package, "source/a", "new"); Put(package, "source/updater", "new-updater");
            var snapshots = new List<MediaUpdateProgress>();
            using var engine = MediaUpdateEngine.Prepare(game, package, progress: snapshots.Add);
            Check(File.ReadAllText(Path.Combine(game, "contents/old")) == "old", "预演修改了文件");
            engine.Apply();
            var installation = snapshots.Where(p => p.Stage == MediaUpdateStage.Installing).ToArray();
            Check(installation[0].Completed == 0 && installation[0].Total == 4, "初始进度未包含全部变更");
            Check(installation.Select(p => p.Completed).Distinct().SequenceEqual(new[] { 0, 1, 2, 3, 4 }), "完成数跳变");
            Check(installation[^1].Path == "launcher/MediaUpdater.exe.pending" && installation[^1].Completed == 4, "延迟替换不是最后一项");
            Check(installation.Any(p => p.Operation == MediaUpdateAction.Write && p.Path == "contents/new/a" && p.Completed == 2), "未在文件写入前报告路径");
        });
        Test("取消快照保留已完成数且不写下一项", root =>
        {
            var (game, package) = Pack(root, Copy("source/a", "contents/a"), Copy("source/b", "contents/b"));
            Put(game, "contents/a", "old"); Put(game, "contents/b", "old"); Put(package, "source/a", "new"); Put(package, "source/b", "new");
            using var cancel = new CancellationTokenSource();
            var snapshots = new List<MediaUpdateProgress>();
            using var engine = MediaUpdateEngine.Prepare(game, package, progress: state =>
            {
                snapshots.Add(state);
                if (state.Stage == MediaUpdateStage.Installing && state.Completed == 1) cancel.Cancel();
            });
            Reject(() => engine.Apply(cancel.Token));
            Check(snapshots[^1].Status == MediaUpdateStatus.Cancelled && snapshots[^1].Completed == 1, "取消状态或进度错误");
            Equal(game, "contents/a", "new"); Equal(game, "contents/b", "old");
        });
        Test("零变更和进度观察器异常不改变安装结果", root =>
        {
            var (game, package) = Pack(root, Delete("contents/missing"));
            var snapshots = new List<MediaUpdateProgress>();
            using var engine = MediaUpdateEngine.Prepare(game, package, progress: state => { snapshots.Add(state); throw new IOException("renderer failed"); });
            engine.Apply();
            Check(snapshots.Any(p => p.Stage == MediaUpdateStage.Installing && p.Total == 0 && p.Message == "无需修改"), "零变更未显示正确状态");
            var done = new MediaUpdateProgress(MediaUpdateStage.Completed, MediaUpdateRunner.SuccessMessage) { Status = MediaUpdateStatus.Succeeded };
            Check(FrameText(MediaUpdateDashboard.Build(done, "game", "log", TimeSpan.Zero, 80, 24, 0, 0)).Contains("无需修改"), "成功页未保留零变更说明");
        });
        Test("预演失败与等待取消均产生明确终态且不安装", root =>
        {
            var (game, package) = Pack(root, Copy("source/a", "contents/a"));
            Put(game, "contents/a", "old"); Put(package, "source/a", "new");
            using var parent = new WaitingChild();
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            var snapshots = new List<MediaUpdateProgress>();
            int cancelled = MediaUpdateRunner.RunAsync(game, package, parent.Process.Id, null, cancel.Token, snapshots.Add).GetAwaiter().GetResult();
            Check(cancelled == 1 && snapshots[^1].Status == MediaUpdateStatus.Cancelled, "等待取消终态错误");
            parent.Exit(); snapshots.Clear();
            using var held = File.Open(Path.Combine(game, "contents/a"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            int failed = MediaUpdateRunner.RunAsync(game, package, parent.Process.Id, null, progress: snapshots.Add).GetAwaiter().GetResult();
            Check(failed == 1 && snapshots[^1].Stage == MediaUpdateStage.Preflight && snapshots[^1].Status == MediaUpdateStatus.Failed
                && snapshots[^1].Path == "contents/a" && !snapshots.Any(s => s.Stage == MediaUpdateStage.Installing), "预演失败状态或路径错误");
            held.Dispose(); Equal(game, "contents/a", "old");
        });
    }
}
