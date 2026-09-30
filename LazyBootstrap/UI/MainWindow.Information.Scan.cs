using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using LazyBootstrap.Services;

namespace LazyBootstrap.UI
{

    public partial class MainWindow
    {
        private static readonly List<EnvironmentScanner.EnvironmentCheckResult> EmptyScanBucket = [];
        private Task InitializeInfoAsync(EnvironmentScanViewState viewState)
        {
            ArgumentNullException.ThrowIfNull(viewState);
            _logger.LogInformation("Environment information initialization started.");

            viewState.MachineProperty = ResolveMachineProperty();
            viewState.GameVersion = ResolveCurrentGameVersion();
            viewState.OperatingSystemVersionName = ResolveOperatingSystemVersionName();
            viewState.OperatingSystemBuildNumber = ResolveOperatingSystemBuildNumber();
            viewState.LauncherVersion = ResolveLauncherVersion();
            _logger.LogInformation("Environment information initialization completed.");
            return Task.CompletedTask;
        }

        private async Task RunScanAsync(EnvironmentScanViewState viewState)
        {
            ArgumentNullException.ThrowIfNull(viewState);
            _logger.LogInformation("Environment scan started.");
            var stopwatch = Stopwatch.StartNew();

            viewState.HasEnvironmentScanErrors = false;
            ResetScanViewState(viewState);

            try
            {
                var summary = await EnvironmentScanner.RunAsync(
                    (_, _) => { },
                    _paths.GetContentsDirectoryPath(),
                    _paths.GetBundledLibsDirectoryPath());

                viewState.HasEnvironmentScanErrors = summary.HadError;
                PopulateScanSlots(viewState, summary);
                stopwatch.Stop();
                _logger.LogInformation(
                    "Environment scan completed. HadError={HadError}, ItemCount={ItemCount}, ElapsedMs={ElapsedMs}",
                    summary.HadError,
                    summary.Items.Count,
                    stopwatch.ElapsedMilliseconds);
                if (summary.HadError)
                {
                    _logger.LogWarning("Environment scan reported errors.");
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Environment scan failed.");
                ShowErrorToast("环境检查失败", "无法完成环境检查，请重试。详情请查看日志。");
            }
        }

        private static void ResetScanViewState(EnvironmentScanViewState viewState)
        {
            viewState.CpuPrimaryRow.ApplyResult(
                "—",
                string.Empty,
                false,
                false,
                EnvironmentScanner.EnvironmentCheckLevel.Success,
                string.Empty);
            viewState.GpuAdapterRows.Clear();
            viewState.GpuAdapterRows.Add(CreateDashPlaceholderRow());
            viewState.NvidiaSkipNoticeRow.Hide();
            viewState.NvidiaDetailVisible = true;
            viewState.NvidiaNvcuda.Hide();
            viewState.NvidiaNvcuvid.Hide();
            viewState.NvidiaEncodeApi.Hide();
            viewState.DirectXRuntimeFaultRow.Hide();
            viewState.DirectXD3d9.Hide();
            viewState.DirectXD3Dx43.Hide();
            viewState.MediaPackRuntimeFaultRow.Hide();
            viewState.MediaPackMf.Hide();
            viewState.MediaPackMfplat.Hide();
            viewState.MediaPackWmvCore.Hide();
            viewState.Vc2010X86RuntimeFaultRow.Hide();
            viewState.Vc2010X86Msvcr.Hide();
            viewState.Vc2010X86Msvcp.Hide();
            viewState.Vc2010X64RuntimeFaultRow.Hide();
            viewState.Vc2010X64Msvcr.Hide();
            viewState.Vc2010X64Msvcp.Hide();
            viewState.ScanRootAlerts.Clear();
            viewState.HasScanRootAlerts = false;
            viewState.ScanUiReady = false;
        }

        private static void PopulateScanSlots(EnvironmentScanViewState viewState, EnvironmentScanner.EnvironmentScanSummary summary)
        {
            PartitionSummaryItems(summary.Items, out var roots, out var grouped);

            ApplyCpuSlots(viewState, grouped);
            ApplyGpuSlots(viewState, grouped);

            PopulateDllGroup(viewState, grouped, new DllGroupSpec
            {
                GroupKey = "NVIDIA API",
                FaultToken = "系统库检测",
                FaultTitle = "兼容层",
                NotFoundTitle = "NVIDIA API",
                FaultRow = viewState.NvidiaSkipNoticeRow,
                Outcomes = [("nvcuda.dll", viewState.NvidiaNvcuda), ("nvcuvid.dll", viewState.NvidiaNvcuvid), ("nvEncodeAPI64.dll", viewState.NvidiaEncodeApi)],
                HideSuccessFaultBadge = true,
                OnFault = v => v.NvidiaDetailVisible = false
            });
            PopulateDllGroup(viewState, grouped, new DllGroupSpec
            {
                GroupKey = "DirectX9",
                FaultToken = "运行时检测",
                FaultTitle = "DirectX 9 运行时",
                NotFoundTitle = "DirectX 9",
                FaultRow = viewState.DirectXRuntimeFaultRow,
                Outcomes = [("d3d9.dll", viewState.DirectXD3d9), ("d3dx9_43.dll", viewState.DirectXD3Dx43)]
            });
            PopulateDllGroup(viewState, grouped, new DllGroupSpec
            {
                GroupKey = "媒体功能包",
                FaultToken = "运行时检测",
                FaultTitle = "媒体功能包 运行时",
                NotFoundTitle = "系统媒体功能包",
                FaultRow = viewState.MediaPackRuntimeFaultRow,
                Outcomes = [("MF.dll", viewState.MediaPackMf), ("MFPLAT.dll", viewState.MediaPackMfplat), ("WMVCore.dll", viewState.MediaPackWmvCore)]
            });
            PopulateDllGroup(viewState, grouped, new DllGroupSpec
            {
                GroupKey = "VC++2010 x86",
                FaultToken = "运行时检测",
                FaultTitle = "VC++2010 x86 运行时",
                NotFoundTitle = "VC++2010 x86 运行时",
                FaultRow = viewState.Vc2010X86RuntimeFaultRow,
                Outcomes = [("msvcr100.dll", viewState.Vc2010X86Msvcr), ("msvcp100.dll", viewState.Vc2010X86Msvcp)]
            });
            PopulateDllGroup(viewState, grouped, new DllGroupSpec
            {
                GroupKey = "VC++2010 x64",
                FaultToken = "运行时检测",
                FaultTitle = "VC++2010 x64 运行时",
                NotFoundTitle = "VC++2010 x64 运行时",
                FaultRow = viewState.Vc2010X64RuntimeFaultRow,
                Outcomes = [("msvcr100.dll", viewState.Vc2010X64Msvcr), ("msvcp100.dll", viewState.Vc2010X64Msvcp)]
            });

            viewState.ScanRootAlerts.Clear();
            foreach (var root in roots)
            {
                string line = string.IsNullOrWhiteSpace(root.Detail)
                    ? root.Item
                    : $"{root.Item} — {root.Detail.Trim()}";
                viewState.ScanRootAlerts.Add(line);
            }

            viewState.HasScanRootAlerts = viewState.ScanRootAlerts.Count > 0;
            viewState.ScanUiReady = true;
        }

        private sealed class DllGroupSpec
        {
            public string GroupKey { get; init; }
            public string FaultToken { get; init; }
            public string FaultTitle { get; init; }
            public string NotFoundTitle { get; init; }
            public EnvironmentCheckRowState FaultRow { get; init; }
            public (string FileToken, EnvironmentCheckOutcomeState Outcome)[] Outcomes { get; init; }
            public bool HideSuccessFaultBadge { get; init; }
            public Action<EnvironmentScanViewState> OnFault { get; init; }
        }

        private static void PopulateDllGroup(
            EnvironmentScanViewState viewState,
            Dictionary<string, List<EnvironmentScanner.EnvironmentCheckResult>> grouped,
            DllGroupSpec spec)
        {
            if (!grouped.TryGetValue(spec.GroupKey, out var bucket) || bucket.Count == 0)
            {
                spec.FaultRow.ApplyResult(
                    spec.NotFoundTitle,
                    "未有检测输出",
                    true,
                    true,
                    EnvironmentScanner.EnvironmentCheckLevel.Warning,
                    BadgeText(EnvironmentScanner.EnvironmentCheckLevel.Warning));
                foreach (var (token, outcome) in spec.Outcomes)
                    MapLibraryOutcome(EmptyScanBucket, token, outcome);
                return;
            }

            var faultItem = bucket.Find(v =>
                v.Item.IndexOf(spec.FaultToken, StringComparison.OrdinalIgnoreCase) >= 0);

            if (faultItem != null)
            {
                bool detailVisible = !string.IsNullOrWhiteSpace(faultItem.Detail);
                bool showBadge = !(spec.HideSuccessFaultBadge && faultItem.Level == EnvironmentScanner.EnvironmentCheckLevel.Success);
                spec.FaultRow.ApplyResult(
                    spec.FaultTitle,
                    detailVisible ? faultItem.Detail.Trim() : "检测过程中发生异常。",
                    detailVisible,
                    showBadge,
                    faultItem.Level,
                    BadgeText(faultItem.Level));
                foreach (var (token, outcome) in spec.Outcomes)
                    MapLibraryOutcome(EmptyScanBucket, token, outcome);
                spec.OnFault?.Invoke(viewState);
                return;
            }

            foreach (var (token, outcome) in spec.Outcomes)
                MapLibraryOutcome(bucket, token, outcome);
        }

        private static void PartitionSummaryItems(
            IReadOnlyList<EnvironmentScanner.EnvironmentCheckResult> items,
            out List<EnvironmentScanner.EnvironmentCheckResult> roots,
            out Dictionary<string, List<EnvironmentScanner.EnvironmentCheckResult>> grouped)
        {
            roots = new List<EnvironmentScanner.EnvironmentCheckResult>();
            grouped = new Dictionary<string, List<EnvironmentScanner.EnvironmentCheckResult>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                int slashIndex = item.Item.IndexOf('/');
                if (slashIndex <= 0 || slashIndex >= item.Item.Length - 1)
                {
                    roots.Add(item);
                    continue;
                }

                string groupName = item.Item[..slashIndex].Trim();
                if (!grouped.TryGetValue(groupName, out var bucket))
                {
                    bucket = new List<EnvironmentScanner.EnvironmentCheckResult>();
                    grouped[groupName] = bucket;
                }

                bucket.Add(item);
            }
        }

