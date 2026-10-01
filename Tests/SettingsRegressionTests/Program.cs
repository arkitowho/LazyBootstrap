using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LazyBootstrap;
using LazyBootstrap.UI;
using LazyBootstrap.Services;
using LazyBootstrap.Serialization;
using SukiUI.Controls;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("-cfg"))
        {
            var path = args[Array.IndexOf(args, "-cfgpath") + 1];
            var document = XDocument.Load(path);
            foreach (var option in document.Descendants("option").Where(option =>
                (string?)option.Attribute("name") is "sp2x-sdvxnosub" or "sdvxlandscape"))
                option.SetAttributeValue("value", (string?)option.Attribute("value") == "/ENABLED" ? "" : "/ENABLED");
            document.Save(path);
            File.WriteAllText("editor-finished", "done");
            return 0;
        }

        AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
        using var cancellation = new CancellationTokenSource();
        var exitCode = 1;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await RunAsync(args.Contains("--display-preview"));
                if (!args.Contains("--display-preview"))
                {
                    Console.WriteLine("PASS: spicecfg exit updates subdisplay and landscape toggles in both directions on the settings page.");
                    Console.WriteLine("PASS: display compatibility mode persists, switches XML refresh overrides, and preserves output bindings on failure.");
                }
                Console.WriteLine("PASS: display preview layout and screen selection work without a transparency mask.");
                exitCode = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { cancellation.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(cancellation.Token);
        return exitCode;
    }

    private static async Task RunAsync(bool previewOnly = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "LazyBootstrap-Settings-" + Guid.NewGuid().ToString("N"));
        var contents = Path.Combine(root, "contents");
        Directory.CreateDirectory(Path.Combine(contents, "lazy"));
        try
        {
            foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
                File.Copy(file, Path.Combine(contents, Path.GetFileName(file)));
            File.Copy(Environment.ProcessPath!, Path.Combine(contents, "spice64.exe"), true);
            var xml = Path.Combine(contents, "lazy", "spicetools.xml");
            File.WriteAllText(xml, "<games><game name=\"Sound Voltex\"><options><option name=\"sp2x-sdvxnosub\" value=\"/ENABLED\"/><option name=\"sdvxlandscape\" value=\"/ENABLED\"/><option name=\"url\" value=\"http://localhost:8083\"/></options></game></games>");
            var assembly = typeof(App).Assembly;
            var defaults = assembly.GetType("LazyBootstrap.Serialization.AppConfigDefaults")!;
            var config = (string)defaults.GetMethod("CreateDefaultConfigText", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            Assert(config.Contains("compatibilitymode = \"false\""), "New configs do not default compatibility mode to false.");
            // Exercise the missing-key fallback used by existing installations.
            config = config.Replace("compatibilitymode = \"false\"", "");
            var configPath = Path.Combine(root, "config.toml");
            File.WriteAllText(configPath, config);
            var paths = Activator.CreateInstance(assembly.GetType("LazyBootstrap.FileSystem.LauncherPaths")!, root, root, configPath)!;
            using var composition = (IDisposable)Activator.CreateInstance(assembly.GetType("LazyBootstrap.Application.ApplicationComposition")!, paths)!;
            var window = (MainWindow)composition.GetType().GetMethod("CreateMainWindow")!.Invoke(composition, null)!;
            window.Opened -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), window,
                typeof(MainWindow).GetMethod("OnWindowOpened", PrivateInstance)!);
            window.ShowInTaskbar = false;
            window.Position = new PixelPoint(-32000, -32000);
            window.Show();
            if (previewOnly)
            {
                var display = typeof(MainWindow).GetField("_displayState", PrivateInstance)!.GetValue(window)!;
                Set(display, "IsDisplayConfigurationEnabled", true);
                Set(display, "IsDualDisplay", true);
                Invoke(window, "InitializeDisplayLayoutControls");
                await VerifyDisplayPreviewAsync(window, Path.Combine(Environment.CurrentDirectory, "outputs", "display-preview.png"));
                Invoke(window, "StopDisplayAnimation");
                window.Hide();
                return;
            }
            var settings = typeof(MainWindow).GetField("_settingsState", PrivateInstance)!.GetValue(window)!;
            await InvokeAsync(window, "LoadSettingsStateAsync", settings);
            await InvokeAsync(window, "LoadDeferredSettingsStateAsync", settings, false);
            Invoke(window, "ApplyDeferredSettingsToUi");
            await Task.Delay(100);
            var subdisplay = window.FindControl<ToggleSwitch>("DisableSubDisplayToggleSwitch")!;
            var landscape = window.FindControl<ToggleSwitch>("LandscapeModeToggleSwitch")!;
            Assert(subdisplay.IsChecked == true && landscape.IsChecked == true, "Initial toggles were not enabled.");
            subdisplay.IsChecked = false;
            landscape.IsChecked = false;
            Assert(XDocument.Load(xml).Descendants("option").Where(option =>
                (string?)option.Attribute("name") is "sp2x-sdvxnosub" or "sdvxlandscape")
                .All(option => (string?)option.Attribute("value") == ""), "Launcher toggle changes were not saved.");
            subdisplay.IsChecked = true;
            landscape.IsChecked = true;
            var menu = window.FindControl<SukiSideMenu>("MainSideMenu")!;
            menu.SelectedItem = menu.Items.OfType<SukiSideMenuItem>().Single(item =>
                item.Tag is ShellPage.Settings);
            foreach (var expectedEnabled in new[] { false, true })
            {
                File.Delete(Path.Combine(contents, "editor-finished"));
                Invoke(window, "OnEditConfigClick", null!, new Avalonia.Interactivity.RoutedEventArgs());
                var busy = (ICollection)typeof(MainWindow).GetField("_busyEntries", PrivateInstance)!.GetValue(window)!;
                var timeout = DateTime.UtcNow.AddSeconds(15);
                while (busy.Count > 0 && DateTime.UtcNow < timeout) await Task.Delay(20);
                Assert(busy.Count == 0, "Editor workflow timed out.");
                Assert(File.Exists(Path.Combine(contents, "editor-finished")), "Editor did not save the fixture.");
                Assert(subdisplay.IsChecked == expectedEnabled, "DisableSubDisplay retained its previous state after spicecfg exit.");
                Assert(landscape.IsChecked == expectedEnabled, "LandscapeMode retained its previous state after spicecfg exit.");
                await Task.Delay(100);
                Assert(XDocument.Load(xml).Descendants("option").Where(option =>
                    (string?)option.Attribute("name") is "sp2x-sdvxnosub" or "sdvxlandscape")
                    .All(option => (string?)option.Attribute("value") == (expectedEnabled ? "/ENABLED" : "")),
                    "Refreshing toggles overwrote the edited XML.");
                Assert(menu.SelectedItem is SukiSideMenuItem { Tag: ShellPage.Settings }, "Reloading settings changed the selected page.");
            }
            await RunDisplayConfigurationAsync(window, paths, xml);
            await RunDisplayRefreshRegressionAsync(window, xml, configPath);
            Invoke(window, "StopDisplayAnimation");
            window.Hide();
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task RunDisplayConfigurationAsync(MainWindow window, object paths, string xml)
    {
        var state = typeof(MainWindow).GetField("_displayState", PrivateInstance)!.GetValue(window)!;
        var store = typeof(MainWindow).GetField("_appConfig", PrivateInstance)!.GetValue(window)!;
        await InvokeAsync(window, "WarmDisplayStateAsync", state);
        Invoke(window, "InitializeDisplayLayoutControls");
        var toggle = window.FindControl<ToggleSwitch>("DisplayCompatibilityModeToggleSwitch")!;
        Assert(toggle.IsChecked == false && !(bool)Get(state, "CompatibilityMode"), "Old configs enabled compatibility mode.");
        toggle.IsChecked = true;
        Assert(ReadCompatibility(store), "Compatibility mode was not saved while display configuration was disabled.");
        await InvokeAsync(window, "WarmDisplayStateAsync", state);
        Invoke(window, "ApplyDisplayStateToUi");
        Assert(toggle.IsChecked == true, "Reload did not restore compatibility mode.");
        toggle.IsChecked = false;

        object Choice(string device, string name)
        {
            var assembly = typeof(MainWindow).Assembly;
            var display = Activator.CreateInstance(assembly.GetType("LazyBootstrap.Services.DisplayInfo")!)!;
            Set(display, "DeviceName", device);
            Set(display, "PersistentId", name);
            Set(display, "FriendlyName", name);
            return Activator.CreateInstance(typeof(MainWindow).GetNestedType("DisplayChoiceOption", BindingFlags.NonPublic)!, display, name)!;
        }
        var main = Choice("DISPLAY1", "测试主屏");
        var sub = Choice("DISPLAY2", "测试副屏");
        var displays = (IList)Get(state, "Displays");
        displays.Add(main);
        displays.Add(sub);
        foreach (string target in new[] { "Main", "Sub" })
        {
            ((IList)Get(state, target + "Resolutions")).Add("1920x1080");
            ((IList)Get(state, target + "RefreshRates")).Add(target == "Main" ? "120" : "75");
            Set(state, "Selected" + target + "Display", target == "Main" ? main : sub);
            Set(state, "Selected" + target + "Resolution", "1920x1080");
            Set(state, "Selected" + target + "RefreshRate", target == "Main" ? "120" : "75");
            Set(state, target + "ModeQuerySucceeded", true);
        }
        Set(state, "IsDisplayConfigurationEnabled", true);
        Set(state, "IsDualDisplay", true);
        Invoke(window, "UpdateDisplayStartupInfo", state);
        Invoke(window, "ApplyDisplayStateToUi");
        Invoke(window, "PersistSelectionState", state);
        await VerifyDisplayPreviewAsync(window);
        AssertRefresh(xml, "120", "75");
        Assert(Option(xml, "mainmonitor") == "DISPLAY1" && Option(xml, "sdvxsubmonitor") == "DISPLAY2", "Output bindings changed.");
        Assert(Option(xml, "url") == "http://localhost:8083", "Unrelated XML option changed.");

        object Request() => Invoke(window, "BuildDisplayConfigurationRequest")!;
        var buildRequest = typeof(MainWindow).GetMethod("TryBuildRequest", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (bool compatibility in new[] { true, false })
        {
            toggle.IsChecked = compatibility;
            Assert(ReadCompatibility(store) == compatibility && (bool)Get(Request(), "CompatibilityMode") == compatibility,
                "Toggle state was not persisted or captured in the launch request.");
            AssertRefresh(xml, compatibility ? "" : "120", compatibility ? "" : "75");
            Assert(window.FindControl<TextBlock>("MainStartupInfoTextBlock")!.Text!.Contains(compatibility ? "系统设置" : "Spice2x 游戏配置"),
                "Startup summary does not identify the refresh rate source.");
            var requestType = typeof(MainWindow).Assembly.GetType("LazyBootstrap.Services.DisplaySettingsRequest")!;
            var requests = (IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(requestType))!;
            var messages = new System.Collections.Generic.List<string>();
            var rotation = Get(Get(state, "SelectedMainRotation"), "Angle");
            Assert((bool)buildRequest.Invoke(null, new object[] { main, rotation, "1920x1080", "120", compatibility, "主显示器", requests, messages })!,
                "Cannot build display request.");
            Assert(Equals(requests[0]!.GetType().GetProperty("RefreshRate")!.GetValue(requests[0]), compatibility ? (object)120 : null),
                "Native request has the wrong optional refresh rate.");
        }

        // Use the actual mode-change handler without native discovery or display changes.
        var modes = window.FindControl<ComboBox>("DisplayModeComboBox")!;
        modes.SelectedIndex = 0;
        AssertRefresh(xml, "120", "");
        Assert(Option(xml, "sdvxsubmonitor") == "", "Single-display mode retained the sub output binding.");
        modes.SelectedIndex = 1;
        AssertRefresh(xml, "120", "75");

        var sync = typeof(MainWindow).GetMethods(PrivateInstance).Single(method => method.Name == "SyncSpiceMonitorOverrides"
            && method.GetParameters()[0].ParameterType == state.GetType());
        bool Sync() => (bool)sync.Invoke(window, new[] { state })!;
        Set(state, "SelectedMainRefreshRate", "0120");
        Assert(Sync(), "Cannot normalize a numeric refresh selection.");
        AssertRefresh(xml, "120", "75");
        Set(state, "SelectedMainRefreshRate", "120");
        var timestamp = File.GetLastWriteTimeUtc(xml);
        Assert(Sync() && File.GetLastWriteTimeUtc(xml) == timestamp, "Unchanged values rewrote the XML.");
        var original = File.ReadAllText(xml);
        Set(state, "SelectedMainRefreshRate", "无效");
        Assert(!Sync() && File.ReadAllText(xml) == original, "Invalid refresh rate changed the XML.");
        Set(state, "SelectedMainRefreshRate", "120");
        Set(state, "SelectedMainDisplay", null);
        Assert(!Sync() && File.ReadAllText(xml) == original, "Missing selection cleared existing output bindings.");
        Set(state, "SelectedMainDisplay", main);

        using (var held = new FileStream(xml, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert(!Sync(), "Locked XML was reported as successfully synchronized.");
            // Saving through the UI persistence path must handle XML failure without throwing.
            Invoke(window, "PersistSelectionState", state);
        }
        Assert(File.ReadAllText(xml) == original, "Failed XML save damaged the file.");
        File.Move(xml, xml + ".saved");
        try { Assert(!Sync(), "Missing XML was reported as synchronized."); }
        finally { File.Move(xml + ".saved", xml); }

        var write = store.GetType().GetMethod("WriteString")!;
        var activePath = typeof(MainWindow).GetMethod("GetActiveSpiceXmlPathForMonitorSync", PrivateInstance)!;
        write.Invoke(store, new object[] { "Setting", "use-system-config", "true" });
        Assert((string)activePath.Invoke(window, null)! == (string)paths.GetType().GetMethod("GetSpiceXmlPath")!.Invoke(paths, null)!,
            "System config mode chose the local XML path.");
        write.Invoke(store, new object[] { "Setting", "use-system-config", "false" });
        Assert((string)activePath.Invoke(window, null)! == xml, "Local config mode did not restore the local XML path.");
        // Do not write the user's real system XML during this test.
        Assert(Sync(), "Cannot resync after restoring the local config path.");

        Set(state, "IsDisplayConfigurationEnabled", false);
        await InvokeAsync(window, "PersistGeneralSettingsAsync", state);
        AssertRefresh(xml, "", "");
        Assert(Option(xml, "mainmonitor") == "" && Option(xml, "sdvxsubmonitor") == "", "Disabled display configuration retained overrides.");
        Assert((string)Get(state, "SelectedMainRefreshRate") == "120" && (string)Get(state, "SelectedSubRefreshRate") == "75",
            "Disabling configuration lost refresh rate selections.");
    }

    private static async Task RunDisplayRefreshRegressionAsync(MainWindow window, string xml, string configPath)
    {
        var state = typeof(MainWindow).GetField("_displayState", PrivateInstance)!.GetValue(window)!;
        var store = (AppConfigStore)typeof(MainWindow).GetField("_appConfig", PrivateInstance)!.GetValue(window)!;
        var originalService = typeof(MainWindow).GetField("_displayConfigurationService", PrivateInstance)!.GetValue(window)!;
        var originalTransactions = typeof(MainWindow).GetField("_displaySettingsTransactionCoordinator", PrivateInstance)!.GetValue(window)!;
        var service = new DisplayModeFixture();
        service.Compatible["DISPLAY1"] = new[] { (1920, 1080, 0), (1920, 1080, 1), (1920, 1080, 50),
            (1920, 1080, 59), (1920, 1080, 60), (1920, 1080, 75), (1920, 1080, 60), (1280, 720, 92) };
        service.Raw["DISPLAY1"] = new[] { (1920, 1080, 60), (1920, 1080, 73), (1920, 1080, 489), (1920, 1080, 900) };
        service.RejectedRates.Add(900);
        service.Compatible["DISPLAY2"] = new[] { (1920, 1080, 60) };
        service.Raw["DISPLAY2"] = new[] { (1920, 1080, 87) };
        object Choice(string device) => Activator.CreateInstance(typeof(MainWindow).GetNestedType("DisplayChoiceOption", BindingFlags.NonPublic)!,
            new DisplayInfo { DeviceName = device, PersistentId = device, FriendlyName = device }, device)!;
        object Request() => Invoke(window, "BuildDisplayConfigurationRequest")!;
        Task Refresh(bool persist = false) => InvokeAsync(window, "HandleConfigurationChangedAsync", state, true, true, persist);
        string[] Rates(string target) => ((IList)Get(state, target + "RefreshRates")).Cast<string>().ToArray();
        async Task AssertRejected()
        {
            foreach (bool preview in new[] { false, true })
            {
                var beforeXml = File.ReadAllText(xml);
                var result = await (Task<DisplaySettingsTransactionResult>)Invoke(window, "ApplyDisplayTransactionAsync", Request(), preview, CancellationToken.None)!;
                Assert(!result.Succeeded && !string.IsNullOrEmpty(result.UserMessage), "Invalid refresh selection was applied.");
                Assert(File.ReadAllText(xml) == beforeXml, "Validation failure changed XML.");
            }
        }
        Invoke(window, "InitializeDisplayServices", service, new DisplaySettingsTransactionCoordinator(service));
        try
        {
            Set(state, "IsDisplayConfigurationEnabled", true);
            Set(state, "IsDualDisplay", true);
            Set(state, "CompatibilityMode", false);
            Invoke(window, "SynchronizeDisplayDetectionWithConfiguration");
            foreach (string target in new[] { "Main", "Sub" })
            {
                Set(state, "Selected" + target + "Display", Choice(target == "Main" ? "DISPLAY1" : "DISPLAY2"));
                Set(state, "Selected" + target + "Rotation", ((IList)Get(state, "Rotations"))[0]);
                Set(state, "Selected" + target + "Resolution", "1920x1080");
                Set(state, "Selected" + target + "RefreshRate", target == "Main" ? "59" : "400");
            }
            await Refresh();
            Invoke(window, "ApplyDisplayStateToUi");
            Assert(Rates("Main").SequenceEqual(new[] { "60", "73", "75", "489" }), "Main refresh candidates are not filtered, sorted or complete: " + string.Join(",", Rates("Main")));
            Assert(Rates("Sub").SequenceEqual(new[] { "60", "87" }), "Sub display inherited main refresh rates.");
            Assert((string)Get(state, "SelectedMainRefreshRate") == "60" && (string)Get(state, "SelectedSubRefreshRate") == "60",
                "Old unsupported rates did not select the lowest supported integer.");
            Assert(store.ReadString("Display", "mainrefresh") == "60", "Automatic replacement was not saved.");
            AssertRefresh(xml, "60", "60");
            Assert((string)Invoke(window, "ValidateDisplayRefreshRates", Request())! == "", "Supported refresh selection failed preflight.");
            Assert(window.FindControl<Button>("PreviewDisplaySettingsButton")!.IsEnabled, "Valid selections disabled preview.");

            foreach (int rotation in new[] { 0, 90, 180, 270 })
            {
                var resolution = rotation is 90 or 270 ? "1080x1920" : "1920x1080";
                var options = Invoke(window, "RefreshDisplayOptions", Get(state, "SelectedMainDisplay"), rotation, resolution, "59", true)!;
                Assert(((System.Collections.Generic.IEnumerable<string>)Get(options, "RefreshRates")).SequenceEqual(Rates("Main")),
                    "Rotation changed the refresh candidates.");
            }
            var lower = Invoke(window, "RefreshDisplayOptions", Get(state, "SelectedMainDisplay"), 0, "1280x720", "60", false)!;
            Assert(((System.Collections.Generic.IEnumerable<string>)Get(lower, "RefreshRates")).SequenceEqual(new[] { "92" }),
                "Refresh rates were mixed between resolutions.");

            foreach (bool compatibility in new[] { true, false })
            {
                Set(state, "CompatibilityMode", compatibility);
                await InvokeAsync(window, "PersistGeneralSettingsAsync", state);
                AssertRefresh(xml, compatibility ? "" : "60", compatibility ? "" : "60");
            }
            service.Raw["DISPLAY1"] = service.Raw["DISPLAY1"].Append((1920, 1080, 101)).ToArray();
            await Refresh();
            Assert(Rates("Main").Contains("101"), "A newly registered custom refresh rate was not detected.");
            Set(state, "SelectedMainRefreshRate", "101");
            await InvokeAsync(window, "HandleConfigurationChangedAsync", state, false, false, true);
            AssertRefresh(xml, "101", "60");

            foreach (string invalid in new[] { "59", "999" })
            {
                Set(state, "SelectedMainRefreshRate", invalid);
                await AssertRejected();
            }
            var savedConfig = File.ReadAllText(configPath);
            var savedXml = File.ReadAllText(xml);
            Set(state, "SelectedMainRefreshRate", "59");
            service.FailDevice = "DISPLAY1";
            await Refresh(persist: true);
            Invoke(window, "ApplyDisplayStateToUi");
            Assert(!Rates("Main").Contains("59") && (string)Get(state, "SelectedMainRefreshRate") == "59",
                "Partial query injected an old rate or overwrote saved intent.");
            Assert(window.FindControl<ComboBox>("MainRefreshRateComboBox")!.SelectedIndex == -1,
                "UI displayed a different selection after a partial query.");
            Assert(File.ReadAllText(configPath) == savedConfig && File.ReadAllText(xml) == savedXml,
                "Partial query rewrote persisted configuration.");
            Assert(!window.FindControl<Button>("PreviewDisplaySettingsButton")!.IsEnabled, "Partial query enabled preview.");
            Set(state, "SelectedMainRefreshRate", "75");
            await AssertRejected();

            service.FailDevice = "";
            service.CurrentRate = 59;
            service.Compatible["DISPLAY1"] = new[] { (1920, 1080, 59) };
            service.Raw["DISPLAY1"] = new[] { (1920, 1080, 59) };
            await Refresh(persist: true);
            Invoke(window, "ApplyDisplayStateToUi");
            Assert(Rates("Main").Length == 0 && (string)Get(state, "SelectedMainRefreshRate") == "",
                "No eligible modes retained an invalid refresh selection.");
            Assert(((IList)Get(state, "MainResolutions")).Contains("1920x1080"), "Refresh filter removed the resolution.");
            Assert(((string)Get(state, "MainDiagnosticsTooltip")).Contains("60 Hz"), "Missing eligible modes had no Chinese guidance.");
            Assert(File.ReadAllText(configPath) == savedConfig && File.ReadAllText(xml) == savedXml,
                "Empty refresh list overwrote saved configuration.");
            foreach (bool compatibility in new[] { false, true })
            {
                Set(state, "CompatibilityMode", compatibility);
                await AssertRejected();
            }
            await InvokeAsync(window, "PersistGeneralSettingsAsync", state);
            AssertRefresh(xml, "", "");
            Assert(store.ReadBool("Display", "compatibilitymode", false) && store.ReadString("Display", "mainrefresh") == "101",
                "Compatibility toggle did not persist or replaced an invalid mode selection.");
            Set(state, "CompatibilityMode", false);
            await InvokeAsync(window, "PersistGeneralSettingsAsync", state);

            service.CurrentRate = 60;
            service.Compatible["DISPLAY1"] = new[] { (1920, 1080, 60), (1920, 1080, 75) };
            service.Raw["DISPLAY1"] = new[] { (1920, 1080, 73) };
            store.WriteString("Display", "mainrefresh", "59");
            store.WriteString("Display", "subrefresh", "87");
            await InvokeAsync(window, "WarmDisplayStateAsync", state);
            Assert(store.ReadString("Display", "mainrefresh") == "60", "Reload did not migrate a saved 59 Hz selection.");
            AssertRefresh(xml, "60", "87");

            savedConfig = File.ReadAllText(configPath);
            savedXml = File.ReadAllText(xml);
            Set(state, "SelectedMainDisplay", Choice(""));
            Set(state, "SelectedMainRefreshRate", "59");
            await Refresh(persist: true);
            Assert(Rates("Main").Length == 0 && File.ReadAllText(configPath) == savedConfig && File.ReadAllText(xml) == savedXml,
                "Disconnected output injected old refresh rates or changed persisted configuration.");
            Assert(service.Tests.All(call => call.Flags == 2), "Detection applied a system display setting.");
            Console.WriteLine("PASS: integer refresh filtering, custom modes, automatic migration, preflight rejection and failure persistence.");
        }
        finally
        {
            Set(state, "IsDisplayConfigurationEnabled", false);
            Invoke(window, "InitializeDisplayServices", originalService, originalTransactions);
        }
    }

    private static async Task VerifyDisplayPreviewAsync(MainWindow window, string? screenshot = null)
    {
        var menu = window.FindControl<SukiSideMenu>("MainSideMenu")!;
        menu.SelectedItem = menu.Items.OfType<SukiSideMenuItem>().Single(item => item.Tag is ShellPage.Display);
        await Task.Delay(350);
        var busyArea = window.FindControl<BusyArea>("DisplayConfigDisabledMask")!;
        var preview = (Grid)busyArea.Content!;
        if (screenshot != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(screenshot)!);
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
            bitmap.Render(window);
            bitmap.Save(screenshot);
        }
        Assert(preview.OpacityMask == null, "Display preview still has a transparency mask.");
        var originalHeight = window.Height;
        try
        {
            foreach (double height in new[] { 700d, originalHeight, 1080d })
            {
                window.Height = height;
                await Task.Delay(150);
                Assert(preview.Bounds.Height > 0 && preview.Bounds.Height <= busyArea.Bounds.Height + 1 && preview.ClipToBounds,
                    "Display preview extends beyond the visible viewport when resized.");
            }
        }
        finally
        {
            window.Height = originalHeight;
            await Task.Delay(150);
        }
        foreach (string target in new[] { "Main", "Sub" })
        {
            var button = window.FindControl<Button>("Select" + target + "ScreenAreaButton")!;
            Assert(button.IsVisible && button.IsEnabled, "Preview screen selection is unavailable.");
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert(window.FindControl<StackPanel>("Panel" + target + "ScreenConfig")!.IsVisible
                && window.FindControl<Avalonia.Controls.Shapes.Ellipse>("Dot" + target + "SelectedRing")!.IsVisible,
                "Preview screen selection did not update the configuration panel.");
        }
        var state = typeof(MainWindow).GetField("_displayState", PrivateInstance)!.GetValue(window)!;
        Set(state, "IsDualDisplay", false);
        Invoke(window, "ApplyDisplayStateToUi");
        Assert(!window.FindControl<Button>("SelectSubScreenAreaButton")!.IsVisible,
            "Single-display preview exposes sub-screen selection.");
        Set(state, "IsDualDisplay", true);
        Set(state, "IsDisplayConfigurationEnabled", false);
        Invoke(window, "ApplyDisplayStateToUi");
        Assert(busyArea.IsBusy && busyArea.OpacityMask == null && preview.OpacityMask == null,
            "Disabled display configuration has an unexpected transparency mask or missing prompt.");
        Set(state, "IsDisplayConfigurationEnabled", true);
        Invoke(window, "ApplyDisplayStateToUi");
    }

    private static object Get(object target, string property) => target.GetType().GetProperty(property, BindingFlags.Public | PrivateInstance)!.GetValue(target)!;
    private static void Set(object target, string property, object? value) => target.GetType().GetProperty(property, BindingFlags.Public | PrivateInstance)!.SetValue(target, value);
    private static bool ReadCompatibility(object store) => (bool)store.GetType().GetMethod("ReadBool")!
        .Invoke(store, new object[] { "Display", "compatibilitymode", false })!;
    private static string Option(string xml, string name) => (string?)XDocument.Load(xml).Descendants("game")
        .Single(game => (string?)game.Attribute("name") == "Sound Voltex").Element("options")!.Elements("option")
        .SingleOrDefault(option => (string?)option.Attribute("name") == name)?.Attribute("value") ?? "";
    private static void AssertRefresh(string xml, string main, string sub) =>
        Assert(Option(xml, "graphics-force-refresh") == main && Option(xml, "graphics-force-refresh-sub") == sub,
            "XML refresh overrides do not match the selected mode.");

    private static object? Invoke(MainWindow window, string name, params object[] args) =>
        typeof(MainWindow).GetMethods(PrivateInstance | BindingFlags.Static).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(window, args);
    private static async Task InvokeAsync(MainWindow window, string name, params object[] args) =>
        await (Task)Invoke(window, name, args)!;
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
