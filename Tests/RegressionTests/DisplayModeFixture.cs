using System;
using System.Collections.Generic;
using System.Linq;
using LazyBootstrap.Services;

internal sealed class DisplayModeFixture : WindowsDisplayConfigurationService
{
    public readonly Dictionary<string, (int Width, int Height, int Rate)[]> Compatible = new();
    public readonly Dictionary<string, (int Width, int Height, int Rate)[]> Raw = new();
    public readonly HashSet<int> RejectedRates = new();
    public readonly List<(string Device, int Flags, int Rate, int Width, int Height)> Tests = new();
    public readonly List<(string Device, int Flags, int Fields, int Rate)> Changes = new();
    public bool AllowApply { get; set; }
    public readonly List<(string Device, int Flags, int Index)> Enumerations = new();
    public string FailDevice = string.Empty;
    public int FailFlags = 2;
    public int FailIndex = 1;
    public int CurrentRate = 60;
    public string[] Devices = { "DISPLAY1", "DISPLAY2" };

    protected override bool TryEnumDisplaySettingsEx(string deviceName, int modeIndex, ref DevMode mode, int flags)
    {
        Enumerations.Add((deviceName, flags, modeIndex));
        if (deviceName == FailDevice && flags == FailFlags && modeIndex == FailIndex)
            throw new InvalidOperationException("Controlled enumeration failure");
        var source = flags == 0 ? Compatible : Raw;
        if (!source.TryGetValue(deviceName, out var modes) || modeIndex >= modes.Length) return false;
        var candidate = modes[modeIndex];
        mode.PelsWidth = candidate.Width;
        mode.PelsHeight = candidate.Height;
        mode.DisplayFrequency = candidate.Rate;
        mode.BitsPerPel = 32;
        return true;
    }

    protected override bool TryEnumDisplaySettings(string deviceName, int modeIndex, ref DevMode mode)
    {
        if (modeIndex != -1 || !Devices.Contains(deviceName)) return false;
        mode.PelsWidth = 1920;
        mode.PelsHeight = 1080;
        mode.DisplayFrequency = CurrentRate;
        return true;
    }

    protected override int TryChangeDisplaySettings(string deviceName, ref DevMode mode, int flags)
    {
        if (flags != 2 && !AllowApply) throw new InvalidOperationException("Detection attempted to apply a display mode");
        Changes.Add((deviceName, flags, mode.Fields, mode.DisplayFrequency));
        if (flags != 2) return 0;
        Tests.Add((deviceName, flags, mode.DisplayFrequency, mode.PelsWidth, mode.PelsHeight));
        return RejectedRates.Contains(mode.DisplayFrequency) ? -2 : 0;
    }

    protected override bool TryEnumDisplayDevices(string name, uint index, ref DisplayDevice device)
    {
        if (name != null || index >= Devices.Length) return false;
        device.DeviceName = Devices[index];
        device.DeviceString = index == 0 ? "内置显示器" : "自定义模式显示器";
        device.StateFlags = 1 | (index == 0 ? 4 : 0);
        return true;
    }

    protected override DisplayDiscoveryResult EnumerateDesktopDisplays() => new(Devices.Select(name =>
        new DisplayInfo { DeviceName = name, PersistentId = name, FriendlyName = name }).ToArray());
}
