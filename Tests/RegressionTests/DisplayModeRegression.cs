using System;
using System.Linq;
using LazyBootstrap.Services;

internal static class DisplayModeRegression
{
    public static int RunAll()
    {
        try
        {
            var service = new DisplayModeFixture();
            service.Compatible["DISPLAY1"] = new[] { (1920, 1080, 0), (1920, 1080, 1), (1920, 1080, 50),
                (1920, 1080, 59), (1920, 1080, 60), (1920, 1080, 75), (1920, 1080, 60), (1280, 720, 92) };
            service.Raw["DISPLAY1"] = new[] { (1920, 1080, 60), (1920, 1080, 73), (1920, 1080, 489),
                (1920, 1080, 900), (1920, 1080, 59), (0, 1080, 400) };
            service.RejectedRates.Add(900);
            var result = service.GetSupportedModes("DISPLAY1");
            Assert(result.Succeeded, result.ErrorMessage);
            var rates = result.Modes.Where(mode => mode.Width == 1920 && mode.Height == 1080 &&
                mode.RefreshRate >= WindowsDisplayConfigurationService.MinimumSelectableRefreshRate).Select(mode => mode.RefreshRate);
            Assert(rates.SequenceEqual(new[] { 60, 73, 75, 489 }), "自定义值、无上限、排序或去重错误");
            Assert(result.Modes.Any(mode => mode.RefreshRate == 59) && result.Modes.Any(mode => mode.RefreshRate == 50),
                "低刷新率模式丢失，影响分辨率目录");
            Assert(!result.Modes.Any(mode => mode.RefreshRate <= 1 || mode.RefreshRate == 900), "加入了无效或拒绝的模式");
            Assert(service.Tests.Select(call => call.Rate).SequenceEqual(new[] { 73, 489, 900 }), "探测了固定值或重复模式");
            Assert(service.Tests.All(call => call.Flags == 2), "检测实际修改了显示状态");
            Assert(service.Enumerations.Any(call => call.Flags == 0 && call.Index == 8) &&
                service.Enumerations.Any(call => call.Flags == 2 && call.Index == 6), "未完整枚举普通和原始模式");

            service.Compatible["DISPLAY2"] = new[] { (1920, 1080, 60) };
            service.Raw["DISPLAY2"] = new[] { (1920, 1080, 87) };
            Assert(service.GetSupportedModes("DISPLAY2").Modes.Select(mode => mode.RefreshRate).SequenceEqual(new[] { 60, 87 }),
                "主、副输出模式混用");
            service.Raw["DISPLAY2"] = new[] { (1920, 1080, 101) };
            Assert(service.GetSupportedModes("DISPLAY2").Modes.Any(mode => mode.RefreshRate == 101), "重新检测未读取新增模式");

            service.FailDevice = "DISPLAY1";
            result = service.GetSupportedModes("DISPLAY1");
            Assert(!result.Succeeded && result.Modes.Any(mode => mode.RefreshRate == 75), "异常丢弃可读模式或被误判完整");
            service.FailFlags = 0;
            service.FailIndex = 0;
            result = service.GetSupportedModes("DISPLAY1");
            Assert(!result.Succeeded && result.Modes.Any(mode => mode.RefreshRate == 489), "普通枚举异常阻断原始模式来源");
            service.Compatible.Clear();
            service.Raw.Clear();
            Assert(!service.GetSupportedModes("DISPLAY2").Succeeded, "枚举失败被当前状态补齐掩盖");
            Assert(!service.GetSupportedModes("").Succeeded, "缺失输出名称仍返回成功");
            Console.WriteLine("通过: 完整枚举自定义刷新率、原始模式校验、无上限及部分失败");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("失败: 显示模式枚举：" + ex.Message);
            return 1;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
