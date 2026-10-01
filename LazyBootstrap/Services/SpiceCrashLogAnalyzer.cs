using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.Services
{
    internal sealed class SpiceCrashLogAnalyzer
    {
        private const string SignalPrefix = "W:signal: exception raised:";
        private const string WrongDisplaySettingRuleId = "WrongDisplaySetting";
        private const string WrongDisplaySettingSignal = "WRONG_DISPLAY_SETTING";

        private static readonly IReadOnlyList<SpiceCrashErrorRule> ErrorRules =
        [
            new SpiceCrashErrorRule(
                WrongDisplaySettingRuleId,
                "错误的显示器分辨率/刷新率设置",
                "F:graphics: failed to update display settings"),
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
                "MissingDependencies",
                "程序无法找到关键依赖文件",
                "Win32 error 126"),
            new SpiceCrashErrorRule(
                "DllLoadFailure",
                "游戏主程序加载失败，疑似损坏或系统架构不匹配",
                "Win32 error 193"),
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
                if (rule?.Id == WrongDisplaySettingRuleId)
                {
                    signal = WrongDisplaySettingSignal;
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

        private sealed record SpiceCrashErrorRule(
            string Id,
            string ReasonText,
            params string[] Markers);
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