        private static string ChildSegment(string itemKey)
        {
            int slash = itemKey.IndexOf('/');
            return slash >= 0 && slash < itemKey.Length - 1
                ? itemKey[(slash + 1)..].Trim()
                : itemKey.Trim();
        }

        private static string BadgeText(EnvironmentScanner.EnvironmentCheckLevel level)
        {
            return level switch
            {
                EnvironmentScanner.EnvironmentCheckLevel.Success => "通过",
                EnvironmentScanner.EnvironmentCheckLevel.Warning => "警告",
                _ => "失败"
            };
        }

        private static EnvironmentScanner.EnvironmentCheckLevel AggregateWorst(IEnumerable<EnvironmentScanner.EnvironmentCheckResult> seq)
        {
            EnvironmentScanner.EnvironmentCheckLevel lvl = EnvironmentScanner.EnvironmentCheckLevel.Success;

            foreach (var item in seq)
            {
                lvl = WorstTwo(lvl, item.Level);
            }

            return lvl;
        }

        private static EnvironmentScanner.EnvironmentCheckLevel WorstTwo(EnvironmentScanner.EnvironmentCheckLevel a, EnvironmentScanner.EnvironmentCheckLevel b)
        {
            static int Rank(EnvironmentScanner.EnvironmentCheckLevel level) =>
                level switch
                {
                    EnvironmentScanner.EnvironmentCheckLevel.Error => 2,
                    EnvironmentScanner.EnvironmentCheckLevel.Warning => 1,
                    _ => 0,
                };

            return Rank(a) >= Rank(b) ? a : b;
        }

