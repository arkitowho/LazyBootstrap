using System;
using System.IO;
using System.Linq;
using LazyBootstrap.FileSystem;
using LazyBootstrap.Services;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SpiceCrashRegression
{
    private const string DisplayMarker = "F:graphics: failed to update display settings";

    internal static int RunAll()
    {
        int failed = 0;
        Check("显示设置错误在没有异常信号时使用自定义信号", paths =>
            VerifyDisplayFailure(paths, DisplayMarker, DisplayMarker), ref failed);
        Check("显示设置错误覆盖日志中的真实异常信号", paths =>
            VerifyDisplayFailure(paths, "W:signal: exception raised: EXCEPTION_ACCESS_VIOLATION\n" + DisplayMarker, DisplayMarker), ref failed);
        Check("显示设置错误覆盖空异常信号", paths =>
            VerifyDisplayFailure(paths, "W:signal: exception raised:   \n" + DisplayMarker, DisplayMarker), ref failed);
        Check("显示设置错误优先于其他崩溃规则", paths =>
            VerifyDisplayFailure(paths, "W:SuperstepSound: Audiodevice is not available!!!\nWin32 error 126\n" + DisplayMarker, DisplayMarker), ref failed);
        Check("显示设置错误支持时间戳附加信息及不同换行", paths =>
        {
            string line = "[2026/10/02 12:00:00] " + DisplayMarker + " (1920x1080@75)";
            foreach (string newline in new[] { "\r\n", "\n", "\r" })
                VerifyDisplayFailure(paths, "I:graphics: initializing" + newline + "  " + line + "  " + newline, line);
        }, ref failed);
        Check("普通图形日志和大小写不同的标记不会误报", paths =>
        {
            foreach (string content in new[] { "I:graphics: display settings updated", "F:graphics: failed to initialize graphics", DisplayMarker.ToUpperInvariant() })
                VerifyDiagnostic(Analyze(paths, content), "UNKNOWN_SIGNAL", "未知", string.Empty, string.Empty, true);
        }, ref failed);
        Check("未匹配错误时保留原始异常信号", paths =>
            VerifyDiagnostic(Analyze(paths, "W:signal: exception raised: EXCEPTION_ACCESS_VIOLATION"),
                "EXCEPTION_ACCESS_VIOLATION", "未知", string.Empty, string.Empty, true), ref failed);
        Check("原有崩溃规则及日文日志编码保持兼容", paths =>
        {
            var cases = new[]
            {
                ("AudioInitFailure", "音频初始化失败", "W:SuperstepSound: Audiodevice is not available!!!"),
                ("AudioInitFailure", "音频初始化失败", "W:dll_entry_init: Failed to boot Audio."),
                ("IncompleteGameData", "游戏数据不完整", "CreateLayer() 指定したレイヤーは存在しません"),
                ("BadPcbid", "PCBID格式不正确", "F:ea3: boot: bad pcbid."),
                ("MissingDependencies", "程序无法找到关键依赖文件", "Win32 error 126"),
                ("DllLoadFailure", "游戏主程序加载失败，疑似损坏或系统架构不匹配", "Win32 error 193"),
                ("MissingAvsConfig", "无法加载 prop/avs-config.xml", "F:avs-core: failed to open config file"),
                ("MissingEa3Config", "无法加载 prop/ea3-config.xml", "F:avs-ea3: no ea3 config file found in prop directory"),
                ("MissingSoftId", "无法检测到软件信息", "W:avs-ea3: soft id (datecode) not found in prop XML files")
            };
            foreach (var (id, reason, marker) in cases)
                VerifyDiagnostic(Analyze(paths, marker + "\nW:signal: exception raised: REAL_SIGNAL"),
                    "REAL_SIGNAL", reason, id, marker, true);
        }, ref failed);
        Check("空日志不会误报显示设置错误", paths =>
            VerifyDiagnostic(Analyze(paths, "  \r\n"), "UNKNOWN_SIGNAL", "log.txt 为空，未能识别具体崩溃原因", string.Empty, string.Empty, true), ref failed);
        Check("缺失日志不会误报显示设置错误", paths =>
            VerifyDiagnostic(CreateAnalyzer(paths).AnalyzeAsync().GetAwaiter().GetResult(),
                "UNKNOWN_SIGNAL", "未找到 log.txt，未能识别具体崩溃原因", string.Empty, string.Empty, false), ref failed);
        Check("日志读取失败不会误报显示设置错误", paths =>
        {
            string logPath = Path.Combine(paths.GetContentsDirectoryPath(), "log.txt");
            File.WriteAllText(logPath, DisplayMarker, SpiceLogEncoding.ShiftJis);
            using var locked = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            VerifyDiagnostic(CreateAnalyzer(paths).AnalyzeAsync().GetAwaiter().GetResult(),
                "UNKNOWN_SIGNAL", "读取 log.txt 失败，未能识别具体崩溃原因", string.Empty, string.Empty, false);
        }, ref failed);
        return failed;
    }

    private static void VerifyDisplayFailure(LauncherPaths paths, string content, string matchedLine) =>
        VerifyDiagnostic(Analyze(paths, content), "WRONG_DISPLAY_SETTING", "错误的显示器分辨率/刷新率设置",
            "WrongDisplaySetting", matchedLine, true);

    private static SpiceCrashLogAnalyzer CreateAnalyzer(LauncherPaths paths) =>
        new(paths, NullLogger<SpiceCrashLogAnalyzer>.Instance);

    private static SpiceCrashDiagnostic Analyze(LauncherPaths paths, string content)
    {
        string logPath = Path.Combine(paths.GetContentsDirectoryPath(), "log.txt");
        File.WriteAllText(logPath, content, SpiceLogEncoding.ShiftJis);
        byte[] original = File.ReadAllBytes(logPath);
        var diagnostic = CreateAnalyzer(paths).AnalyzeAsync().GetAwaiter().GetResult();
        Assert(diagnostic.LogPath == logPath, "诊断日志路径错误");
        Assert(original.SequenceEqual(File.ReadAllBytes(logPath)), "诊断修改了原始日志");
        return diagnostic;
    }

    private static void VerifyDiagnostic(SpiceCrashDiagnostic diagnostic, string signal, string reason, string rule, string matchedLine, bool readSucceeded)
    {
        Assert(diagnostic.Signal == signal, $"信号错误：{diagnostic.Signal}");
        Assert(diagnostic.ReasonText == reason, $"崩溃原因错误：{diagnostic.ReasonText}");
        Assert(diagnostic.MatchedRuleId == rule, $"匹配规则错误：{diagnostic.MatchedRuleId}");
        Assert(diagnostic.MatchedLine == matchedLine, $"匹配日志行错误：{diagnostic.MatchedLine}");
        Assert(diagnostic.ReadSucceeded == readSucceeded, "日志读取状态错误");
    }

    private static void Check(string name, Action<LauncherPaths> test, ref int failed)
    {
        string temp = Path.GetFullPath(Path.GetTempPath());
        string root = Path.GetFullPath(Path.Combine(temp, "LazyBootstrap-crash-regression-" + Guid.NewGuid().ToString("N")));
        Assert(DirectorySafety.IsWithin(root, temp), "测试路径越界");
        var paths = new LauncherPaths(root, Path.Combine(root, "launcher"), Path.Combine(root, "launcher", "config.toml"));
        Directory.CreateDirectory(paths.GetContentsDirectoryPath());
        try
        {
            test(paths);
            Console.WriteLine("通过: " + name);
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine("失败: " + name + " — " + ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
