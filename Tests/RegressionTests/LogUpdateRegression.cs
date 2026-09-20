using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LazyBootstrap.MediaUpdate;

internal static partial class UpdateRegression
{
    private static string ReadUpdateLog(string game) => File.ReadAllText(Path.Combine(game, ".media-update/update_log.txt"));

    private static void SingleLogEvent(string log, string name)
        => Check(log.Split('\n').Count(line => line.StartsWith(name + ":", StringComparison.Ordinal)) == 1,
            "日志事件缺失或重复：" + name);

    private static void NoUpdateUiInLog(string log)
    {
        foreach (string text in new[] { "正在安装 ", "正在等待启动器退出", "正在清理更新临时目录", "正在重新启动启动器",
                     "预演通过，所有文件均可安装", "更新安装完成", "预演失败，未进行安装", "更新已取消，未进行安装",
                     "安装未完成，部分文件可能已更新", "Update Successful!", "请手动启动", "按回车", "秒后重新启动", "请关闭启动器后重试" })
            Check(!log.Contains(text), "日志包含界面文案：" + text);
    }

    private static void RunLogTests()
    {
        Test("XML 完全一致时记录逐步跳过及单次文件跳过", root =>
        {
            var (game, package) = Pack(root,
                Edit("contents/中文.xml", Value("/config/value", "same"), Value("/config/@enabled", "true"),
                    new MediaXmlEdit { Action = "addAttribute", XPath = "/config", Name = "enabled", Value = "true" },
                    Fragment("replaceElement", "/config/value", "<value>same</value>"),
                    Fragment("appendChild", "/config", "<group><child/></group>"),
                    Fragment("insertBefore", "/config/anchor", "<before/>"),
                    Fragment("insertAfter", "/config/anchor", "<after/>")),
                Edit("contents/中文.xml", Value("/config/value", "same")));
            string original = "<config enabled='true'><value>same</value><before/><anchor/><after/><group><child/></group></config>";
            Put(game, "contents/中文.xml", original); Seal(package);
            var messages = new List<string>();
            using (var engine = MediaUpdateEngine.Prepare(game, package, messages.Add)) engine.Apply();
            string log = ReadUpdateLog(game);
            Check(log.Split('\n').Count(line => line.StartsWith("XML edit skipped:")) == 8, "逐步跳过缺失或重复");
            for (int edit = 1; edit <= 7; edit++)
                Check(log.Contains($"operation=1 path=contents/中文.xml edit={edit} action="), "跳过日志缺少编辑序号");
            Check(log.Contains("operation=2 path=contents/中文.xml edit=1 action=setValue xpath=/config/value reason=already-identical"), "跳过日志缺少动作、XPath 或原因");
            SingleLogEvent(log, "XML file skipped");
            Check(log.Contains("XML file skipped: phase=preflight path=contents/中文.xml reason=no-changes"), "文件跳过缺少路径或原因");
            Check(log.Contains("completed=0 total=0") && !log.Contains("Installing "), "跳过的文件仍被计为写入");
            Check(messages.All(message => !message.Contains("XML edit skipped") && !message.Contains("XML file skipped")), "内部日志混入界面回调");
            Equal(game, "contents/中文.xml", original); NoUpdateUiInLog(log);
        });

        foreach (string other in new[] { "copy-before", "copy-after", "xml-after", "delete-after" })
        Test("XML 步骤跳过不误报整个文件跳过：" + other, root =>
        {
            var skip = Edit("contents/config.xml", Value("/config/@value", "same"));
            var copy = Copy("source/config.xml", "contents/config.xml");
            var operations = other switch
            {
                "copy-before" => new[] { copy, skip },
                "copy-after" => new[] { skip, copy },
                "xml-after" => new[] { Edit("contents/config.xml", Value("/config/@value", "same"), Value("/config/@value", "new")) },
                _ => new[] { skip, new MediaUpdateOperation { Type = "delete", Target = "contents/config.xml" } }
            };
            var (game, package) = Pack(root, operations);
            Put(game, "contents/config.xml", "<config value='same'/>");
            Put(package, "source/config.xml", "<config value='same' copied='true'/>"); Seal(package);
            using (var engine = MediaUpdateEngine.Prepare(game, package)) engine.Apply();
            string log = ReadUpdateLog(game);
            SingleLogEvent(log, "XML edit skipped");
            Check(!log.Contains("XML file skipped:"), "文件还有其他变更却记录了整文件跳过");
            Check(log.Contains("completed=1 total=1"), "其他变更未正常安装");
            NoUpdateUiInLog(log);
        });

        Test("正常日志仅含内部步骤且逐项安装不重复", root =>
        {
            var (game, package) = Pack(root, Copy("source/中文.bin", "contents/中文.bin"));
            Put(package, "source/中文.bin", "new"); Seal(package);
            var messages = new List<string>();
            Verify(game, package, messages.Add);
            using (var engine = MediaUpdateEngine.Prepare(game, package, messages.Add)) engine.Apply();
            string log = ReadUpdateLog(game);
            Check(log.Contains("Package SHA256 verification completed."), "安装没有追加到校验日志");
            SingleLogEvent(log, "Preflight started"); SingleLogEvent(log, "Preflight completed");
            SingleLogEvent(log, "Installation completed");
            Check(log.Split('\n').Count(line => line.StartsWith("Installing ")) == 1, "逐项安装记录重复");
            Check(log.Contains("Write contents/中文.bin") && log.Contains("completed=1 total=1"), "操作或完成数量丢失");
            Check(messages.Contains("更新安装完成。") && messages.All(m => !log.Contains(m)), "界面回调未与日志分离");
            NoUpdateUiInLog(log);
        });

        foreach (string failure in new[] { "manifest", "xml", "locked-source" })
        Test("预演失败只记录一次且保留原始详情：" + failure, root =>
        {
            var (game, package) = Pack(root, failure == "xml"
                ? Edit("contents/中文.xml", Value("/不存在", "x")) : Copy("source/中文.bin", "contents/中文.bin"));
            Put(package, "source/中文.bin", "new"); Put(game, "contents/中文.xml", "<config/>");
            if (failure == "manifest") Put(package, "update", "{ invalid");
            using var held = failure == "locked-source"
                ? File.Open(Path.Combine(package, "source/中文.bin"), FileMode.Open, FileAccess.Read, FileShare.None) : null;
            using var parent = new WaitingChild(); int pid = parent.Process.Id; parent.Exit();
            Check(MediaUpdateRunner.RunAsync(game, package, pid, null).GetAwaiter().GetResult() == 1, "预演错误没有失败");
            string log = ReadUpdateLog(game);
            SingleLogEvent(log, "Preflight started"); SingleLogEvent(log, "Preflight failed");
            Check(!log.Contains("Update failed:") && !log.Contains("Installation started:"), "Runner 重复记录错误或错误后安装");
            Check(log.Contains("type=") && log.Contains("hresult="), "缺少原始错误类型或代码");
            if (failure == "xml") Check(log.Contains("contents/中文.xml") && log.Contains("XPath「/不存在」") && log.Contains("编辑 1"), "XML 诊断详情丢失");
            if (failure == "locked-source") Check(log.Contains("中文.bin") && log.Contains("win32=32"), "文件占用诊断丢失");
            NoUpdateUiInLog(log);
        });

        Test("校验失败与取消记录内部结果且不记录界面包装", root =>
        {
            var (game, package) = Pack(root, Copy("source/a", "contents/a"));
            Put(package, "source/a", "before"); Seal(package); Put(package, "source/a", "changed");
            Reject(() => Verify(game, package));
            string log = ReadUpdateLog(game);
            SingleLogEvent(log, "Package verification failed");
            Check(log.Contains("SHA256 不符：source/a") && !log.Contains("更新包疑似被修改或损坏"), "校验具体原因丢失或记录了界面包装");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { Verify(game, package, cancel: cancel.Token); throw new Exception("校验未取消"); }
            catch (OperationCanceledException) { }
            log = ReadUpdateLog(game);
            SingleLogEvent(log, "Package verification cancelled"); NoUpdateUiInLog(log);
        });

        foreach (bool installing in new[] { false, true })
        Test("取消日志仅记录一次并保留完成数：" + (installing ? "安装" : "预演"), root =>
        {
            var (game, package) = Pack(root, Copy("source/a", "contents/a"), Copy("source/b", "contents/b"));
            Put(package, "source/a", "new"); Put(package, "source/b", "new");
            using var cancel = new CancellationTokenSource();
            using var parent = new WaitingChild(); int pid = parent.Process.Id; parent.Exit();
            var states = new List<MediaUpdateProgress>();
            int result = MediaUpdateRunner.RunAsync(game, package, pid, null, cancel.Token, state =>
            {
                states.Add(state);
                if (installing ? state.Stage == MediaUpdateStage.Installing && state.Completed == 1
                    : state.Stage == MediaUpdateStage.Preflight) cancel.Cancel();
            }).GetAwaiter().GetResult();
            Check(result == 1 && states[^1].Status == MediaUpdateStatus.Cancelled, "取消状态错误");
            string log = ReadUpdateLog(game);
            SingleLogEvent(log, installing ? "Installation cancelled" : "Preflight cancelled");
            Check(!log.Contains("Update cancelled:") && !log.Contains("Update failed:"), "Runner 重复记录取消");
            Check(log.Contains(installing ? "completed=1 total=2" : "completed=0 installationStarted=false"), "取消日志丢失完成数");
            Check(!File.Exists(Path.Combine(game, "contents/b")), "取消后继续安装");
            NoUpdateUiInLog(log);
        });
    }
}