        private static void ApplyCpuSlots(EnvironmentScanViewState viewState, Dictionary<string, List<EnvironmentScanner.EnvironmentCheckResult>> grouped)
        {
            if (!grouped.TryGetValue("CPU", out List<EnvironmentScanner.EnvironmentCheckResult> bucket) || bucket.Count == 0)
            {
                viewState.CpuPrimaryRow.ApplyResult(
                    "未产生检测结果",
                    string.Empty,
                    false,
                    false,
                    EnvironmentScanner.EnvironmentCheckLevel.Warning,
                    string.Empty);
                return;
            }

            EnvironmentScanner.EnvironmentCheckLevel groupLevel = AggregateWorst(bucket);
            EnvironmentScanner.EnvironmentCheckResult anchor = bucket[0];
            string primary = !string.IsNullOrWhiteSpace(anchor.Detail)
                ? anchor.Detail.Trim()
                : ChildSegment(anchor.Item);
            viewState.CpuPrimaryRow.ApplyResult(
                primary,
                string.Empty,
                false,
                false,
                groupLevel,
                string.Empty);
        }

        private static void ApplyGpuSlots(EnvironmentScanViewState viewState, Dictionary<string, List<EnvironmentScanner.EnvironmentCheckResult>> grouped)
        {
            viewState.GpuAdapterRows.Clear();

            if (!grouped.TryGetValue("GPU", out List<EnvironmentScanner.EnvironmentCheckResult> bucket) || bucket.Count == 0)
            {
                var row = new EnvironmentCheckRowState();
                row.ApplyResult(
                    "未发现可用的显示适配器",
                    string.Empty,
                    false,
                    false,
                    EnvironmentScanner.EnvironmentCheckLevel.Warning,
                    string.Empty);
                viewState.GpuAdapterRows.Add(row);
                return;
            }

            foreach (EnvironmentScanner.EnvironmentCheckResult entry in bucket)
            {
                string slug = ChildSegment(entry.Item);
                EnvironmentScanner.EnvironmentCheckLevel rowLevel = entry.Level;

                var row = new EnvironmentCheckRowState();
                row.ApplyResult(
                    slug,
                    string.Empty,
                    false,
                    false,
                    rowLevel,
                    string.Empty);
                viewState.GpuAdapterRows.Add(row);
            }
        }

