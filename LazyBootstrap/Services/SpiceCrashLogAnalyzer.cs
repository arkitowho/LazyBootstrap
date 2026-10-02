using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.Services
{
    internal sealed class SpiceCrashLogAnalyzer
    {
        private const string SignalPrefix = "W:signal: exception raised:";
        private const string Win32ErrorPrefix = "Win32 error ";
        private const string WrongDisplaySettingRuleId = "WrongDisplaySetting";
        private const string WrongDisplaySettingSignal = "WRONG_DISPLAY_SETTING";
        private const string MissingDependenciesRuleId = "MissingDependencies";
        private const string MissingDependencySignal = "MISSING_DEPENDENCY";

        private static readonly IReadOnlyList<SpiceCrashErrorRule> ErrorRules =
        [
            new SpiceCrashErrorRule(
                WrongDisplaySettingRuleId,
                "错误的显示器分辨率/刷新率设置",
                "F:graphics: failed to update display settings") { Signal = WrongDisplaySettingSignal },
            new SpiceCrashErrorRule(
                "AudioInitFailure",
                "音频初始化失败",
                "W:SuperstepSound: Audiodevice is not available!!!",
                "W:dll_entry_init: Failed to boot Audio."),
            new SpiceCrashErrorRule(
                "IncompleteGameData",
                "游戏数据不完整",
                "CreateLayer() 指定したレイヤーは存在しません"),
            new SpiceCrashErrorRule(
                "BadPcbid",
                "PCBID格式不正确",
                "F:ea3: boot: bad pcbid."),
            new SpiceCrashErrorRule(
                MissingDependenciesRuleId,
                "程序无法找到关键依赖文件") { Signal = MissingDependencySignal, Win32ErrorCodes = [126, 1157] },
            new SpiceCrashErrorRule(
                "DllLoadFailure",
                "游戏主程序加载失败，疑似损坏或系统架构不匹配") { Win32ErrorCodes = [193] },
            new SpiceCrashErrorRule(
                "MissingFile",
                "找不到指定文件") { Signal = "FILE_NOT_FOUND", Win32ErrorCodes = [2] },
            new SpiceCrashErrorRule(
                "MissingPath",
                "找不到指定路径") { Signal = "PATH_NOT_FOUND", Win32ErrorCodes = [3] },
            new SpiceCrashErrorRule(
                "AccessDenied",
                "访问被拒绝，程序没有所需权限") { Signal = "ACCESS_DENIED", Win32ErrorCodes = [5] },
            new SpiceCrashErrorRule(
                "OutOfMemory",
                "系统可用内存不足，无法完成操作") { Signal = "OUT_OF_MEMORY", Win32ErrorCodes = [8, 14] },
            new SpiceCrashErrorRule(
                "FileInUse",
                "文件正被其他进程占用，无法访问") { Signal = "FILE_IN_USE", Win32ErrorCodes = [32] },
            new SpiceCrashErrorRule(
                "MissingProcedure",
                "依赖文件缺少所需函数入口，可能存在版本不匹配") { Signal = "MISSING_PROCEDURE", Win32ErrorCodes = [127] },
            new SpiceCrashErrorRule(
                "MachineTypeMismatch",
                "程序与当前系统的处理器架构不匹配") { Signal = "MACHINE_TYPE_MISMATCH", Win32ErrorCodes = [216] },
            new SpiceCrashErrorRule(
                "InvalidImageHash",
                "Windows 无法验证文件的数字签名") { Signal = "INVALID_IMAGE_HASH", Win32ErrorCodes = [577] },
            new SpiceCrashErrorRule(
                "DllInitFailure",
                "动态链接库初始化失败") { Signal = "DLL_INIT_FAILED", Win32ErrorCodes = [1114] },
            new SpiceCrashErrorRule(
                "BlockedByPolicy",
                "程序被系统组策略阻止运行") { Signal = "BLOCKED_BY_POLICY", Win32ErrorCodes = [1260] },
            new SpiceCrashErrorRule(
                "PagefileTooSmall",
                "系统分页文件太小，无法完成操作") { Signal = "PAGEFILE_TOO_SMALL", Win32ErrorCodes = [1455] },
            new SpiceCrashErrorRule(
                "SideBySideConfigurationError",
                "程序的并行配置不正确，请检查所需运行库") { Signal = "SIDE_BY_SIDE_CONFIGURATION_ERROR", Win32ErrorCodes = [14001] },
            new SpiceCrashErrorRule(
                "MissingAvsConfig",
                "无法加载 prop/avs-config.xml",
                "F:avs-core: failed to open config file"),
            new SpiceCrashErrorRule(
                "MissingEa3Config",
                "无法加载 prop/ea3-config.xml",
                "F:avs-ea3: no ea3 config file found in prop directory"),
            new SpiceCrashErrorRule(
                "MissingSoftId",
                "无法检测到软件信息",
                "W:avs-ea3: soft id (datecode) not found in prop XML files")
        ];

        private readonly LauncherPaths _paths;
        private readonly ILogger<SpiceCrashLogAnalyzer> _logger;

        public SpiceCrashLogAnalyzer(LauncherPaths paths, ILogger<SpiceCrashLogAnalyzer> logger)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<SpiceCrashDiagnostic> AnalyzeAsync()
        {
            string logPath = Path.Combine(_paths.GetContentsDirectoryPath(), "log.txt");

            try
            {
                if (!File.Exists(logPath))
                {
                    return new SpiceCrashDiagnostic(
                        SpiceCrashDiagnostic.UnknownSignal,
                        "未找到 log.txt，未能识别具体崩溃原因",
                        string.Empty,
                        string.Empty,
                        logPath,
                        readSucceeded: false);
                }

                string content = await File.ReadAllTextAsync(logPath, SpiceLogEncoding.ShiftJis);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return new SpiceCrashDiagnostic(
                        SpiceCrashDiagnostic.UnknownSignal,
                        "log.txt 为空，未能识别具体崩溃原因",
                        string.Empty,
                        string.Empty,
                        logPath,
                        readSucceeded: true);
                }

                string signal = ExtractSignal(content);
                var rule = FindMatchingRule(content, out string matchedLine);
                if (!string.IsNullOrWhiteSpace(rule?.Signal))
                {
                    signal = rule.Signal;
                }
                string reasonText = rule?.ReasonText ?? "未知";

                return new SpiceCrashDiagnostic(
                    signal,
                    reasonText,
                    rule?.Id ?? string.Empty,
                    matchedLine,
                    logPath,
                    readSucceeded: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to analyze spice2x crash log. LogPath={LogPath}", logPath);
                return new SpiceCrashDiagnostic(
                    SpiceCrashDiagnostic.UnknownSignal,
                    "读取 log.txt 失败，未能识别具体崩溃原因",
                    string.Empty,
                    string.Empty,
                    logPath,
                    readSucceeded: false);
            }
        }

        private static string ExtractSignal(string logContent)
        {
            string[] lines = logContent
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n');

            foreach (string line in lines)
            {
                int prefixIndex = line.IndexOf(SignalPrefix, StringComparison.Ordinal);
                if (prefixIndex < 0)
                {
                    continue;
                }

                string signal = line.Substring(prefixIndex + SignalPrefix.Length).Trim();
                if (!string.IsNullOrWhiteSpace(signal))
                {
                    return signal;
                }
            }

            return SpiceCrashDiagnostic.UnknownSignal;
        }

        private static SpiceCrashErrorRule FindMatchingRule(string logContent, out string matchedLine)
        {
            matchedLine = string.Empty;
            if (string.IsNullOrWhiteSpace(logContent))
            {
                return null;
            }

            string[] lines = logContent
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n');

            foreach (var rule in ErrorRules)
            {
                if (rule.Win32ErrorCodes.Length > 0)
                {
                    foreach (string line in lines)
                    {
                        if (ContainsWin32ErrorCode(line, rule.Win32ErrorCodes))
                        {
                            matchedLine = line.Trim();
                            return rule;
                        }
                    }
                }

                foreach (string marker in rule.Markers)
                {
                    if (string.IsNullOrWhiteSpace(marker))
                    {
                        continue;
                    }

                    foreach (string line in lines)
                    {
                        if (line.IndexOf(marker, StringComparison.Ordinal) >= 0)
                        {
                            matchedLine = line.Trim();
                            return rule;
                        }
                    }
                }
            }

            return null;
        }

        private static bool ContainsWin32ErrorCode(string line, int[] codes)
        {
            int searchStart = 0;
            while (searchStart < line.Length)
            {
                int prefixIndex = line.IndexOf(Win32ErrorPrefix, searchStart, StringComparison.Ordinal);
                if (prefixIndex < 0)
                {
                    return false;
                }

                int numberStart = prefixIndex + Win32ErrorPrefix.Length;
                while (numberStart < line.Length && char.IsWhiteSpace(line[numberStart]))
                {
                    numberStart++;
                }

                int numberEnd = numberStart;
                while (numberEnd < line.Length && char.IsAsciiDigit(line[numberEnd]))
                {
                    numberEnd++;
                }

                bool hasBoundary = numberEnd == line.Length
                    || (!char.IsLetterOrDigit(line[numberEnd]) && line[numberEnd] != '_');
                if (hasBoundary
                    && int.TryParse(line.AsSpan(numberStart, numberEnd - numberStart), NumberStyles.None, CultureInfo.InvariantCulture, out int code)
                    && Array.IndexOf(codes, code) >= 0)
                {
                    return true;
                }

                searchStart = numberEnd;
            }

            return false;
        }

        private sealed record SpiceCrashErrorRule(
            string Id,
            string ReasonText,
            params string[] Markers)
        {
            public string Signal { get; init; } = string.Empty;
            public int[] Win32ErrorCodes { get; init; } = [];
        }
    }

    internal sealed class SpiceCrashDiagnostic
    {
        internal const string UnknownSignal = "UNKNOWN_SIGNAL";

        internal SpiceCrashDiagnostic(
            string signal,
            string reasonText,
            string matchedRuleId,
            string matchedLine,
            string logPath,
            bool readSucceeded)
        {
            Signal = string.IsNullOrWhiteSpace(signal) ? UnknownSignal : signal.Trim();
            ReasonText = string.IsNullOrWhiteSpace(reasonText) ? "未识别具体崩溃原因" : reasonText.Trim();
            MatchedRuleId = matchedRuleId ?? string.Empty;
            MatchedLine = matchedLine ?? string.Empty;
            LogPath = logPath ?? string.Empty;
            ReadSucceeded = readSucceeded;
        }

        internal string Signal { get; }

        internal string ReasonText { get; }

        internal string MatchedRuleId { get; }

        internal string MatchedLine { get; }

        internal string LogPath { get; }

        internal bool ReadSucceeded { get; }
    }
}
