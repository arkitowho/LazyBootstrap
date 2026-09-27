using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LazyBootstrap.Services;

internal static class DisplayRegression
{
    public static int RunLive()
    {
        var service = new LiveDisplayService();
        for (int index = 0; index < 10; index++)
        {
            var desktop = service.ReadDesktop();
            var result = service.GetDisplays();
            Assert(desktop.Succeeded && result.Succeeded, "本机枚举失败：" + desktop.ErrorMessage + result.ErrorMessage);
            Assert(desktop.Displays.All(expected => result.Displays.Any(actual =>
                string.Equals(expected.DeviceName, actual.DeviceName, StringComparison.OrdinalIgnoreCase))), "启动器遗漏活动桌面输出");
            if (index == 0) Console.WriteLine($"本机活动输出：{result.Displays.Count}；适配器来源：{result.AdapterCount}；桌面来源：{result.DesktopCount}");
        }
        Console.WriteLine("通过: 真实 Windows 枚举连续比对 10 次");
        return 0;
    }

    private sealed class LiveDisplayService : WindowsDisplayConfigurationService
    {
        public DisplayDiscoveryResult ReadDesktop() => EnumerateDesktopDisplays();
    }

    public static int RunAll()
    {
        int failed = 0;
        Check("空枚举不能被当作成功结果", () =>
        {
            var result = new EmptyDisplayService().GetDisplays();
            Assert(!result.Succeeded, "未检测到任何屏幕却报告成功，界面会接受空列表");
        }, ref failed);
        Check("桌面枚举补齐遗漏输出并去重", () =>
        {
            var service = new FakeDisplayService { Adapters = new[] { "DISPLAY1" }, Desktop = new[] { "DISPLAY1", "DISPLAY2", "DISPLAY2" } };
            var result = service.GetDisplays();
            Assert(result.Succeeded && result.Displays.Count == 2, "桌面输出未合并或重复");
            Assert(result.Displays.Any(display => display.DeviceName == "DISPLAY2"), "漏掉桌面已识别的第二屏");
        }, ref failed);
        Check("单个输出的信息读取异常不阻断后续屏幕", () =>
        {
            var service = new FakeDisplayService { Adapters = new[] { "DISPLAY1", "DISPLAY2" }, Desktop = new[] { "DISPLAY1", "DISPLAY2" }, BrokenMetadata = "DISPLAY1" };
            var result = service.GetDisplays();
            Assert(result.Status == DisplayDiscoveryStatus.Partial && result.Displays.Count == 2, "异常导致丢失输出或没有报告部分失败");
        }, ref failed);
        Check("镜像驱动和停用输出不会进入可配置列表", () =>
        {
            var service = new FakeDisplayService { Adapters = new[] { "DISPLAY1", "MIRROR", "DISABLED" }, Desktop = new[] { "DISPLAY1", "MIRROR" } };
            var result = service.GetDisplays();
            Assert(result.Displays.Count == 1 && result.Displays[0].DeviceName == "DISPLAY1", "错误加入镜像或停用输出");
        }, ref failed);
        Check("失败或部分检测保留上次列表，完整检测允许移除断开屏幕", () =>
        {
            var catalog = new DisplayCatalog();
            catalog.Update(new(new[] { Display("a", "DISPLAY1"), Display("b", "DISPLAY2") }));
            catalog.Update(new(Array.Empty<DisplayInfo>()));
            Assert(catalog.Displays.Count == 2 && catalog.StatusMessage.Length > 0, "空结果清空了有效列表");
            catalog.Update(new(new[] { Display("c", "DISPLAY3") }, "暂时失败"));
            Assert(catalog.Displays.Count == 3, "部分结果丢失原有输出");
            catalog.Update(new(new[] { Display("a", "DISPLAY1") }));
            Assert(catalog.Displays.Count == 1, "确认断开的输出没有移除");
        }, ref failed);
        Check("断开重连保持身份，编号变化后重新匹配", () =>
        {
            var missing = DisplayCatalog.Resolve(new[] { Display("other", "DISPLAY1") }, "saved", "", 0);
            Assert(!missing.IsAvailable && missing.PersistentId == "saved", "缺失目标被替换为其他屏幕");
            var connected = DisplayCatalog.Resolve(new[] { Display("saved", "DISPLAY8") }, missing.PersistentId, "", 0);
            Assert(connected.DeviceName == "DISPLAY8", "重新连接仍沿用旧编号");
        }, ref failed);
        Check("身份暂时退化为设备名时不覆盖已保存身份", () =>
        {
            var selected = DisplayCatalog.Resolve(new[] { Display("DISPLAY1", "DISPLAY1") }, "saved", "", 0);
            Assert(!selected.IsAvailable && selected.PersistentId == "saved", "临时设备名覆盖稳定身份");
        }, ref failed);
        Check("旧索引等待迟到屏幕，旧设备名兼容稳定身份", () =>
        {
            Assert(DisplayCatalog.Resolve(new[] { Display("a", "DISPLAY1") }, "", "1", 0) == null, "旧索引被提前映射到第一屏");
            Assert(DisplayCatalog.Resolve(new[] { Display("a", "DISPLAY1"), Display("b", "DISPLAY2") }, "", "1", 0).PersistentId == "b", "旧索引未恢复");
            Assert(DisplayCatalog.Resolve(new[] { Display("a", @"\\.\DISPLAY1") }, @"\\.\DISPLAY1", "", 0).PersistentId == "a", "旧设备名不能匹配");
        }, ref failed);
        Check("启动首次一屏，手动刷新后第二屏进入同一个目录", () => RunAsync(async () =>
        {
            using var coordinator = new DisplayRefreshCoordinator();
            var service = new FakeDisplayService { Adapters = new[] { "DISPLAY1" }, Desktop = new[] { "DISPLAY1" } };
            var catalog = new DisplayCatalog();
            catalog.Update(await coordinator.RunAsync(service.GetDisplays));
            Assert(catalog.Displays.Count == 1, "首次检测结果错误");
            service.Desktop = new[] { "DISPLAY1", "DISPLAY2" };
            Assert(catalog.Displays.Count == 1, "未手动刷新时列表改变");
            catalog.Update(await coordinator.RunAsync(service.GetDisplays));
            Assert(catalog.Displays.Count == 2, "手动刷新后列表仍只有首次结果");
        }), ref failed);
        Check("显示事务持有期间刷新和模式查询等待，释放后执行", () => RunAsync(async () =>
        {
            using var coordinator = new DisplayRefreshCoordinator();
            using var lease = await coordinator.EnterTransactionAsync();
            int calls = 0;
            var refresh = coordinator.RunAsync(() => Interlocked.Increment(ref calls));
            var modes = coordinator.RunAsync(() => Interlocked.Increment(ref calls));
            Assert(!refresh.IsCompleted && !modes.IsCompleted && calls == 0, "原生查询与事务发生并发");
            lease.Dispose();
            await Task.WhenAll(refresh, modes);
            Assert(calls == 2, "事务结束未执行等待请求");
        }), ref failed);
        Check("启动和游戏运行期间暂停查询，退出清理完成后才恢复", () => RunAsync(async () =>
        {
            using var coordinator = new DisplayRefreshCoordinator();
            int calls = 0;
            coordinator.SetLaunchState(true, false, true);
            await ExpectCanceled(coordinator.RunAsync(() => ++calls));
            coordinator.SetLaunchState(false, true, false);
            await ExpectCanceled(coordinator.RunAsync(() => ++calls));
            // The game has exited, but the launcher has not left its launching state yet.
            coordinator.SetLaunchState(true, false, false);
            await ExpectCanceled(coordinator.RunAsync(() => ++calls));
            // Failure UI may already be idle while the launch workflow is still restoring displays.
            coordinator.SetLaunchState(false, false, true);
            await ExpectCanceled(coordinator.RunAsync(() => ++calls));
            Assert(calls == 0 && coordinator.IsPaused, "暂停期间仍执行了显示查询");
            coordinator.SetLaunchState(false, false, false);
            Assert(await coordinator.RunAsync(() => ++calls) == 1 && !coordinator.IsPaused, "退出启动状态后未恢复");
        }), ref failed);
        Check("进入启动状态取消已排队查询，不会在游戏期间补执行", () => RunAsync(async () =>
        {
            using var coordinator = new DisplayRefreshCoordinator();
            using var lease = await coordinator.EnterTransactionAsync();
            int calls = 0;
            var queued = coordinator.RunAsync(() => ++calls);
            coordinator.SetLaunchState(true, false, true);
            lease.Dispose();
            await ExpectCanceled(queued);
            await coordinator.WaitForQueriesToFinishAsync(CancellationToken.None);
            Assert(calls == 0, "暂停前排队的查询仍访问了显示驱动");
        }), ref failed);
        Check("等待在途原生查询结束并丢弃旧结果，恢复后仅接受新结果", () => RunAsync(async () =>
        {
            using var coordinator = new DisplayRefreshCoordinator();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var query = coordinator.RunAsync(() =>
            {
                entered.Set();
                if (!release.Wait(5000)) throw new Exception("等待测试释放超时");
                return "old";
            });
            Assert(entered.Wait(5000), "查询未开始");
            coordinator.SetLaunchState(true, false, true);
            var drained = coordinator.WaitForQueriesToFinishAsync(CancellationToken.None);
            Assert(!drained.IsCompleted, "原生查询仍在执行就允许启动游戏");
            coordinator.SetLaunchState(false, false, false);
            release.Set();
            await ExpectCanceled(query);
            await drained;
            Assert(await coordinator.RunAsync(() => "new") == "new", "恢复后仍被旧取消状态影响");
        }), ref failed);
        Check("关闭时丢弃正在返回的检测结果", () => RunAsync(async () =>
        {
            using var coordinator = new DisplayRefreshCoordinator();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var query = coordinator.RunAsync(() =>
            {
                entered.Set();
                if (!release.Wait(5000)) throw new Exception("等待测试释放超时");
                return new DisplayDiscoveryResult(new[] { Display("a", "DISPLAY1") });
            });
            Assert(entered.Wait(5000), "检测未开始");
            coordinator.Dispose();
            release.Set();
            try { await query; throw new Exception("关闭后仍返回可应用的结果"); }
            catch (OperationCanceledException) { }
        }), ref failed);
        return failed;
    }