        private static EnvironmentCheckRowState CreateDashPlaceholderRow()
        {
            var row = new EnvironmentCheckRowState();
            row.ApplyResult(
                "—",
                string.Empty,
                false,
                false,
                EnvironmentScanner.EnvironmentCheckLevel.Success,
                string.Empty);
            return row;
        }

        private static void MapLibraryOutcome(
            List<EnvironmentScanner.EnvironmentCheckResult> bucket,
            string fileToken,
            EnvironmentCheckOutcomeState outcome)
        {
            EnvironmentScanner.EnvironmentCheckResult hit = bucket.Find(value =>
                value.Item.IndexOf(fileToken, StringComparison.OrdinalIgnoreCase) >= 0);

            if (hit == null)
            {
                outcome.Apply(EnvironmentScanner.EnvironmentCheckLevel.Error, BadgeText(EnvironmentScanner.EnvironmentCheckLevel.Error));
            }
            else
            {
                outcome.Apply(hit.Level, BadgeText(hit.Level));
            }
        }

        private string ResolveMachineProperty()
        {
            var identPath = Path.Combine(_paths.GetContentsDirectoryPath(), "prop", "ea3-ident.xml");
            var result = TryReadMachinePropertyFromEa3(identPath);
            if (!string.IsNullOrWhiteSpace(result))
            {
                return result;
            }

            var configPath = Path.Combine(_paths.GetContentsDirectoryPath(), "prop", "ea3-config.xml");
            result = TryReadMachinePropertyFromEa3(configPath);
            return string.IsNullOrWhiteSpace(result) ? "未知" : result;
        }

        private string ResolveCurrentGameVersion()
        {
            try
            {
                var bootstrapPath = Path.Combine(_paths.GetContentsDirectoryPath(), "prop", "bootstrap.xml");
                if (!File.Exists(bootstrapPath))
                {
                    return "未知";
                }

                var doc = XDocument.Load(bootstrapPath);
                var releaseCode = doc.Root?.Element("release_code")?.Value?.Trim();
                return string.IsNullOrWhiteSpace(releaseCode) ? "未知" : releaseCode;
            }
            catch
            {
                return "未知";
            }
        }

        private static string ResolveOperatingSystemVersionName()
        {
            if (!OperatingSystem.IsWindows())
            {
                return "未知";
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key == null)
                {
                    return "未知";
                }

                var productName = key.GetValue("ProductName")?.ToString()?.Trim();
                var editionId = key.GetValue("EditionID")?.ToString()?.Trim();
                var buildNumber = key.GetValue("CurrentBuildNumber")?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(buildNumber))
                {
                    buildNumber = key.GetValue("CurrentBuild")?.ToString()?.Trim();
                }

