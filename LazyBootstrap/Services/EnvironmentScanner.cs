using System;
using SystemEnvironment = System.Environment;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using LazyBootstrap.Platform;

namespace LazyBootstrap.Services
{
    internal static class EnvironmentScanner
    {
        private const int StepCount = 7; // CPU / GPU / NVIDIA API / DirectX9.0c / 系统媒体功能包 / VC2010 x86 / VC2010 x64

        internal enum EnvironmentCheckLevel
        {
            Success,
            Warning,
            Error
        }

        internal sealed class EnvironmentCheckResult
        {
            public string Item { get; set; } = string.Empty;

            public string Detail { get; set; } = string.Empty;

            public EnvironmentCheckLevel Level { get; set; } = EnvironmentCheckLevel.Success;
        }

        internal sealed class EnvironmentScanSummary
        {
            public static EnvironmentScanSummary Empty { get; } = new(false, string.Empty, Array.Empty<EnvironmentCheckResult>());

            public EnvironmentScanSummary(bool hadError, string errorSummary, IReadOnlyList<EnvironmentCheckResult> items)
            {
                HadError = hadError;
                ErrorSummary = errorSummary ?? string.Empty;
                Items = items ?? Array.Empty<EnvironmentCheckResult>();
            }

            public bool HadError { get; }

            public string ErrorSummary { get; }

            public IReadOnlyList<EnvironmentCheckResult> Items { get; }
        }

        internal static List<string> GetGpuNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!OperatingSystem.IsWindows())
            {
                return set.ToList();
            }

            try
            {
                using (var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\\CurrentControlSet\\Control\\Video"))
                {
                    if (root == null) return set.ToList();
                    foreach (var guid in root.GetSubKeyNames())
                    {
                        using (var adapterKey = root.OpenSubKey(guid))
                        {
                            if (adapterKey == null) continue;
                            foreach (var sub in new[] { "0000", "0001", "0002" })
                            {
                                using (var conf = adapterKey.OpenSubKey(sub))
                                {
                                    var desc = conf?.GetValue("DriverDesc") as string;
                                    if (!string.IsNullOrWhiteSpace(desc)) set.Add(desc.Trim());
                                    var adapterStr = conf?.GetValue("HardwareInformation.AdapterString") as string;
                                    if (!string.IsNullOrWhiteSpace(adapterStr)) set.Add(adapterStr.Trim());
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return set.ToList();
        }

        public static Task<EnvironmentScanSummary> RunAsync(
            Action<int, string> progress,
            string contentsDirectoryPath,
            string bundledLibsDirectoryPath)
        {
            return Task.Run(() =>
            {
            // 统一的进度上报（不传递 message 内容，仅数值）
            void Report(int stepIndex) { try { progress?.Invoke((int)Math.Round((double)stepIndex * 100 / StepCount), ""); } catch { } }

            bool hadError = false; // 仅 Error
            var errorSummary = new StringBuilder();
            var items = new List<EnvironmentCheckResult>();

            void AddResult(string item, string detail, EnvironmentCheckLevel level)
            {
                items.Add(new EnvironmentCheckResult
                {
                    Item = item,
                    Detail = detail,
                    Level = level
                });

                if (level == EnvironmentCheckLevel.Error)
                {
                    hadError = true;
                    errorSummary.AppendLine(item);
                }
            }

            Report(0);

            // 局部函数：无对外暴露，保持单方法结构
            string GetCpuName()
            {
                if (!OperatingSystem.IsWindows())
                {
                    return "未知处理器";
                }

                try
                {
                    using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0"))
                    {
                        var name = k?.GetValue("ProcessorNameString") as string;
                        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
                    }
                }
                catch { }
                return "未知处理器";
            }

            void CheckDlls(string groupName, string dllDirectory, string[] dllNames, string faultLabel = "运行时检测")
            {
                try
                {
                    foreach (var dll in dllNames)
                    {
                        bool ok = File.Exists(Path.Combine(dllDirectory, dll));
                        AddResult($"{groupName}/{dll}", string.Empty, ok ? EnvironmentCheckLevel.Success : EnvironmentCheckLevel.Error);
                    }
                }
                catch (Exception)
                {
                    AddResult($"{groupName}/{faultLabel}", string.Empty, EnvironmentCheckLevel.Error);
                }
            }

            var sysDir = Path.Combine(SystemEnvironment.GetFolderPath(SystemEnvironment.SpecialFolder.Windows), "System32");
            var winDir = SystemEnvironment.GetFolderPath(SystemEnvironment.SpecialFolder.Windows);

            void LogCpu()
            {
                var cpuName = GetCpuName();
                AddResult("CPU/处理器", cpuName, string.Equals(cpuName, "未知处理器", StringComparison.Ordinal) ? EnvironmentCheckLevel.Warning : EnvironmentCheckLevel.Success);
            }

            void LogGpu()
            {
                var gpus = GetGpuNames();
                if (gpus.Count == 0)
                {
                    AddResult("GPU/显示适配器", string.Empty, EnvironmentCheckLevel.Warning);
                    return;
                }

                foreach (var gpu in gpus)
                {
                    AddResult($"GPU/{gpu}", string.Empty, EnvironmentCheckLevel.Success);
                }
            }

            void LogNvidia()
            {
                try
                {
                    if (GpuCompatLayerConfigurator.DetectRuntimeState(contentsDirectoryPath, bundledLibsDirectoryPath).IsFullyApplied)
                    {
                        AddResult("NVIDIA API/系统库检测", "已启用兼容层，自动跳过 NVIDIA API 检测", EnvironmentCheckLevel.Success);
                        return;
                    }
                }
                catch { }

                CheckDlls("NVIDIA API", sysDir, ["nvcuda.dll", "nvcuvid.dll", "nvEncodeAPI64.dll"], "系统库检测");
            }

            void LogDirectX()
            {
                CheckDlls("DirectX9", sysDir, ["d3d9.dll", "d3dx9_43.dll"]);
            }

            void LogMediaFeaturePack()
            {
                CheckDlls("媒体功能包", sysDir, ["MF.dll", "MFPLAT.dll", "WMVCore.dll"]);
            }

            void LogVc2010(bool x64)
            {
                string arch = x64 ? "x64" : "x86";
                string dllDir = x64
                    ? Path.Combine(winDir, "System32")
                    : SystemEnvironment.Is64BitOperatingSystem
                        ? Path.Combine(winDir, "SysWOW64")
                        : Path.Combine(winDir, "System32");
                CheckDlls($"VC++2010 {arch}", dllDir, ["msvcr100.dll", "msvcp100.dll"]);
            }

            // 步骤定义数组，循环执行
            var steps = new Action[]
            {
                LogCpu,
                LogGpu,
                LogNvidia,
                LogDirectX,
                LogMediaFeaturePack,
                () => LogVc2010(false),
                () => LogVc2010(true)
            };

            for (int i = 0; i < steps.Length; i++)
            {
                try { steps[i](); }
                catch (Exception) { AddResult($"步骤{i + 1}", string.Empty, EnvironmentCheckLevel.Error); }
                Report(i + 1);
            }

            Report(StepCount);
            return new EnvironmentScanSummary(hadError, errorSummary.ToString(), items.ToArray());
            });
        }
    }
}
