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
        Check("Win32 error 126 使用缺失依赖信号并覆盖原始异常信号", paths =>
        {
            const string marker = "Win32 error 126";
            foreach (string prefix in new[] { string.Empty, "W:signal: exception raised: EXCEPTION_ACCESS_VIOLATION\n", "W:signal: exception raised:   \n" })
                VerifyDiagnostic(Analyze(paths, prefix + marker), "MISSING_DEPENDENCY",
                    "程序无法找到关键依赖文件", "MissingDependencies", marker, true);
        }, ref failed);
        Check("缺失依赖信号不覆盖优先匹配的音频错误", paths =>
            VerifyDiagnostic(Analyze(paths, "Win32 error 126\nW:dll_entry_init: Failed to boot Audio.\nW:signal: exception raised: REAL_SIGNAL"),
                "REAL_SIGNAL", "音频初始化失败", "AudioInitFailure", "W:dll_entry_init: Failed to boot Audio.", true), ref failed);
        var win32Cases = new[]
        {
            (2, "FILE_NOT_FOUND", "MissingFile", "找不到指定文件"),
            (3, "PATH_NOT_FOUND", "MissingPath", "找不到指定路径"),
            (5, "ACCESS_DENIED", "AccessDenied", "访问被拒绝，程序没有所需权限"),
            (8, "OUT_OF_MEMORY", "OutOfMemory", "系统可用内存不足，无法完成操作"),
            (14, "OUT_OF_MEMORY", "OutOfMemory", "系统可用内存不足，无法完成操作"),
            (32, "FILE_IN_USE", "FileInUse", "文件正被其他进程占用，无法访问"),
            (127, "MISSING_PROCEDURE", "MissingProcedure", "依赖文件缺少所需函数入口，可能存在版本不匹配"),
            (216, "MACHINE_TYPE_MISMATCH", "MachineTypeMismatch", "程序与当前系统的处理器架构不匹配"),
            (577, "INVALID_IMAGE_HASH", "InvalidImageHash", "Windows 无法验证文件的数字签名"),
            (1114, "DLL_INIT_FAILED", "DllInitFailure", "动态链接库初始化失败"),
            (1157, "MISSING_DEPENDENCY", "MissingDependencies", "程序无法找到关键依赖文件"),
            (1260, "BLOCKED_BY_POLICY", "BlockedByPolicy", "程序被系统组策略阻止运行"),
            (1455, "PAGEFILE_TOO_SMALL", "PagefileTooSmall", "系统分页文件太小，无法完成操作"),
            (14001, "SIDE_BY_SIDE_CONFIGURATION_ERROR", "SideBySideConfigurationError", "程序的并行配置不正确，请检查所需运行库")
        };
        foreach (var (code, signal, id, reason) in win32Cases)
        {
            Check($"Win32 error {code} 提供中文原因和专用信号", paths =>
            {
                foreach (string prefix in new[] { string.Empty, "W:signal: exception raised: REAL_SIGNAL\n", "W:signal: exception raised:   \n" })
                {
                    string marker = "Win32 error " + code;
                    VerifyDiagnostic(Analyze(paths, prefix + marker), signal, reason, id, marker, true);
                }
                foreach (string newline in new[] { "\r\n", "\n", "\r" })
                {
                    string line = $"[2026/10/02 12:00:00] W:loader: Win32 error {code} (details)";
                    VerifyDiagnostic(Analyze(paths, "I:loader: loading" + newline + "  " + line + "  " + newline), signal, reason, id, line, true);
                }
            }, ref failed);
        }
        Check("Win32 错误码按完整数字匹配且未知编号保留原始信号", paths =>
        {
            foreach (string code in new[] { "20", "30", "50", "80", "140", "320", "12600", "1930", "11140", "11570", "140010", "14550", "2147483648", "126abc", "126_", "-126", "+126", "0x7e", string.Empty })
                VerifyDiagnostic(Analyze(paths, "Win32 error " + code + "\nW:signal: exception raised: REAL_SIGNAL"),
                    "REAL_SIGNAL", "未知", string.Empty, string.Empty, true);
        }, ref failed);
        Check("同一行首个编号未知或无效时仍可识别后续错误码", paths =>
        {
            foreach (string firstCode in new[] { "9999", "126abc", string.Empty })
            {
                string line = $"Win32 error {firstCode}; Win32 error 126";
                VerifyDiagnostic(Analyze(paths, line), "MISSING_DEPENDENCY", "程序无法找到关键依赖文件", "MissingDependencies", line, true);
            }
        }, ref failed);
        Check("错误码支持额外空白和前导零", paths =>
        {
            const string line = "Win32 error   00126: details";
            VerifyDiagnostic(Analyze(paths, line), "MISSING_DEPENDENCY", "程序无法找到关键依赖文件", "MissingDependencies", line, true);
        }, ref failed);
        Check("新增 Win32 规则不覆盖优先匹配的显示设置或音频错误", paths =>
        {
            foreach (var (code, _, _, _) in win32Cases)
            {
                VerifyDisplayFailure(paths, "Win32 error " + code + "\n" + DisplayMarker, DisplayMarker);
                VerifyDiagnostic(Analyze(paths, "Win32 error " + code + "\nW:dll_entry_init: Failed to boot Audio.\nW:signal: exception raised: REAL_SIGNAL"),
                    "REAL_SIGNAL", "音频初始化失败", "AudioInitFailure", "W:dll_entry_init: Failed to boot Audio.", true);
            }
        }, ref failed);
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
                    id == "MissingDependencies" ? "MISSING_DEPENDENCY" : "REAL_SIGNAL", reason, id, marker, true);
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