                productName = NormalizeWindowsProductName(productName, editionId, buildNumber);

                var displayVersion = key.GetValue("DisplayVersion")?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(displayVersion))
                {
                    displayVersion = key.GetValue("ReleaseId")?.ToString()?.Trim();
                }

                if (string.IsNullOrWhiteSpace(productName))
                {
                    return string.IsNullOrWhiteSpace(displayVersion) ? "未知" : displayVersion;
                }

                return string.IsNullOrWhiteSpace(displayVersion)
                    ? productName
                    : $"{productName} {displayVersion}";
            }
            catch
            {
                return "未知";
            }
        }

        private static string NormalizeWindowsProductName(string productName, string editionId, string buildNumber)
        {
            if (!IsWindows11Build(buildNumber))
            {
                return productName;
            }

            if (string.IsNullOrWhiteSpace(productName))
            {
                var editionName = NormalizeWindowsEditionName(editionId);
                return string.IsNullOrWhiteSpace(editionName)
                    ? "Windows 11"
                    : $"Windows 11 {editionName}";
            }

            const string windows10Prefix = "Windows 10";
            if (!productName.StartsWith(windows10Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return productName;
            }

            return $"Windows 11{productName.Substring(windows10Prefix.Length)}";
        }

        private static bool IsWindows11Build(string buildNumber)
        {
            return int.TryParse(buildNumber, out var build) && build >= 22000;
        }

        private static string NormalizeWindowsEditionName(string editionId)
        {
            if (string.IsNullOrWhiteSpace(editionId))
            {
                return string.Empty;
            }

            return editionId switch
            {
                "Core" => "Home",
                "CoreSingleLanguage" => "Home Single Language",
                "Professional" => "Pro",
                "ProfessionalWorkstation" => "Pro for Workstations",
                "ProfessionalEducation" => "Pro Education",
                "EnterpriseS" => "Enterprise LTSC",
                _ => editionId
            };
        }

        private static string ResolveOperatingSystemBuildNumber()
        {
            if (!OperatingSystem.IsWindows())
            {
                return "未知";
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key == null)
                {
                    return "未知";
                }

                var buildNumber = key.GetValue("CurrentBuildNumber")?.ToString()?.Trim();
                var ubr = key.GetValue("UBR")?.ToString()?.Trim();

                if (string.IsNullOrWhiteSpace(buildNumber))
                {
                    return "未知";
                }

                return string.IsNullOrWhiteSpace(ubr)
                    ? buildNumber
                    : $"{buildNumber}.{ubr}";
            }
            catch
            {
                return "未知";
            }
        }

        private string ResolveLauncherVersion()
        {
            try
            {
                var launcherExe = _paths.GetLauncherExecutablePath();
                if (!File.Exists(launcherExe))
                {
                    return "未知";
                }

                var versionInfo = FileVersionInfo.GetVersionInfo(launcherExe);
                if (!string.IsNullOrWhiteSpace(versionInfo.FileVersion))
                {
                    return versionInfo.FileVersion;
                }

                if (!string.IsNullOrWhiteSpace(versionInfo.ProductVersion))
                {
                    return versionInfo.ProductVersion;
                }
            }
            catch
            {
            }

            return "未知";
        }

        private static string TryReadMachinePropertyFromEa3(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    return null;
                }

                var doc = XDocument.Load(filePath);
                var softNode = doc.Root?.Element("soft");
                if (softNode == null)
                {
                    return null;
                }

                var model = softNode.Element("model")?.Value?.Trim();
                var dest = softNode.Element("dest")?.Value?.Trim();
                var spec = softNode.Element("spec")?.Value?.Trim();
                var rev = softNode.Element("rev")?.Value?.Trim();

                if (string.IsNullOrWhiteSpace(model)
                    || string.IsNullOrWhiteSpace(dest)
                    || string.IsNullOrWhiteSpace(spec)
                    || string.IsNullOrWhiteSpace(rev))
                {
                    return null;
                }

                return $"{model}:{dest}:{spec}:{rev}";
            }
            catch
            {
                return null;
            }
        }
    }
}
