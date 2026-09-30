using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LazyBootstrap.Services;
using LazyBootstrap.Serialization;

internal static class DisplayWorkflowRegression
{
    public static int RunAll()
    {
        int failed = 0;
        foreach (int? refreshRate in new int?[] { null, 120 })
        {
            Check($"显示事务按模式指定刷新率并完整恢复：{refreshRate?.ToString() ?? "游戏配置"}", async () =>
            {
                var service = new RecordingDisplayService();
                var transaction = new DisplaySettingsTransactionCoordinator(service);
                var result = transaction.Apply(new[] { new DisplaySettingsRequest("主屏", "DISPLAY1", 90, 720, 1280, refreshRate) });
                Assert(result.Succeeded && result.RestoreStates.Count == 1, "应用失败或未保留快照");
                Assert(service.Calls.Count == 2 && service.Calls[0].Flags == 2 && service.Calls[1].Flags == 1, "没有先校验再应用");
                foreach (var call in service.Calls)
                {
                    Assert(((call.Fields & 0x00400000) != 0) == refreshRate.HasValue, "系统刷新率字段与模式不匹配");
                    Assert((call.Fields & 0x00180080) == 0x00180080 && call.Width == 720 && call.Height == 1280 && call.Orientation == 1,
                        "旋转或分辨率未正常应用");
                    Assert(call.Frequency == (refreshRate ?? 60), "刷新率值错误");
                }
                Assert(service.RefreshRate == (refreshRate ?? 60), "默认模式更改了系统刷新率");
                var restored = transaction.Restore(result.RestoreStates);
                Assert(restored.Succeeded && service.Width == 1920 && service.Height == 1080 && service.Orientation == 0 && service.RefreshRate == 60,
                    "未还原完整原始显示状态");
                Assert((service.Calls[^1].Fields & 0x00400000) != 0, "恢复未包含原始刷新率");
                await Task.CompletedTask;
            }, ref failed);
        }
        Check("可选刷新率仍拒绝无效参数，失败校验不应用", async () =>
        {
            var service = new RecordingDisplayService();
            Assert(!service.ApplyDisplaySettings("DISPLAY1", 0, 1920, 1080, 0).Succeeded, "接受零刷新率");
            Assert(!service.ApplyDisplaySettings("DISPLAY1", 0, 1920, 1080, -1).Succeeded, "接受负刷新率");
            Assert(!service.ApplyDisplaySettings("DISPLAY1", 0, 0, 1080, null).Succeeded, "接受无效分辨率");
            Assert(service.Calls.Count == 0, "无效参数调用了原生接口");
            service.FailValidation = true;
            var result = new DisplaySettingsTransactionCoordinator(service).Apply(new[] { new DisplaySettingsRequest("主屏", "DISPLAY1", 0, 1280, 720, null) });
            Assert(!result.Succeeded && service.Calls.Count == 1 && service.Width == 1920, "校验失败仍应用了设置");
            await Task.CompletedTask;
        }, ref failed);
        Check("部分检测允许使用正常目标，但不以其他输出替代缺失目标", async () =>
        {
            var main = new DisplayInfo { PersistentId = "main", DeviceName = "DISPLAY1" };
            var result = new DisplayDiscoveryResult(new[] { main }, "另一个输出的信息读取失败");
            Assert(DisplayCatalog.ResolveFresh(result, main)?.DeviceName == "DISPLAY1", "部分结果阻断了正常目标");
            var missing = new DisplayInfo { PersistentId = "missing", DeviceName = "DISPLAY2" };
            Assert(DisplayCatalog.ResolveFresh(result, missing)?.IsAvailable != true, "缺失目标被替换");
            Assert(DisplayCatalog.ResolveFresh(new(Array.Empty<DisplayInfo>()), main) == null, "整体失败仍接受旧目标");
            await Task.CompletedTask;
        }, ref failed);

        Check("事务枚举期间取消等待原生返回，但不接受其结果", async () =>
        {
            using var refresh = new DisplayRefreshCoordinator();
            using var cancellation = new CancellationTokenSource();
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var lease = await refresh.EnterTransactionAsync(cancellation.Token);
            var discovery = refresh.DiscoverForTransactionAsync(() =>
            {
                entered.Set();
                Assert(release.Wait(5000), "枚举释放超时");
                return new DisplayDiscoveryResult(new[] { new DisplayInfo { PersistentId = "a", DeviceName = "DISPLAY1" } });
            }, cancellation.Token);
            Assert(entered.Wait(5000), "枚举未开始");
            cancellation.Cancel();
            Assert(!discovery.IsCompleted, "未等待原生调用结束");
            release.Set();
            await ExpectCanceled(discovery);
        }, ref failed);

        Check("等待显示事务锁时取消，不进入应用", async () =>
        {
            using var refresh = new DisplayRefreshCoordinator();
            using var lease = await refresh.EnterTransactionAsync();
            using var cancellation = new CancellationTokenSource();
            var waiting = refresh.EnterTransactionAsync(cancellation.Token);
            cancellation.Cancel();
            await ExpectCanceled(waiting);
        }, ref failed);

        foreach (int? refreshRate in new int?[] { null, 60 })
        foreach (bool exitRestore in new[] { false, true })
        {
            Check($"原生应用期间停止必须等待还原，退出还原={exitRestore}，系统刷新率={refreshRate?.ToString() ?? "不指定"}", async () =>
            {
                using var service = new ControlledDisplayService();
                using var cancellation = new CancellationTokenSource();
                var transaction = new DisplaySettingsTransactionCoordinator(service);
                var lifetime = new LaunchWorkflowLifetime();
                DisplaySettingsTransactionResult result = null;
                int cleanupCalls = 0;
                var workflow = lifetime.RunAsync(async () =>
                {
                    result = await Task.Run(() => transaction.Apply(Requests(refreshRate), cancellation.Token));
                    // Normal-exit preference deliberately differs from the unconditional canceled-transaction rollback.
                    if (exitRestore && result.Succeeded) transaction.Restore(result.RestoreStates);
                });
                Assert(service.Entered.Wait(5000), "应用未开始");
                Task Cleanup()
                {
                    Assert(result != null, "停止先于快照交接");
                    Assert(service.Width == 1920, "停止完成时显示模式未还原");
                    cleanupCalls++;
                    return Task.CompletedTask;
                }
                var stopping = lifetime.StopAsync(cancellation.Cancel, Cleanup);
                var repeated = lifetime.StopAsync(cancellation.Cancel, Cleanup);
                Assert(ReferenceEquals(stopping, repeated) && !stopping.IsCompleted && lifetime.IsBusy, "重复停止没有合并，或提前结束");
                bool restarted = false;
                await lifetime.RunAsync(() => { restarted = true; return Task.CompletedTask; });
                Assert(!restarted, "停止期间允许重新启动");
                service.Release.Set();
                await stopping;
                await workflow;
                Assert(!result.Succeeded && result.Cancelled && result.RestoreStates.Count == 0 && cleanupCalls == 1 && !lifetime.IsBusy, "停止清理状态错误或取消标记丢失");
            }, ref failed);
        }

        Check("还原失败保留快照和诊断，重试成功后才清空", async () =>
        {
            using var service = new ControlledDisplayService { FailRestore = true };
            using var cancellation = new CancellationTokenSource();
            var transaction = new DisplaySettingsTransactionCoordinator(service);
            var apply = Task.Run(() => transaction.Apply(Requests(), cancellation.Token));
            Assert(service.Entered.Wait(5000), "应用未开始");
            cancellation.Cancel();
            service.Release.Set();
            var result = await apply;
            Assert(result.Cancelled && result.RestoreStates.Count == 1 && result.Messages.Count > 0 && service.Width == 1280, "还原失败丢失快照、取消标记或诊断");
            var failedRestore = transaction.Restore(result.RestoreStates);
            Assert(!failedRestore.Succeeded && failedRestore.RestoreStates.Count == 1, "重试失败却清空快照");
            service.FailRestore = false;
            var restored = transaction.Restore(failedRestore.RestoreStates);
            Assert(restored.Succeeded && restored.RestoreStates.Count == 0 && service.Width == 1920, "成功重试未还原");
        }, ref failed);

        Check("关闭清理等待原生任务并在工作流失败时仍执行", async () =>
        {
            var lifetime = new LaunchWorkflowLifetime();
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool cleaned = false;
            var workflow = lifetime.RunAsync(async () => { await release.Task; throw new InvalidOperationException("模拟失败"); });
            var closing = lifetime.StopAsync(() => { }, () => { cleaned = true; return Task.CompletedTask; });
            Assert(!closing.IsCompleted && !cleaned, "关闭清理未等待工作流");
            release.SetResult();
            try { await workflow; } catch (InvalidOperationException) { }
            try { await closing; } catch (InvalidOperationException) { }
            Assert(cleaned && !lifetime.IsBusy, "工作流异常阻断了关闭清理");
        }, ref failed);

        Check("启用检测取消和失败不写配置，恢复后只保存一次", async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "display-init-" + Guid.NewGuid().ToString("N") + ".toml");
            try
            {
                File.WriteAllText(path, AppConfigDefaults.CreateDefaultConfigText());
                string original = File.ReadAllText(path);
                var store = new AppConfigStore(path, null);
                var initialization = new DisplayInitialization();
                initialization.Begin();
                long generation = initialization.Generation;
                int writes = 0;
                void Save()
                {
                    store.WriteSection("Display", new Dictionary<string, string>
                    {
                        ["displayconfigure"] = "true", ["maindisplayid"] = "monitor",
                        ["mainresolution"] = "1920x1080", ["mainrefresh"] = "120"
                    });
                    writes++;
                }
                foreach (var outcome in new[] { DisplayRefreshOutcome.Canceled, DisplayRefreshOutcome.Failed, DisplayRefreshOutcome.Deferred })
                    Assert(!initialization.TrySave(generation, outcome, true, Save), "未完成的检测写入配置");
                Assert(!initialization.TrySave(generation, DisplayRefreshOutcome.Completed, false, Save), "缺失目标写入配置");
                Assert(File.ReadAllText(path) == original && initialization.IsPending, "取消后丢失待保存状态或修改文件");
                Assert(initialization.TrySave(generation, DisplayRefreshOutcome.Completed, true, Save), "有效检测未补写");
                initialization.TrySave(generation, DisplayRefreshOutcome.Completed, true, Save);
                Assert(writes == 1 && store.ReadString("Display", "mainrefresh") == "120", "重复保存或保存错误模式");
            }
            finally { File.Delete(path); }
            await Task.CompletedTask;
        }, ref failed);

        Check("关闭或重新启用使旧检测回调失效，保存失败可重试", async () =>
        {
            var initialization = new DisplayInitialization();
            initialization.Begin();
            long old = initialization.Generation;
            initialization.Cancel();
            int writes = 0;
            Assert(!initialization.TrySave(old, DisplayRefreshOutcome.Completed, true, () => writes++), "关闭后仍保存");
            initialization.Begin();
            Assert(!initialization.TrySave(old, DisplayRefreshOutcome.Completed, true, () => writes++), "旧启用覆盖了新操作");
            try { initialization.TrySave(initialization.Generation, DisplayRefreshOutcome.Completed, true, () => throw new IOException()); }
            catch (IOException) { }
            Assert(initialization.IsPending && writes == 0, "失败的写入被当成已保存");
            Assert(initialization.TrySave(initialization.Generation, DisplayRefreshOutcome.Completed, true, () => writes++), "保存失败无法重试");
            await Task.CompletedTask;
        }, ref failed);
        return failed;
    }

    private static DisplaySettingsRequest[] Requests(int? refreshRate = 60) => new[] { new DisplaySettingsRequest("主屏", "DISPLAY1", 0, 1280, 720, refreshRate) };

    private sealed class RecordingDisplayService : WindowsDisplayConfigurationService
    {
        public readonly List<(int Flags, int Fields, int Frequency, int Width, int Height, int Orientation)> Calls = new();
        public int Width = 1920, Height = 1080, Orientation, RefreshRate = 60;
        public bool FailValidation;
        protected override bool TryEnumDisplaySettings(string deviceName, int modeIndex, ref DevMode mode)
        {
            mode.PelsWidth = Width;
            mode.PelsHeight = Height;
            mode.DisplayOrientation = Orientation;
            mode.DisplayFrequency = RefreshRate;
            return true;
        }
        protected override int TryChangeDisplaySettings(string deviceName, ref DevMode mode, int flags)
        {
            Calls.Add((flags, mode.Fields, mode.DisplayFrequency, mode.PelsWidth, mode.PelsHeight, mode.DisplayOrientation));
            if (flags == 2) return FailValidation ? -1 : 0;
            Width = mode.PelsWidth;
            Height = mode.PelsHeight;
            Orientation = mode.DisplayOrientation;
            if ((mode.Fields & 0x00400000) != 0) RefreshRate = mode.DisplayFrequency;
            return 0;
        }
    }

    private sealed class ControlledDisplayService : WindowsDisplayConfigurationService, IDisposable
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        public int Width = 1920;
        public bool FailRestore;
        protected override bool TryEnumDisplaySettings(string deviceName, int modeIndex, ref DevMode mode)
        {
            mode.PelsWidth = Width;
            mode.PelsHeight = Width == 1920 ? 1080 : 720;
            mode.DisplayFrequency = 60;
            return true;
        }
        protected override int TryChangeDisplaySettings(string deviceName, ref DevMode mode, int flags)
        {
            if (flags == 2) return 0;
            if (mode.PelsWidth == 1280)
            {
                Entered.Set();
                if (!Release.Wait(5000)) return -1;
            }
            else if (FailRestore) return -1;
            Width = mode.PelsWidth;
            return 0;
        }
        public void Dispose() { Release.Set(); Entered.Dispose(); Release.Dispose(); }
    }

    private static async Task ExpectCanceled(Task task)
    {
        try { await task; throw new Exception("应取消任务"); }
        catch (OperationCanceledException) { }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Check(string name, Func<Task> action, ref int failed)
    {
        try { action().WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); Console.WriteLine("通过: " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("失败: " + name + " — " + ex.Message); }
    }
}
