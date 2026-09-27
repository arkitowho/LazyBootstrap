using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LazyBootstrap.Services
{

    internal enum DisplayDiscoveryStatus { Complete, Partial, Failed }

    internal sealed class DisplayDiscoveryResult
    {
        public DisplayDiscoveryResult(IReadOnlyList<DisplayInfo> displays, string errorMessage = "", int adapterCount = 0, int desktopCount = 0)
        {
            Displays = displays ?? Array.Empty<DisplayInfo>();
            ErrorMessage = Displays.Count == 0 && string.IsNullOrWhiteSpace(errorMessage)
                ? "暂未检测到已启用的显示器，请稍后重新检测。" : errorMessage ?? string.Empty;
            AdapterCount = adapterCount;
            DesktopCount = desktopCount;
        }

        public IReadOnlyList<DisplayInfo> Displays { get; }

        public string ErrorMessage { get; }

        public bool Succeeded => string.IsNullOrWhiteSpace(ErrorMessage);
        public DisplayDiscoveryStatus Status => Succeeded ? DisplayDiscoveryStatus.Complete
            : Displays.Count > 0 ? DisplayDiscoveryStatus.Partial : DisplayDiscoveryStatus.Failed;
        public int AdapterCount { get; }
        public int DesktopCount { get; }
    }

    internal sealed class DisplayModeQueryResult
    {
        public DisplayModeQueryResult(IReadOnlyList<DisplayMode> modes, string errorMessage = "")
        {
            Modes = modes ?? Array.Empty<DisplayMode>();
            ErrorMessage = errorMessage ?? string.Empty;
        }

        public IReadOnlyList<DisplayMode> Modes { get; }

        public string ErrorMessage { get; }

        public bool Succeeded => string.IsNullOrWhiteSpace(ErrorMessage);
    }

    internal sealed class DisplayStateQueryResult
    {
        public DisplayStateQueryResult(DisplayState state, string errorMessage = "")
        {
            State = state;
            ErrorMessage = errorMessage ?? string.Empty;
        }

        public DisplayState State { get; }

        public string ErrorMessage { get; }

        public bool Succeeded => State != null && string.IsNullOrWhiteSpace(ErrorMessage);
    }

    internal sealed class DisplayConfigurationResult
    {
        public DisplayConfigurationResult(bool succeeded, string errorMessage = "")
        {
            Succeeded = succeeded;
            ErrorMessage = errorMessage ?? string.Empty;
        }

        public bool Succeeded { get; }

        public string ErrorMessage { get; }

        public static DisplayConfigurationResult Success()
        {
            return new DisplayConfigurationResult(true);
        }

        public static DisplayConfigurationResult Failure(string errorMessage)
        {
            return new DisplayConfigurationResult(false, errorMessage);
        }
    }

    internal sealed class DisplayInfo
    {
        public string DeviceName { get; init; } = string.Empty;

        internal string PersistentId { get; init; } = string.Empty;

        public string FriendlyName { get; init; } = string.Empty;

        public bool IsPrimary { get; init; }
        public bool IsAvailable => !string.IsNullOrWhiteSpace(DeviceName);
    }

    internal sealed class DisplayMode
    {
        public int Width { get; init; }

        public int Height { get; init; }

        public int RefreshRate { get; init; }
    }

    internal sealed class DisplayState
    {
        public string DeviceName { get; init; } = string.Empty;

        public int Orientation { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }

        public int RefreshRate { get; init; }
    }

    internal class WindowsDisplayConfigurationService
    {
        private static readonly (int Width, int Height)[] CommonProbeResolutions =
        {
            (1280, 720),
            (1920, 1080)
        };

        private static readonly int[] CommonProbeRefreshRates =
        {
            50,
            59,
            60,
            75,
            85,
            100,
            120,
            144,
            165,
            170,
            180,
            200,
            240,
            280,
            300,
            360
        };

        private const int DisplayDeviceActive = 0x1;
        private const int DisplayDevicePrimaryDevice = 0x4;
        private const int DisplayDeviceMirroringDriver = 0x8;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public int Size;
            public int Left, Top, Right, Bottom;
            public int WorkLeft, WorkTop, WorkRight, WorkBottom;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool MonitorEnumCallback(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumCallback callback, IntPtr data);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

        private const int DmdoDefault = 0;
        private const int Dmdo90 = 1;
        private const int Dmdo180 = 2;
        private const int Dmdo270 = 3;

        private const int EnumCurrentSettings = -1;

        private const int EdsRawMode = 0x2;

        private const int CdsUpdateRegistry = 0x01;
        private const int CdsTest = 0x02;

        private const int DmDisplayOrientation = 0x00000080;
        private const int DmPelsWidth = 0x00080000;
        private const int DmPelsHeight = 0x00100000;
        private const int DmDisplayFrequency = 0x00400000;

        private const int DispChangeSuccessful = 0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        protected internal struct DisplayDevice
        {
            public int Cb;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;

            public int StateFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceId;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        protected internal struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;

            public short SpecVersion;
            public short DriverVersion;
            public short Size;
            public short DriverExtra;
            public int Fields;
            public int PositionX;
            public int PositionY;
            public int DisplayOrientation;
            public int DisplayFixedOutput;
            public short Color;
            public short Duplex;
            public short YResolution;
            public short TTOption;
            public short Collate;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string FormName;

            public short LogPixels;
            public int BitsPerPel;
            public int PelsWidth;
            public int PelsHeight;
            public int DisplayFlags;
            public int DisplayFrequency;
            public int IcmMethod;
            public int IcmIntent;
            public int MediaType;
            public int DitherType;
            public int Reserved1;
            public int Reserved2;
            public int PanningWidth;
            public int PanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern int ChangeDisplaySettingsEx(string lpszDeviceName, ref DevMode lpDevMode, IntPtr hwnd, int dwFlags, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplaySettings(string lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplaySettingsEx(string lpszDeviceName, int iModeNum, ref DevMode lpDevMode, int dwFlags);

        public DisplayDiscoveryResult GetDisplays()
        {
            var displays = new Dictionary<string, DisplayInfo>(StringComparer.OrdinalIgnoreCase);
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            int adapterCount = 0;
            int desktopCount = 0;
            try
            {
                for (uint index = 0; ; index++)
                {
                    var adapter = CreateDisplayDevice();
                    if (!TryEnumDisplayDevices(null, index, ref adapter)) break;
                    if (string.IsNullOrWhiteSpace(adapter.DeviceName)) continue;
                    if ((adapter.StateFlags & DisplayDeviceMirroringDriver) != 0)
                    {
                        excluded.Add(adapter.DeviceName);
                        continue;
                    }
                    if ((adapter.StateFlags & DisplayDeviceActive) == 0) continue;
                    adapterCount++;
                    // A driver/metadata failure on one output must not discard later outputs.
                    var info = new DisplayInfo
                    {
                        DeviceName = adapter.DeviceName,
                        PersistentId = adapter.DeviceName,
                        FriendlyName = adapter.DeviceName,
                        IsPrimary = (adapter.StateFlags & DisplayDevicePrimaryDevice) != 0
                    };
                    try
                    {
                        var monitors = EnumerateActiveMonitors(adapter.DeviceName);
                        info = new DisplayInfo
                        {
                            DeviceName = adapter.DeviceName,
                            PersistentId = ResolvePersistentId(adapter, monitors),
                            FriendlyName = ResolveFriendlyName(adapter, monitors),
                            IsPrimary = info.IsPrimary
                        };
                    }
                    catch (Exception ex) { errors.Add($"读取 {adapter.DeviceName} 信息失败: {ex.Message}"); }
                    displays[adapter.DeviceName] = info;
                }
            }
            catch (Exception ex) { errors.Add($"枚举显示设备失败: {ex.Message}"); }

            try
            {
                var desktop = EnumerateDesktopDisplays();
                desktopCount = desktop.Displays.Count;
                if (!desktop.Succeeded) errors.Add(desktop.ErrorMessage);
                foreach (var display in desktop.Displays)
                {
                    if (!display.IsAvailable || excluded.Contains(display.DeviceName)) continue;
                    if (!displays.ContainsKey(display.DeviceName))
                    {
                        var adapter = CreateDisplayDevice();
                        adapter.DeviceName = display.DeviceName;
                        try
                        {
                            var monitors = EnumerateActiveMonitors(display.DeviceName);
                            displays[display.DeviceName] = new DisplayInfo
                            {
                                DeviceName = display.DeviceName,
                                PersistentId = ResolvePersistentId(adapter, monitors),
                                FriendlyName = ResolveFriendlyName(adapter, monitors),
                                IsPrimary = display.IsPrimary
                            };
                        }
                        catch (Exception ex)
                        {
                            displays[display.DeviceName] = display;
                            errors.Add($"读取 {display.DeviceName} 信息失败: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex) { errors.Add($"枚举桌面显示器失败: {ex.Message}"); }
            return new DisplayDiscoveryResult(displays.Values.ToArray(), string.Join("\n", errors), adapterCount, desktopCount);
        }

        protected virtual DisplayDiscoveryResult EnumerateDesktopDisplays()
        {
            var displays = new List<DisplayInfo>();
            bool incomplete = false;
            MonitorEnumCallback callback = (monitor, _, _, _) =>
            {
                // Never let an exception cross the unmanaged callback boundary.
                try
                {
                    var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                    if (!GetMonitorInfo(monitor, ref info) || string.IsNullOrWhiteSpace(info.DeviceName))
                    {
                        incomplete = true;
                        return true;
                    }
                    displays.Add(new DisplayInfo
                    {
                        DeviceName = info.DeviceName,
                        PersistentId = info.DeviceName,
                        FriendlyName = info.DeviceName,
                        IsPrimary = (info.Flags & 1) != 0
                    });
                }
                catch { incomplete = true; }
                return true;
            };
            bool success = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            return new DisplayDiscoveryResult(displays,
                success && !incomplete ? string.Empty : "无法完整读取桌面显示器列表。");
        }
        public DisplayModeQueryResult GetSupportedModes(string deviceName)
        {
            var modes = new List<DisplayMode>();
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return new DisplayModeQueryResult(modes, "未提供显示器设备名称。");
            }

            try
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                EnumerateDisplayModes(deviceName, useRawModes: false, modes, seen);
                EnumerateDisplayModes(deviceName, useRawModes: true, modes, seen);

                var currentStateResult = GetCurrentState(deviceName);
                var currentState = currentStateResult.Succeeded ? currentStateResult.State : null;
                if (currentState != null)
                {
                    AddDisplayMode(modes, seen, currentState.Width, currentState.Height, currentState.RefreshRate);
                }

                if (modes.Count > 0)
                {
                    SupplementProbeModes(deviceName, modes, seen, currentState);
                }

                var orderedModes = modes
                    .OrderBy(mode => mode.Width * mode.Height)
                    .ThenBy(mode => mode.Width)
                    .ThenBy(mode => mode.Height)
                    .ThenBy(mode => mode.RefreshRate)
                    .ToList();

                return orderedModes.Count > 0
                    ? new DisplayModeQueryResult(orderedModes)
                    : new DisplayModeQueryResult(orderedModes, $"未读取到 {deviceName} 的任何显示模式。");
            }
            catch (Exception ex)
            {
                return new DisplayModeQueryResult(modes, $"读取 {deviceName} 显示模式失败: {ex.Message}");
            }
        }

        public DisplayStateQueryResult GetCurrentState(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return new DisplayStateQueryResult(null, "未提供显示器设备名称。");
            }

            try
            {
                var devMode = CreateDevMode();
                if (!TryEnumDisplaySettings(deviceName, EnumCurrentSettings, ref devMode))
                {
                    return new DisplayStateQueryResult(null, $"无法读取 {deviceName} 的当前显示状态。");
                }

                return new DisplayStateQueryResult(new DisplayState
                {
                    DeviceName = deviceName,
                    Orientation = devMode.DisplayOrientation,
                    Width = devMode.PelsWidth,
                    Height = devMode.PelsHeight,
                    RefreshRate = devMode.DisplayFrequency
                });
            }
            catch (Exception ex)
            {
                return new DisplayStateQueryResult(null, $"读取 {deviceName} 当前显示状态失败: {ex.Message}");
            }
        }

        public int OrientationToAngle(int orientation)
        {
            return orientation switch
            {
                Dmdo90 => 90,
                Dmdo180 => 180,
                Dmdo270 => 270,
                _ => 0
            };
        }

        public DisplayConfigurationResult ApplyDisplaySettings(string deviceName, int angle, int width, int height, int refreshRate)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return DisplayConfigurationResult.Failure("未提供显示器设备名称。");
            }

            if (width <= 0 || height <= 0 || refreshRate <= 0)
            {
                return DisplayConfigurationResult.Failure("显示器分辨率或刷新率无效。");
            }

            try
            {
                int orientation = AngleToOrientation(angle);
                if (orientation < 0)
                {
                    return DisplayConfigurationResult.Failure($"不支持的旋转角度: {angle}。");
                }

                var devMode = CreateDevMode();
                if (!TryEnumDisplaySettings(deviceName, EnumCurrentSettings, ref devMode))
                {
                    return DisplayConfigurationResult.Failure($"无法读取 {deviceName} 的当前显示状态。");
                }

                devMode.DisplayOrientation = orientation;
                devMode.PelsWidth = width;
                devMode.PelsHeight = height;
                devMode.DisplayFrequency = refreshRate;
                devMode.Fields = DmDisplayOrientation | DmPelsWidth | DmPelsHeight | DmDisplayFrequency;

                int testResult = TryChangeDisplaySettings(deviceName, ref devMode, CdsTest);
                if (testResult != DispChangeSuccessful)
                {
                    return DisplayConfigurationResult.Failure($"显示器设置校验失败，返回码: {testResult}。");
                }

                int result = TryChangeDisplaySettings(deviceName, ref devMode, CdsUpdateRegistry);
                return result == DispChangeSuccessful
                    ? DisplayConfigurationResult.Success()
                    : DisplayConfigurationResult.Failure($"应用显示器设置失败，返回码: {result}。");
            }
            catch (Exception ex)
            {
                return DisplayConfigurationResult.Failure($"应用显示器设置失败: {ex.Message}");
            }
        }

        public DisplayConfigurationResult RestoreDisplaySettings(DisplayState state)
        {
            if (state == null)
            {
                return DisplayConfigurationResult.Failure("未提供需要恢复的显示器状态。");
            }

            if (string.IsNullOrWhiteSpace(state.DeviceName))
            {
                return DisplayConfigurationResult.Failure("显示器状态缺少设备名称。");
            }

            if (state.Width <= 0 || state.Height <= 0 || state.RefreshRate <= 0)
            {
                return DisplayConfigurationResult.Failure("显示器状态包含无效的分辨率或刷新率。");
            }

            try
            {
                var devMode = CreateDevMode();
                if (!TryEnumDisplaySettings(state.DeviceName, EnumCurrentSettings, ref devMode))
                {
                    return DisplayConfigurationResult.Failure($"无法读取 {state.DeviceName} 的当前显示状态。");
                }

                devMode.DisplayOrientation = state.Orientation;
                devMode.PelsWidth = state.Width;
                devMode.PelsHeight = state.Height;
                devMode.DisplayFrequency = state.RefreshRate;
                devMode.Fields = DmDisplayOrientation | DmPelsWidth | DmPelsHeight | DmDisplayFrequency;

                int testResult = TryChangeDisplaySettings(state.DeviceName, ref devMode, CdsTest);
                if (testResult != DispChangeSuccessful)
                {
                    return DisplayConfigurationResult.Failure($"显示器还原校验失败，返回码: {testResult}。");
                }

                int result = TryChangeDisplaySettings(state.DeviceName, ref devMode, CdsUpdateRegistry);
                return result == DispChangeSuccessful
                    ? DisplayConfigurationResult.Success()
                    : DisplayConfigurationResult.Failure($"还原显示器设置失败，返回码: {result}。");
            }
            catch (Exception ex)
            {
                return DisplayConfigurationResult.Failure($"还原显示器设置失败: {ex.Message}");
            }
        }

        protected virtual bool TryEnumDisplayDevices(string deviceName, uint deviceIndex, ref DisplayDevice device)
        {
            return EnumDisplayDevices(deviceName, deviceIndex, ref device, 0);
        }

        protected virtual bool TryEnumDisplaySettings(string deviceName, int modeIndex, ref DevMode devMode)
        {
            return EnumDisplaySettings(deviceName, modeIndex, ref devMode);
        }

        protected virtual bool TryEnumDisplaySettingsEx(string deviceName, int modeIndex, ref DevMode devMode, int flags)
        {
            return EnumDisplaySettingsEx(deviceName, modeIndex, ref devMode, flags);
        }

        protected virtual int TryChangeDisplaySettings(string deviceName, ref DevMode devMode, int flags)
        {
            return ChangeDisplaySettingsEx(deviceName, ref devMode, IntPtr.Zero, flags, IntPtr.Zero);
        }

        private void EnumerateDisplayModes(string deviceName, bool useRawModes, List<DisplayMode> modes, ISet<string> seen)
        {
            for (int index = 0; ; index++)
            {
                var current = CreateDevMode();
                bool success = useRawModes
                    ? TryEnumDisplaySettingsEx(deviceName, index, ref current, EdsRawMode)
                    : TryEnumDisplaySettings(deviceName, index, ref current);
                if (!success)
                {
                    break;
                }

                AddDisplayMode(modes, seen, current.PelsWidth, current.PelsHeight, current.DisplayFrequency);
            }
        }

        private static void AddDisplayMode(List<DisplayMode> modes, ISet<string> seen, int width, int height, int refreshRate)
        {
            if (width <= 0 || height <= 0 || refreshRate <= 0)
            {
                return;
            }

            string key = $"{width}x{height}@{refreshRate}";
            if (!seen.Add(key))
            {
                return;
            }

            modes.Add(new DisplayMode
            {
                Width = width,
                Height = height,
                RefreshRate = refreshRate
            });
        }

        private void SupplementProbeModes(string deviceName, List<DisplayMode> modes, ISet<string> seen, DisplayState currentState)
        {
            if (modes == null || modes.Count == 0)
            {
                return;
            }

            var highestMode = modes
                .OrderByDescending(mode => mode.Width * mode.Height)
                .ThenByDescending(mode => mode.Width)
                .ThenByDescending(mode => mode.Height)
                .FirstOrDefault();
            if (highestMode == null)
            {
                return;
            }

            int maxArea = highestMode.Width * highestMode.Height;
            var refreshCandidates = BuildProbeRefreshCandidates(modes, currentState?.RefreshRate);
            var resolutionCandidates = BuildProbeResolutionCandidates(modes, currentState);

            foreach (var resolution in resolutionCandidates)
            {
                if (resolution.Width * resolution.Height > maxArea)
                {
                    continue;
                }

                var adjustedResolution = AdjustResolutionForOrientation(resolution.Width, resolution.Height, currentState?.Orientation ?? DmdoDefault);
                foreach (int refreshRate in refreshCandidates)
                {
                    if (!TryProbeMode(deviceName, adjustedResolution.Width, adjustedResolution.Height, refreshRate))
                    {
                        continue;
                    }

                    AddDisplayMode(modes, seen, adjustedResolution.Width, adjustedResolution.Height, refreshRate);
                }
            }
        }

        private static IReadOnlyList<(int Width, int Height)> BuildProbeResolutionCandidates(IEnumerable<DisplayMode> modes, DisplayState currentState)
        {
            var candidates = new List<(int Width, int Height)>();

            foreach (var resolution in CommonProbeResolutions)
            {
                AddProbeResolutionCandidate(candidates, resolution.Width, resolution.Height);
            }

            var highestMode = modes
                .Where(mode => mode.Width > 0 && mode.Height > 0)
                .OrderByDescending(mode => mode.Width * mode.Height)
                .ThenByDescending(mode => mode.Width)
                .ThenByDescending(mode => mode.Height)
                .FirstOrDefault();
            if (highestMode != null)
            {
                AddProbeResolutionCandidate(candidates, highestMode.Width, highestMode.Height);
            }

            if (currentState != null)
            {
                AddProbeResolutionCandidate(candidates, currentState.Width, currentState.Height);
            }

            return candidates;
        }

        private static void AddProbeResolutionCandidate(ICollection<(int Width, int Height)> candidates, int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (candidates.Any(candidate => candidate.Width == width && candidate.Height == height))
            {
                return;
            }

            candidates.Add((width, height));
        }

        private List<int> BuildProbeRefreshCandidates(IEnumerable<DisplayMode> modes, int? currentRefreshRate)
        {
            var candidates = new List<int>();
            if (currentRefreshRate.HasValue && currentRefreshRate.Value > 0)
            {
                candidates.Add(currentRefreshRate.Value);
            }

            foreach (int refreshRate in CommonProbeRefreshRates)
            {
                if (!candidates.Contains(refreshRate))
                {
                    candidates.Add(refreshRate);
                }
            }

            foreach (int refreshRate in modes
                .Select(mode => mode.RefreshRate)
                .Where(value => value > 0)
                .Distinct()
                .OrderBy(value => value))
            {
                if (!candidates.Contains(refreshRate))
                {
                    candidates.Add(refreshRate);
                }
            }

            return candidates;
        }

        private bool TryProbeMode(string deviceName, int width, int height, int refreshRate)
        {
            var devMode = CreateDevMode();
            if (!TryEnumDisplaySettings(deviceName, EnumCurrentSettings, ref devMode))
            {
                return false;
            }

            devMode.PelsWidth = width;
            devMode.PelsHeight = height;
            devMode.DisplayFrequency = refreshRate;
            devMode.Fields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;

            return TryChangeDisplaySettings(deviceName, ref devMode, CdsTest) == DispChangeSuccessful;
        }

        private static (int Width, int Height) AdjustResolutionForOrientation(int width, int height, int orientation)
        {
            bool isPortrait = orientation == Dmdo90 || orientation == Dmdo270;
            return isPortrait
                ? (Math.Min(width, height), Math.Max(width, height))
                : (Math.Max(width, height), Math.Min(width, height));
        }

        private List<DisplayDevice> EnumerateActiveMonitors(string adapterDeviceName)
        {
            var monitors = new List<DisplayDevice>();
            uint monitorIndex = 0;

            while (true)
            {
                var monitor = CreateDisplayDevice();
                if (!TryEnumDisplayDevices(adapterDeviceName, monitorIndex, ref monitor))
                {
                    break;
                }

                if ((monitor.StateFlags & DisplayDeviceActive) != 0)
                {
                    monitors.Add(monitor);
                }

                monitorIndex++;
            }

            return monitors;
        }

        private static string ResolveFriendlyName(DisplayDevice adapter, IReadOnlyList<DisplayDevice> monitors)
        {
            foreach (var monitor in monitors)
            {
                string registryName = ResolveFriendlyNameFromRegistry(monitor);
                if (!string.IsNullOrWhiteSpace(registryName))
                {
                    return registryName;
                }
            }

            foreach (var monitor in monitors)
            {
                string monitorName = monitor.DeviceString?.Trim() ?? string.Empty;
                if (!IsGenericMonitorName(monitorName))
                {
                    return monitorName;
                }
            }

            return adapter.DeviceName?.Trim() ?? string.Empty;
        }

        private static string ResolvePersistentId(DisplayDevice adapter, IReadOnlyList<DisplayDevice> monitors)
        {
            foreach (var monitor in monitors)
            {
                string registryIdentity = ResolvePersistentIdFromRegistry(monitor);
                if (!string.IsNullOrWhiteSpace(registryIdentity))
                {
                    return registryIdentity;
                }
            }

            foreach (var monitor in monitors)
            {
                string deviceId = NormalizeMonitorIdentity(monitor.DeviceId);
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    return deviceId;
                }
            }

            return adapter.DeviceName?.Trim() ?? string.Empty;
        }

        private static string ResolvePersistentIdFromRegistry(DisplayDevice monitor)
        {
            return OperatingSystem.IsWindows()
                ? ResolvePersistentIdFromRegistryCore(monitor)
                : string.Empty;
        }

        [SupportedOSPlatform("windows")]
        private static string ResolvePersistentIdFromRegistryCore(DisplayDevice monitor)
        {
            string hardwareKey = ExtractMonitorHardwareKey(monitor.DeviceId);
            if (string.IsNullOrWhiteSpace(hardwareKey))
            {
                hardwareKey = ExtractMonitorHardwareKey(ExtractEnumIdentityFromRegistryPath(monitor.DeviceKey));
            }

            if (string.IsNullOrWhiteSpace(hardwareKey))
            {
                return string.Empty;
            }

            try
            {
                using var displayKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{hardwareKey}");
                if (displayKey == null)
                {
                    return string.Empty;
                }

                string driverKey = ExtractMonitorDriverKey(monitor.DeviceKey);
                string fallbackIdentity = string.Empty;
                foreach (string instanceName in displayKey.GetSubKeyNames())
                {
                    if (string.IsNullOrWhiteSpace(instanceName))
                    {
                        continue;
                    }

                    string identity = NormalizeMonitorIdentity($@"DISPLAY\{hardwareKey}\{instanceName}");
                    if (string.IsNullOrWhiteSpace(driverKey))
                    {
                        return identity;
                    }

                    using var instanceKey = displayKey.OpenSubKey(instanceName);
                    string instanceDriverKey = instanceKey?.GetValue("Driver")?.ToString()?.Trim() ?? string.Empty;
                    if (string.Equals(instanceDriverKey, driverKey, StringComparison.OrdinalIgnoreCase))
                    {
                        return identity;
                    }

                    fallbackIdentity = string.IsNullOrWhiteSpace(fallbackIdentity)
                        ? identity
                        : fallbackIdentity;
                }

                return fallbackIdentity;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ResolveFriendlyNameFromRegistry(DisplayDevice monitor)
        {
            return OperatingSystem.IsWindows()
                ? ResolveFriendlyNameFromRegistryCore(monitor)
                : string.Empty;
        }

        [SupportedOSPlatform("windows")]
        private static string ResolveFriendlyNameFromRegistryCore(DisplayDevice monitor)
        {
            string hardwareKey = ExtractMonitorHardwareKey(monitor.DeviceId);
            if (string.IsNullOrWhiteSpace(hardwareKey))
            {
                hardwareKey = ExtractMonitorHardwareKey(ExtractEnumIdentityFromRegistryPath(monitor.DeviceKey));
            }

            if (string.IsNullOrWhiteSpace(hardwareKey))
            {
                return string.Empty;
            }

            try
            {
                using var displayKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{hardwareKey}");
                if (displayKey == null)
                {
                    return string.Empty;
                }

                string driverKey = ExtractMonitorDriverKey(monitor.DeviceKey);
                var fallbackNames = new List<string>();
                foreach (string instanceName in displayKey.GetSubKeyNames())
                {
                    using var instanceKey = displayKey.OpenSubKey(instanceName);
                    if (instanceKey == null)
                    {
                        continue;
                    }

                    string name = ResolveFriendlyNameFromRegistryInstance(instanceKey);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(driverKey))
                    {
                        return name;
                    }

                    string instanceDriverKey = instanceKey.GetValue("Driver")?.ToString()?.Trim() ?? string.Empty;
                    if (string.Equals(instanceDriverKey, driverKey, StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }

                    fallbackNames.Add(name);
                }

                return fallbackNames.FirstOrDefault() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        [SupportedOSPlatform("windows")]
        private static string ResolveFriendlyNameFromRegistryInstance(RegistryKey instanceKey)
        {
            using var deviceParametersKey = instanceKey.OpenSubKey("Device Parameters");
            if (deviceParametersKey?.GetValue("EDID") is byte[] edid)
            {
                string edidName = DecodeEdidDisplayName(edid);
                if (!string.IsNullOrWhiteSpace(edidName))
                {
                    return edidName;
                }
            }

            string friendlyName = CleanRegistryDisplayName(instanceKey.GetValue("FriendlyName")?.ToString());
            if (!string.IsNullOrWhiteSpace(friendlyName) && !IsGenericMonitorName(friendlyName))
            {
                return friendlyName;
            }

            string deviceDesc = CleanRegistryDisplayName(instanceKey.GetValue("DeviceDesc")?.ToString());
            return IsGenericMonitorName(deviceDesc) ? string.Empty : deviceDesc;
        }

        private static string DecodeEdidDisplayName(byte[] edid)
        {
            if (edid == null || edid.Length < 128)
            {
                return string.Empty;
            }

            const int descriptorStart = 54;
            const int descriptorLength = 18;
            const int descriptorCount = 4;
            for (int descriptorIndex = 0; descriptorIndex < descriptorCount; descriptorIndex++)
            {
                int offset = descriptorStart + descriptorIndex * descriptorLength;
                if (offset + descriptorLength > edid.Length)
                {
                    break;
                }

                if (edid[offset] != 0x00
                    || edid[offset + 1] != 0x00
                    || edid[offset + 2] != 0x00
                    || edid[offset + 3] != 0xFC
                    || edid[offset + 4] != 0x00)
                {
                    continue;
                }

                var characters = new List<char>();
                for (int index = offset + 5; index < offset + descriptorLength; index++)
                {
                    byte value = edid[index];
                    if (value == 0x00 || value == 0x0A)
                    {
                        break;
                    }

                    if (value >= 0x20 && value <= 0x7E)
                    {
                        characters.Add((char)value);
                    }
                }

                string name = new string(characters.ToArray()).Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }

            return string.Empty;
        }

        private static string CleanRegistryDisplayName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string cleaned = value.Trim();
            int semicolonIndex = cleaned.LastIndexOf(';');
            if (semicolonIndex >= 0 && semicolonIndex + 1 < cleaned.Length)
            {
                cleaned = cleaned[(semicolonIndex + 1)..].Trim();
            }

            if (cleaned.Length >= 2 && cleaned[0] == '(' && cleaned[^1] == ')')
            {
                cleaned = cleaned[1..^1].Trim();
            }

            return cleaned;
        }

        private static string NormalizeMonitorIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string normalized = value.Trim().Replace('/', '\\').Trim('\\').ToUpperInvariant();
            if (normalized.StartsWith(@"MONITOR\", StringComparison.OrdinalIgnoreCase))
            {
                normalized = $@"DISPLAY\{normalized[8..]}";
            }

            return normalized;
        }

        private static string ExtractEnumIdentityFromRegistryPath(string deviceKey)
        {
            if (string.IsNullOrWhiteSpace(deviceKey))
            {
                return string.Empty;
            }

            const string enumMarker = @"ENUM\";
            int enumIndex = deviceKey.IndexOf(enumMarker, StringComparison.OrdinalIgnoreCase);
            if (enumIndex < 0)
            {
                return string.Empty;
            }

            return NormalizeMonitorIdentity(deviceKey[(enumIndex + enumMarker.Length)..]);
        }

        private static string ExtractMonitorHardwareKey(string value)
        {
            string normalized = NormalizeMonitorIdentity(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            var parts = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return string.Empty;
            }

            return parts[1];
        }

        private static string ExtractMonitorDriverKey(string deviceKey)
        {
            if (string.IsNullOrWhiteSpace(deviceKey))
            {
                return string.Empty;
            }

            string normalized = deviceKey.Trim().Replace('/', '\\').Trim('\\');
            const string marker = @"CONTROL\CLASS\";
            int markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return string.Empty;
            }

            string driverKey = normalized[(markerIndex + marker.Length)..].Trim('\\');
            var parts = driverKey.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2
                ? $@"{parts[0]}\{parts[1]}"
                : string.Empty;
        }

        private static bool IsGenericMonitorName(string monitorName)
        {
            return string.IsNullOrWhiteSpace(monitorName)
                || string.Equals(monitorName.Trim(), "Generic PnP Monitor", StringComparison.OrdinalIgnoreCase);
        }

        private static DisplayDevice CreateDisplayDevice()
        {
            return new DisplayDevice
            {
                Cb = Marshal.SizeOf<DisplayDevice>()
            };
        }

        private static DevMode CreateDevMode()
        {
            return new DevMode
            {
                Size = (short)Marshal.SizeOf<DevMode>()
            };
        }

        private static int AngleToOrientation(int angle)
        {
            int normalizedAngle = ((angle % 360) + 360) % 360;
            return normalizedAngle switch
            {
                0 => DmdoDefault,
                90 => Dmdo90,
                180 => Dmdo180,
                270 => Dmdo270,
                _ => -1
            };
        }
    }
}