    private static void RunAsync(Func<Task> action) => action().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();

    private static async Task ExpectCanceled(Task task)
    {
        try { await task; throw new Exception("查询应被取消"); }
        catch (OperationCanceledException) { }
    }

    private static DisplayInfo Display(string id, string device) => new() { PersistentId = id, DeviceName = device, FriendlyName = "同型号显示器" };

    private sealed class FakeDisplayService : WindowsDisplayConfigurationService
    {
        public string[] Adapters = Array.Empty<string>();
        public string[] Desktop = Array.Empty<string>();
        public string BrokenMetadata;
        protected override bool TryEnumDisplayDevices(string name, uint index, ref DisplayDevice device)
        {
            if (name != null)
            {
                if (name == BrokenMetadata) throw new InvalidOperationException("模拟驱动信息读取失败");
                return false;
            }
            if (index >= Adapters.Length) return false;
            device.DeviceName = Adapters[index];
            device.StateFlags = device.DeviceName == "MIRROR" ? 9 : device.DeviceName == "DISABLED" ? 0 : 1;
            return true;
        }
        protected override DisplayDiscoveryResult EnumerateDesktopDisplays() => new(Desktop.Select(name => Display(name, name)).ToArray());
    }

    private static void Check(string name, Action test, ref int failed)
    {
        try { test(); Console.WriteLine("通过: " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("失败: " + name + " — " + ex.Message); }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class EmptyDisplayService : WindowsDisplayConfigurationService
    {
        protected override bool TryEnumDisplayDevices(string name, uint index, ref DisplayDevice device) => false;
        protected override DisplayDiscoveryResult EnumerateDesktopDisplays() => new(Array.Empty<DisplayInfo>());
    }
}
