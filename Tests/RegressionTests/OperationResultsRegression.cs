using System;
using LazyBootstrap.Services;

internal static class OperationResultsRegression
{
    public static int RunAll()
    {
        int failed = 0;
        Check("停止成功与无需结束进程均可确认完成", () =>
        {
            var result = new ManualStopResult([new("spice64", 1, true), new("asphyxia-core-x64", 0, true)], 0);
            Assert(result.Succeeded, "已退出或没有进程时误报停止失败");
        }, ref failed);
        Check("成功数量不掩盖仍在运行的进程或枚举失败", () =>
        {
            foreach (int terminated in new[] { 0, 2 })
            {
                var result = new ManualStopResult([new("spice64", terminated, false), new("asphyxia-core-x64", 1, true)], 0);
                Assert(!result.Succeeded && result.Message.Contains("spice64"), "未确认结束的进程被计为停止成功");
                Assert(!result.Message.Contains("asphyxia-core-x64"), "错误地报告已结束的进程仍在运行");
            }
        }, ref failed);
        Check("进程与显示器失败合并为同一结果", () =>
        {
            var result = new ManualStopResult([new("spice64", 0, false, true)], 1);
            Assert(!result.Succeeded && result.Message.Contains("管理员") && result.Message.Contains("显示器"), "汇总丢失权限或还原失败信息");
        }, ref failed);
        Check("进程均退出但显示器待还原时不报告成功", () =>
        {
            var result = new ManualStopResult([new("spice64", 1, true)], 1);
            Assert(!result.Succeeded && result.Message.Contains("显示器"), "还原失败被停止成功掩盖");
        }, ref failed);
        Check("运行库全部成功才报告整体完成", () =>
        {
            var result = new RuntimeInstallResult();
            result.Record("DirectX", 0);
            result.Record("VC++", 0);
            Assert(result.Succeeded && result.ShouldNotify && result.Installed.Count == 2, "完整安装结果错误");
        }, ref failed);
        Check("部分安装失败不计入已安装项", () =>
        {
            var result = new RuntimeInstallResult();
            result.Record("DirectX", 1);
            result.Record("VC++", 0);
            Assert(!result.Succeeded && result.Failed.Contains("DirectX") && !result.Installed.Contains("DirectX"), "安装失败后仍报告成功");
            Assert(result.Message.Contains("DirectX") && result.Message.Contains("VC++"), "部分结果未同时说明失败和成功项目");
        }, ref failed);
        Check("缺少安装程序与零项安装不报告成功", () =>
        {
            var result = new RuntimeInstallResult();
            Assert(!result.Succeeded, "没有安装任何项目仍报告成功");
            result.Missing.Add("VC++");
            result.Record("DirectX", 0);
            Assert(!result.Succeeded && result.Message.Contains("VC++"), "缺少组件时声称全部完成");
        }, ref failed);
        Check("主动取消授权不追加通知或安装完成", () =>
        {
            var result = new RuntimeInstallResult();
            result.Record("DirectX", 0);
            result.Record("VC++", -1223);
            Assert(result.Cancelled && !result.ShouldNotify && !result.Succeeded && result.Failed.Count == 0, "取消被当成失败或成功通知");
        }, ref failed);
        Check("取消后仍保留此前实际安装失败", () =>
        {
            var result = new RuntimeInstallResult();
            result.Record("DirectX", -1);
            result.Record("VC++", -1223);
            Assert(result.ShouldNotify && !result.Succeeded && result.Failed.Contains("DirectX"), "取消隐藏了实际安装失败");
        }, ref failed);
        return failed;
    }

    private static void Check(string name, Action test, ref int failed)
    {
        try { test(); Console.WriteLine($"通过：{name}"); }
        catch (Exception ex) { failed++; Console.WriteLine($"失败：{name}：{ex.Message}"); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
