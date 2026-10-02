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
using Avalonia.VisualTree;
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
            if (File.Exists("refresh-values"))
            {
                var values = File.ReadAllLines("refresh-values");
                foreach (var (name, value) in new[] { ("graphics-force-refresh", values[0]), ("graphics-force-refresh-sub", values[1]) })
                    document.Descendants("option").Single(option => (string?)option.Attribute("name") == name).SetAttributeValue("value", value);
            }
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
            Assert(config.Contains("compatibilitymode = \"false\""), "New configs lack a boolean compatibility mode default.");
            // Exercise the missing-key fallback used by existing installations.
            config = config.Replace("compatibilitymode = \"false\"", "")
                .Replace("maincustomrefresh = \"false\"", "").Replace("subcustomrefresh = \"false\"", "");
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
            await VerifySettingsBorderAlignmentAsync(window);
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
        Assert(!(bool)Get(state, "MainCustomRefresh") && !(bool)Get(state, "SubCustomRefresh"), "Old configs enabled custom refresh input.");
        foreach (string target in new[] { "Main", "Sub" })
        {
            var combo = window.FindControl<ComboBox>(target + "RefreshRateComboBox")!;
            Assert(Equals(combo.Items[0], "不修改") && Equals(combo.SelectedItem, "不修改"),
                "Empty default XML did not select unchanged.");
        }
        toggle.IsChecked = true;
        Assert(ReadCompatibility(store), "Disabled display configuration lost a boolean compatibility setting.");
        await InvokeAsync(window, "WarmDisplayStateAsync", state);
        Invoke(window, "ApplyDisplayStateToUi");
        Assert(toggle.IsChecked == true && !(bool)Get(state, "IsDualDisplay") && (bool)Get(state, "ExitRestore"),
            "Disabled settings did not preserve the boolean option while restoring single mode.");
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
        Assert(((AppConfigStore)store).ReadString("Display", "mainrefresh", "missing") == "missing" &&
            ((AppConfigStore)store).ReadString("Display", "subrefresh", "missing") == "missing", "Default save created TOML refresh records.");
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
            if (!compatibility)
                Assert(((AppConfigStore)store).ReadString("Display", "mainrefresh") == "" &&
                    ((AppConfigStore)store).ReadString("Display", "subrefresh") == "", "Turning compatibility off retained TOML refresh values.");
            string startupInfo = window.FindControl<TextBlock>("MainStartupInfoTextBlock")!.Text!;
            Assert(!startupInfo.Contains("Spice2x") && startupInfo.Contains("系统设置") == compatibility,
                "Startup summary retained the Spice2x label or lost the compatibility source.");
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
        AssertRefresh(xml, "120", "75");
        foreach (string key in new[] { "subdisplayid", "subrotation", "subresolution", "subrefresh" })
            Assert(((AppConfigStore)store).ReadString("Display", key) == "", "Single mode retained TOML sub setting: " + key);
        Assert(Option(xml, "sdvxsubmonitor") == "", "Single-display mode retained the sub output binding.");
        modes.SelectedIndex = 1;
        AssertRefresh(xml, "120", "75");
        Assert(((AppConfigStore)store).ReadString("Display", "subdisplayid") == "测试副屏", "Dual mode did not save the sub monitor.");

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
        foreach (string key in new[] { "mode",
            "maindisplayid", "subdisplayid", "mainrotation", "subrotation", "mainresolution", "subresolution", "mainrefresh", "subrefresh" })
            Assert(((AppConfigStore)store).ReadString("Display", key) == "", "Disabling retained TOML display setting: " + key);
        Assert(((AppConfigStore)store).ReadString("Display", "exitrestore") == "true", "Disabling lost the default exit restore preference.");
        foreach (string key in new[] { "displayconfigure", "compatibilitymode", "maincustomrefresh", "subcustomrefresh" })
            Assert(((AppConfigStore)store).ReadString("Display", key) == "false", "Disabling cleared a boolean display setting: " + key);
        AssertRefresh(xml, "120", "75");
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
            Set(state, "SelectedMainDisplay", null);
            Set(state, "SelectedSubDisplay", null);
            Invoke(window, "ApplyDisplayStateToUi");
            var configureToggle = window.FindControl<ToggleSwitch>("DisplayConfigEnabledToggleSwitch")!;
            configureToggle.IsChecked = true;
            var firstEnableTimeout = DateTime.UtcNow.AddSeconds(5);
            while (!store.ReadBool("Display", "displayconfigure", false) && DateTime.UtcNow < firstEnableTimeout) await Task.Delay(20);
            Assert(store.ReadBool("Display", "displayconfigure", false) && store.ReadString("Display", "mode") == "single",
                "First enable did not save single mode after detection.");
            Assert(window.FindControl<ToggleSwitch>("ExitRestoreToggleSwitch")!.IsChecked == true, "First enable did not check exit restore by default.");
            foreach (string name in new[] { "DisplayCompatibilityModeToggleSwitch", "MainCustomRefreshRateToggleSwitch", "SubCustomRefreshRateToggleSwitch" })
                Assert(window.FindControl<ToggleSwitch>(name)!.IsChecked == false, "First enable checked an extra option: " + name);
            foreach (string key in new[] { "subdisplayid", "subrotation", "subresolution", "subrefresh" })
                Assert(store.ReadString("Display", key) == "", "First single-mode enable wrote sub settings: " + key);
            configureToggle.IsChecked = false;
            Assert(store.ReadString("Display", "displayconfigure") == "false" && !(bool)Get(state, "IsDualDisplay"),
                "Disabling the actual UI did not clear the enabled flag and reset single mode.");
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
            await Refresh(persist: true);
            Invoke(window, "ApplyDisplayStateToUi");
            Assert(Rates("Main").SequenceEqual(new[] { "不修改", "60", "73", "75", "489" }), "Main refresh candidates are not filtered, sorted or complete: " + string.Join(",", Rates("Main")));
            Assert(Rates("Sub").SequenceEqual(new[] { "不修改", "60", "87" }), "Sub display inherited main refresh rates.");
            Assert((string)Get(state, "SelectedMainRefreshRate") == "60" && (string)Get(state, "SelectedSubRefreshRate") == "60",
                "Old unsupported rates did not select the lowest supported integer.");
            Assert(store.ReadString("Display", "mainrefresh") == "", "Default save created a TOML refresh record after clearing.");
            AssertRefresh(xml, "60", "60");
            Assert((string)Invoke(window, "ValidateDisplayRefreshRates", Request())! == "", "Supported refresh selection failed preflight.");
            Assert(window.FindControl<Button>("PreviewDisplaySettingsButton")!.IsEnabled, "Valid selections disabled preview.");

            foreach (int rotation in new[] { 0, 90, 180, 270 })
            {
                var resolution = rotation is 90 or 270 ? "1080x1920" : "1920x1080";
                var options = Invoke(window, "RefreshDisplayOptions", Get(state, "SelectedMainDisplay"), rotation, resolution, "59", true, false, false)!;
                Assert(((System.Collections.Generic.IEnumerable<string>)Get(options, "RefreshRates")).SequenceEqual(Rates("Main")),
                    "Rotation changed the refresh candidates.");
            }
            var lower = Invoke(window, "RefreshDisplayOptions", Get(state, "SelectedMainDisplay"), 0, "1280x720", "60", false, false, false)!;
            Assert(((System.Collections.Generic.IEnumerable<string>)Get(lower, "RefreshRates")).SequenceEqual(new[] { "不修改", "92" }),
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

            Set(state, "CompatibilityMode", true);
            Invoke(window, "PersistSelectionState", state);
            Set(state, "CompatibilityMode", false);
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
            Assert(Rates("Main").SequenceEqual(new[] { "不修改" }) && (string)Get(state, "SelectedMainRefreshRate") == "",
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
            store.WriteString("Display", "compatibilitymode", "true");
            await InvokeAsync(window, "WarmDisplayStateAsync", state);
            Assert(store.ReadString("Display", "mainrefresh") == "60", "Reload did not migrate a saved 59 Hz selection.");
            AssertRefresh(xml, "", "");
            Assert((string)Get(state, "SelectedSubRefreshRate") == "87", "Compatibility reload did not read TOML independently of XML.");
            Set(state, "CompatibilityMode", false);
            Set(state, "SelectedSubRefreshRate", "87");
            Invoke(window, "PersistSelectionState", state);

            await VerifyCustomRefreshAsync(window, store, state, xml, service);
            await VerifyUnchangedRefreshAsync(window, store, state, xml, service);
            await VerifySpiceRefreshPriorityAsync(window, store, state, xml, service);

            savedConfig = File.ReadAllText(configPath);
            savedXml = File.ReadAllText(xml);
            Set(state, "SelectedMainDisplay", Choice(""));
            Set(state, "SelectedMainRefreshRate", "59");
            await Refresh(persist: true);
            Assert(Rates("Main").SequenceEqual(new[] { "不修改" }) && File.ReadAllText(configPath) == savedConfig && File.ReadAllText(xml) == savedXml,
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

    private static async Task VerifyCustomRefreshAsync(MainWindow window, AppConfigStore store, object state, string xml, DisplayModeFixture service)
    {
        const string warning = "请确保输入的自定义刷新率被显示器支持，否则会启动失败";
        async Task Wait(Func<bool> ready)
        {
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (!ready() && DateTime.UtcNow < timeout) await Task.Delay(20);
            Assert(ready(), "Custom refresh UI persistence timed out.");
            await Task.Delay(30);
        }
        object Request() => Invoke(window, "BuildDisplayConfigurationRequest")!;
        Invoke(window, "ApplyDisplayStateToUi");
        var mainToggle = window.FindControl<ToggleSwitch>("MainCustomRefreshRateToggleSwitch")!;
        var subToggle = window.FindControl<ToggleSwitch>("SubCustomRefreshRateToggleSwitch")!;
        var mainInput = window.FindControl<TextBox>("MainCustomRefreshRateTextBox")!;
        var subInput = window.FindControl<TextBox>("SubCustomRefreshRateTextBox")!;
        mainToggle.IsChecked = true;
        await Wait(() => store.ReadBool("Display", "maincustomrefresh", false));
        Assert(mainInput.IsVisible && !window.FindControl<ComboBox>("MainRefreshRateComboBox")!.IsVisible &&
            !subInput.IsVisible && window.FindControl<ComboBox>("SubRefreshRateComboBox")!.IsVisible, "Custom switch did not replace only its own dropdown.");
        Assert(window.FindControl<TextBlock>("MainCustomRefreshRateWarning")! is { IsVisible: true, Text: warning }, "Custom input warning is missing or incorrect.");
        mainInput.Text = "900";
        await Wait(() => Option(xml, "graphics-force-refresh") == "900");
        AssertRefresh(xml, "900", "87");
        Assert((bool)Get(Request(), "MainCustomRefresh") && !(bool)Get(Request(), "SubCustomRefresh"), "Custom flags were not captured independently.");
        Assert((string)Invoke(window, "ValidateDisplayRefreshRates", Request())! == "", "Custom input was incorrectly restricted to enumerated candidates.");
        await InvokeAsync(window, "HandleConfigurationChangedAsync", state, true, true, false);
        Invoke(window, "ApplyDisplayStateToUi");
        Assert(mainInput.Text == "900", "Redetection replaced custom input.");

        foreach (var invalid in new[] { "", "59", "60.5", "无效", "2147483648" })
        {
            mainInput.Text = invalid;
            await Wait(() => (string)Get(state, "SelectedMainRefreshRate") == invalid);
            Assert(!window.FindControl<Button>("PreviewDisplaySettingsButton")!.IsEnabled, "Invalid custom input enabled preview.");
            Assert(Option(xml, "graphics-force-refresh") == "900", "Invalid input overwrote persisted rate.");
            Assert(((string)Invoke(window, "ValidateDisplayRefreshRates", Request())!).Contains("整数"), "Invalid custom input had no validation message.");
        }
        mainInput.Text = "900";
        await Wait(() => window.FindControl<Button>("PreviewDisplaySettingsButton")!.IsEnabled);
        var compatibility = window.FindControl<ToggleSwitch>("DisplayCompatibilityModeToggleSwitch")!;
        compatibility.IsChecked = true;
        await Wait(() => store.ReadBool("Display", "compatibilitymode", false));
        AssertRefresh(xml, "", "");
        var testArgs = new object[] { Request(), null!, null!, CancellationToken.None };
        Assert(!(bool)Invoke(window, "TryApplyDisplayForLaunch", testArgs)!, "Unsupported custom rate passed Windows compatibility validation.");
        Assert(service.Tests.Any(call => call.Rate == 900 && call.Flags == 2), "Compatibility mode did not test the manually entered rate.");
        compatibility.IsChecked = false;
        await Wait(() => !store.ReadBool("Display", "compatibilitymode", true));
        AssertRefresh(xml, "900", "87");

        subToggle.IsChecked = true;
        await Wait(() => store.ReadBool("Display", "subcustomrefresh", false));
        subInput.Text = "91";
        await Wait(() => Option(xml, "graphics-force-refresh-sub") == "91");
        AssertRefresh(xml, "900", "91");
        Assert(window.FindControl<TextBlock>("SubCustomRefreshRateWarning")! is { IsVisible: true, Text: warning }, "Sub custom input warning is missing.");
        await InvokeAsync(window, "WarmDisplayStateAsync", state);
        Invoke(window, "ApplyDisplayStateToUi");
        Assert(mainToggle.IsChecked == true && subToggle.IsChecked == true && mainInput.Text == "900" && subInput.Text == "91",
            "Reload did not restore custom flags and rates.");
        typeof(MainWindow).GetFields(PrivateInstance).Select(field => field.GetValue(window))
            .OfType<SukiUI.Toasts.ISukiToastManager>().Single().DismissAll();
        await Task.Delay(500);
        double originalWidth = window.Width;
        try
        {
            foreach (double width in new[] { 960d, originalWidth })
            {
                window.Width = width;
                foreach (string target in new[] { "Main", "Sub" })
                {
                    Invoke(window, "OnSelect" + target + "DisplayClick", null!, new Avalonia.Interactivity.RoutedEventArgs());
                    await Task.Delay(150);
                    VerifyRefreshBorderAlignment(window, target);
                }
            }
        }
        finally { window.Width = originalWidth; }
        Invoke(window, "OnSelectMainDisplayClick", null!, new Avalonia.Interactivity.RoutedEventArgs());
        await VerifyDisplayPreviewAsync(window, Path.Combine(Environment.CurrentDirectory, "outputs", "display-custom-refresh.png"));

        mainToggle.IsChecked = false;
        await Wait(() => !store.ReadBool("Display", "maincustomrefresh", true));
        Assert(!mainInput.IsVisible && window.FindControl<ComboBox>("MainRefreshRateComboBox")!.IsVisible &&
            !window.FindControl<TextBlock>("MainCustomRefreshRateWarning")!.IsVisible, "Disabling custom mode did not restore dropdown.");
        Assert(Option(xml, "graphics-force-refresh") == "900" && (string)Get(state, "SelectedSubRefreshRate") == "91",
            "Returning to the dropdown lost the unlisted Spice rate or changed the other screen.");
        subToggle.IsChecked = false;
        await Wait(() => !store.ReadBool("Display", "subcustomrefresh", true));
        AssertRefresh(xml, "900", "91");
        Console.WriteLine("PASS: independent custom refresh switches, text input, validation, XML, compatibility and reload.");
    }

    private static async Task VerifyUnchangedRefreshAsync(MainWindow window, AppConfigStore store, object state, string xml, DisplayModeFixture service)
    {
        const string unchanged = "不修改";
        object Request() => Invoke(window, "BuildDisplayConfigurationRequest")!;
        async Task Wait(Func<bool> ready)
        {
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (!ready() && DateTime.UtcNow < timeout) await Task.Delay(20);
            Assert(ready(), "Unchanged refresh UI persistence timed out.");
            await Task.Delay(30);
        }
        Task Refresh() => InvokeAsync(window, "HandleConfigurationChangedAsync", state, true, true, false);
        var mainCombo = window.FindControl<ComboBox>("MainRefreshRateComboBox")!;
        var subCombo = window.FindControl<ComboBox>("SubRefreshRateComboBox")!;
        var mainToggle = window.FindControl<ToggleSwitch>("MainCustomRefreshRateToggleSwitch")!;
        var subToggle = window.FindControl<ToggleSwitch>("SubCustomRefreshRateToggleSwitch")!;
        Invoke(window, "ApplyDisplayStateToUi");
        mainCombo.SelectedItem = "60";
        await Wait(() => Option(xml, "graphics-force-refresh") == "60");
        subCombo.SelectedItem = "60";
        await Wait(() => Option(xml, "graphics-force-refresh-sub") == "60");
        Assert(Equals(mainCombo.Items[0], unchanged) && Equals(subCombo.Items[0], unchanged), "Unchanged is not first in both dropdowns.");

        mainCombo.SelectedItem = unchanged;
        await Wait(() => Option(xml, "graphics-force-refresh") == "");
        AssertRefresh(xml, "", "60");
        Assert(!mainToggle.IsEnabled && mainToggle.IsChecked == false && subToggle.IsEnabled,
            "Unchanged did not disable only the main custom switch.");
        Assert(mainCombo.IsEnabled && mainCombo.IsVisible && !window.FindControl<TextBox>("MainCustomRefreshRateTextBox")!.IsVisible &&
            !window.FindControl<TextBlock>("MainCustomRefreshRateWarning")!.IsVisible, "Unchanged did not retain the dropdown and hide custom input.");
        mainToggle.IsChecked = true;
        Assert(mainToggle.IsChecked == false && !(bool)Get(state, "MainCustomRefresh"), "Programmatic custom enable bypassed unchanged guard.");
        Assert(!store.ReadBool("Display", "maincustomrefresh", true), "Unchanged persisted an enabled custom flag.");
        Assert(window.FindControl<TextBlock>("MainStartupInfoTextBlock")!.Text!.Contains("刷新率: 不修改") &&
            !window.FindControl<TextBlock>("MainStartupInfoTextBlock")!.Text!.Contains("不修改Hz"), "Unchanged startup summary includes Hz or missing text.");

        mainCombo.SelectedItem = "75";
        await Wait(() => Option(xml, "graphics-force-refresh") == "75");
        Assert(mainToggle.IsEnabled && mainToggle.IsChecked == false, "Numeric selection did not reenable the custom switch in the off state.");
        mainToggle.IsChecked = true;
        await Wait(() => store.ReadBool("Display", "maincustomrefresh", false));
        mainCombo.SelectedItem = unchanged;
        await Wait(() => Option(xml, "graphics-force-refresh") == "" && !store.ReadBool("Display", "maincustomrefresh", true));
        Assert(mainToggle.IsChecked == false && !mainToggle.IsEnabled && mainCombo.IsVisible,
            "Selecting unchanged did not turn off an active custom mode.");

        await InvokeAsync(window, "WarmDisplayStateAsync", state);
        Invoke(window, "ApplyDisplayStateToUi");
        Assert(Equals(mainCombo.SelectedItem, unchanged) && !mainToggle.IsEnabled && subToggle.IsEnabled,
            "Reload lost independent unchanged selection or custom lock.");
        await InvokeAsync(window, "RequestDisplayListRefreshAsync", "UnchangedRegression", true);
        Assert(Equals(mainCombo.SelectedItem, unchanged), "Manual rediscovery lost unchanged selection.");

        var screenCombo = window.FindControl<ComboBox>("MainScreenComboBox")!;
        var originalScreen = screenCombo.SelectedItem;
        screenCombo.SelectedItem = screenCombo.Items.Cast<object>().First(item => !Equals(item, originalScreen));
        await Wait(() => store.ReadString("Display", "maindisplayid") == "DISPLAY2");
        Assert(Equals(mainCombo.SelectedItem, unchanged), "Changing monitor cleared unchanged selection.");
        screenCombo.SelectedItem = originalScreen;
        await Wait(() => store.ReadString("Display", "maindisplayid") == "DISPLAY1");
        service.Compatible["DISPLAY1"] = service.Compatible["DISPLAY1"].Append((1280, 720, 60)).ToArray();
        await Refresh();
        Invoke(window, "ApplyDisplayStateToUi");
        var resolutionCombo = window.FindControl<ComboBox>("MainResolutionComboBox")!;
        resolutionCombo.SelectedItem = "1280x720";
        await Wait(() => store.ReadString("Display", "mainresolution") == "1280x720");
        Assert(Equals(mainCombo.SelectedItem, unchanged), "Changing resolution cleared unchanged selection.");
        resolutionCombo.SelectedItem = "1920x1080";
        await Wait(() => store.ReadString("Display", "mainresolution") == "1920x1080");
        var rotationCombo = window.FindControl<ComboBox>("RotationComboBox")!;
        rotationCombo.SelectedItem = rotationCombo.Items.Cast<object>().Single(item => (int)Get(item, "Angle") == 90);
        await Wait(() => store.ReadString("Display", "mainrotation") == "90");
        Assert(Equals(mainCombo.SelectedItem, unchanged), "Changing rotation cleared unchanged selection.");
        rotationCombo.SelectedItem = rotationCombo.Items.Cast<object>().Single(item => (int)Get(item, "Angle") == 0);
        await Wait(() => store.ReadString("Display", "mainrotation") == "0");

        subCombo.SelectedItem = unchanged;
        await Wait(() => Option(xml, "graphics-force-refresh-sub") == "");
        AssertRefresh(xml, "", "");
        Assert(!subToggle.IsEnabled && subToggle.IsChecked == false, "Sub unchanged custom switch is not locked off.");
        mainCombo.SelectedItem = "60";
        await Wait(() => Option(xml, "graphics-force-refresh") == "60");
        AssertRefresh(xml, "60", "");
        Assert(mainToggle.IsEnabled && !subToggle.IsEnabled, "Sub unchanged affected the main custom switch.");
        mainCombo.SelectedItem = unchanged;
        await Wait(() => Option(xml, "graphics-force-refresh") == "");
        store.WriteString("Display", "maincustomrefresh", "true");
        await InvokeAsync(window, "WarmDisplayStateAsync", state);
        Invoke(window, "ApplyDisplayStateToUi");
        Assert(mainToggle.IsChecked == false && subToggle.IsChecked == false && !mainToggle.IsEnabled && !subToggle.IsEnabled,
            "Reload did not normalize inconsistent custom flags for unchanged.");
        Invoke(window, "PersistSelectionState", state);
        Assert(!store.ReadBool("Display", "maincustomrefresh", true) && !store.ReadBool("Display", "subcustomrefresh", true),
            "Normalized custom flags were not saved as false.");

        var savedModes = service.Compatible["DISPLAY1"];
        var savedRaw = service.Raw["DISPLAY1"];
        service.CurrentRate = 59;
        service.Compatible["DISPLAY1"] = new[] { (1920, 1080, 59) };
        service.Raw["DISPLAY1"] = new[] { (1920, 1080, 59) };
        await Refresh();
        Invoke(window, "ApplyDisplayStateToUi");
        Assert(mainCombo.Items.Cast<string>().SequenceEqual(new[] { unchanged }) && mainCombo.SelectedItem?.ToString() == unchanged &&
            window.FindControl<Button>("PreviewDisplaySettingsButton")!.IsEnabled, "Unchanged cannot be selected with only sub-60 Hz modes.");
        Assert((string)Invoke(window, "ValidateDisplayRefreshRates", Request())! == "", "Unchanged incorrectly required a numeric refresh mode.");

        // All native calls use the fixture: exercise successful launch and preview without changing the desktop.
        service.AllowApply = true;
        var manager = (SukiUI.Dialogs.ISukiDialogManager)typeof(MainWindow).GetField("_dialogManager", PrivateInstance)!.GetValue(window)!;
        SukiUI.Dialogs.SukiDialogManagerEventHandler restorePreview = (_, args) =>
        {
            if (args.Dialog.Title == "显示器预览")
                Dispatcher.UIThread.Post(() => args.Dialog.ActionButtons.OfType<Button>().Single(button => Equals(button.Content, "还原"))
                    .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)));
        };
        manager.OnDialogShown += restorePreview;
        try
        {
            foreach (bool compatibility in new[] { false, true })
            {
                Set(state, "CompatibilityMode", compatibility);
                foreach (bool preview in new[] { false, true })
                {
                    service.Changes.Clear();
                    var result = await (Task<DisplaySettingsTransactionResult>)Invoke(window, "ApplyDisplayTransactionAsync", Request(), preview, CancellationToken.None)!;
                    Assert(result.Succeeded, "Unchanged launch or preview failed: " + string.Join(";", result.Messages));
                    var applies = service.Changes.Where(call => call.Flags == 1).Take(2).ToArray();
                    Assert(applies.Length == 2 && applies.All(call => (call.Fields & 0x00400000) == 0),
                        "Unchanged launch or preview specified a native display frequency.");
                    Assert(applies.All(call => (call.Fields & 0x00080000) != 0 && (call.Fields & 0x00100000) != 0),
                        "Unchanged suppressed resolution changes.");
                    AssertRefresh(xml, "", "");
                }
            }
        }
        finally
        {
            manager.OnDialogShown -= restorePreview;
            service.AllowApply = false;
        }

        async Task AssertRejected()
        {
            var beforeXml = File.ReadAllText(xml);
            service.Changes.Clear();
            foreach (bool preview in new[] { false, true })
            {
                var result = await (Task<DisplaySettingsTransactionResult>)Invoke(window, "ApplyDisplayTransactionAsync", Request(), preview, CancellationToken.None)!;
                Assert(!result.Succeeded && !string.IsNullOrEmpty(result.UserMessage), "Unchanged bypassed display or resolution validation.");
            }
            Assert(service.Changes.All(call => call.Flags == 2) && File.ReadAllText(xml) == beforeXml,
                "Invalid unchanged selection applied settings or changed XML.");
        }
        service.FailDevice = "DISPLAY1";
        await Refresh();
        Invoke(window, "ApplyDisplayStateToUi");
        Assert(mainCombo.SelectedItem?.ToString() == unchanged && !window.FindControl<Button>("PreviewDisplaySettingsButton")!.IsEnabled,
            "Incomplete discovery lost unchanged intent or enabled preview.");
        await AssertRejected();
        service.FailDevice = "";
        Set(state, "SelectedMainResolution", "9999x9999");
        await AssertRejected();
        Set(state, "SelectedMainResolution", "1920x1080");
        var mainDisplay = Get(state, "SelectedMainDisplay");
        Set(state, "SelectedMainDisplay", null);
        await AssertRejected();
        Set(state, "SelectedMainDisplay", mainDisplay);

        service.CurrentRate = 60;
        service.Compatible["DISPLAY1"] = savedModes;
        service.Raw["DISPLAY1"] = savedRaw;
        Set(state, "CompatibilityMode", false);
        await Refresh();
        Invoke(window, "ApplyDisplayStateToUi");
        Set(state, "IsDualDisplay", false);
        Invoke(window, "ApplyDisplayStateToUi");
        Assert(!subCombo.IsEnabled && !subToggle.IsEnabled, "Single-display mode enabled sub refresh controls.");
        Set(state, "IsDualDisplay", true);
        Invoke(window, "ApplyDisplayStateToUi");
        mainCombo.SelectedItem = "60";
        await Wait(() => Option(xml, "graphics-force-refresh") == "60");
        subCombo.SelectedItem = "60";
        await Wait(() => Option(xml, "graphics-force-refresh-sub") == "60");
        Console.WriteLine("PASS: unchanged refresh selection, independent custom locks, persistence, rediscovery, optional native frequency and validation.");
    }

    private static async Task VerifySpiceRefreshPriorityAsync(MainWindow window, AppConfigStore store, object state, string xml, DisplayModeFixture service)
    {
        object Request() => Invoke(window, "BuildDisplayConfigurationRequest")!;
        var mainCombo = window.FindControl<ComboBox>("MainRefreshRateComboBox")!;
        var subCombo = window.FindControl<ComboBox>("SubRefreshRateComboBox")!;
        void WriteSpiceValues(string main, string sub)
        {
            var document = XDocument.Load(xml);
            foreach (var (name, value) in new[] { ("graphics-force-refresh", main), ("graphics-force-refresh-sub", sub) })
                document.Descendants("game").Single(game => (string?)game.Attribute("name") == "Sound Voltex")
                    .Element("options")!.Elements("option").Single(option => (string?)option.Attribute("name") == name).SetAttributeValue("value", value);
            document.Save(xml);
        }
        Set(state, "CompatibilityMode", false);
        store.WriteString("Display", "compatibilitymode", "false");
        foreach (var (main, sub) in new[] { ("141", "93"), ("59", "141") })
        {
            store.WriteString("Display", "mainrefresh", "111");
            store.WriteString("Display", "subrefresh", "222");
            WriteSpiceValues(main, sub);
            var original = File.ReadAllText(xml);
            await InvokeAsync(window, "WarmDisplayStateAsync", state);
            Invoke(window, "ApplyDisplayStateToUi");
            Assert(Equals(mainCombo.SelectedItem, main) && Equals(subCombo.SelectedItem, sub), "Spice values were not displayed when absent from system modes.");
            Assert(File.ReadAllText(xml) == original, "Reading Spice refresh values rewrote the XML.");
            Assert((string)Invoke(window, "ValidateDisplayRefreshRates", Request())! == "", "Default mode rejected a Spice-only refresh rate.");
            await InvokeAsync(window, "RequestDisplayListRefreshAsync", "SpicePriorityRegression", true);
            Assert(Equals(mainCombo.SelectedItem, main) && Equals(subCombo.SelectedItem, sub) && File.ReadAllText(xml) == original,
                "Rediscovery replaced the Spice values or rewrote XML.");
            service.AllowApply = true;
            try
            {
                service.Changes.Clear();
                var result = await (Task<DisplaySettingsTransactionResult>)Invoke(window, "ApplyDisplayTransactionAsync", Request(), false, CancellationToken.None)!;
                Assert(result.Succeeded, "Default mode could not launch with Spice-only rates: " + string.Join(";", result.Messages));
                Assert(service.Changes.Where(call => call.Flags == 1).All(call => (call.Fields & 0x00400000) == 0),
                    "Default mode applied a Spice-only refresh to Windows.");
                AssertRefresh(xml, main, sub);
            }
            finally { service.AllowApply = false; }
            Set(state, "CompatibilityMode", true);
            service.Changes.Clear();
            service.RejectedRates.Add(int.Parse(main));
            service.RejectedRates.Add(int.Parse(sub));
            int beforeNativeTests = service.Tests.Count;
            var rejected = await (Task<DisplaySettingsTransactionResult>)Invoke(window, "ApplyDisplayTransactionAsync", Request(), false, CancellationToken.None)!;
            Assert(!rejected.Succeeded && service.Changes.All(call => call.Flags == 2), "Compatibility mode bypassed Windows refresh support.");
            if (main != "59")
                Assert(service.Tests.Skip(beforeNativeTests).Any(call => call.Device == "DISPLAY1" && call.Rate == int.Parse(main)),
                    "Compatibility mode did not ask Windows to validate the unlisted Spice refresh.");
            service.RejectedRates.Remove(int.Parse(main));
            service.RejectedRates.Remove(int.Parse(sub));
            AssertRefresh(xml, main == "59" ? main : "", main == "59" ? sub : "");
            Set(state, "CompatibilityMode", false);
        }

        WriteSpiceValues("155", "96");
        await InvokeAsync(window, "RequestDisplayListRefreshAsync", "ExternalSpiceEdit", true);
        Assert(Equals(mainCombo.SelectedItem, "155") && Equals(subCombo.SelectedItem, "96"), "Manual refresh did not read an external XML edit.");
        string editorFixture = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(xml))!, "refresh-values");
        File.WriteAllLines(editorFixture, new[] { "161", "97" });
        WriteSpiceValues("141", "93");
        try
        {
            Invoke(window, "OnEditConfigClick", null!, new Avalonia.Interactivity.RoutedEventArgs());
            var busy = (ICollection)typeof(MainWindow).GetField("_busyEntries", PrivateInstance)!.GetValue(window)!;
            var editorTimeout = DateTime.UtcNow.AddSeconds(15);
            while (busy.Count > 0 && DateTime.UtcNow < editorTimeout) await Task.Delay(20);
            Assert(busy.Count == 0 && Equals(mainCombo.SelectedItem, "161") && Equals(subCombo.SelectedItem, "97") &&
                (string)Get(state, "MainSpiceRefreshRate") == "161", "spicecfg exit did not reload the latest XML refresh values.");
            AssertRefresh(xml, "161", "97");
        }
        finally { File.Delete(editorFixture); }
        WriteSpiceValues("155", "96");
        await InvokeAsync(window, "RequestDisplayListRefreshAsync", "AfterSpiceEditor", true);
        foreach (string target in new[] { "Main", "Sub" })
            Assert(!window.FindControl<TextBlock>(target + "StartupInfoTextBlock")!.Text!.Contains("Spice2x"), "Startup parameters retained the Spice2x label.");
        mainCombo.SelectedItem = "75";
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (Option(xml, "graphics-force-refresh") != "75" && DateTime.UtcNow < timeout) await Task.Delay(20);
        AssertRefresh(xml, "75", "96");
        Assert(store.ReadString("Display", "mainrefresh") == "" && store.ReadString("Display", "subrefresh") == "", "Default selection retained TOML refresh values.");
        await InvokeAsync(window, "RequestDisplayListRefreshAsync", "UserSpiceSelection", true);
        Assert(Equals(mainCombo.SelectedItem, "75") && Equals(subCombo.SelectedItem, "96"), "Rediscovery reverted the user's saved selection.");

        // Read failures keep the previously loaded choice and leave the locked config intact.
        var beforeFailure = File.ReadAllText(xml);
        using (var held = new FileStream(xml, FileMode.Open, FileAccess.Read, FileShare.None))
            await InvokeAsync(window, "WarmDisplayStateAsync", state);
        Assert((string)Get(state, "SelectedSubRefreshRate") == "96" && File.ReadAllText(xml) == beforeFailure,
            "Unreadable XML discarded the previous external refresh rate or changed the file.");

        store.WriteString("Display", "mainrefresh", "144");
        store.WriteString("Display", "subrefresh", "165");
        WriteSpiceValues("", "");
        await InvokeAsync(window, "WarmDisplayStateAsync", state);
        Invoke(window, "ApplyDisplayStateToUi");
        Assert(Equals(mainCombo.SelectedItem, "不修改") && Equals(subCombo.SelectedItem, "不修改"), "Empty Spice overrides discarded unchanged selections.");
        mainCombo.SelectedItem = "60";
        subCombo.SelectedItem = "60";
        timeout = DateTime.UtcNow.AddSeconds(5);
        while ((Option(xml, "graphics-force-refresh") != "60" || Option(xml, "graphics-force-refresh-sub") != "60") && DateTime.UtcNow < timeout)
            await Task.Delay(20);
        AssertRefresh(xml, "60", "60");
        Console.WriteLine("PASS: Spice refresh priority, unlisted rates, XML read failures, default launch, compatibility validation and startup summary.");
    }

    private static void VerifyRefreshBorderAlignment(MainWindow window, string target)
    {
        var combo = window.FindControl<ComboBox>(target + "ResolutionComboBox")!;
        var input = window.FindControl<TextBox>(target + "CustomRefreshRateTextBox")!;
        VerifyBorderAlignment(window, combo, input);
    }

    private static async Task VerifySettingsBorderAlignmentAsync(MainWindow window)
    {
        double originalWidth = window.Width;
        try
        {
            foreach (double width in new[] { 960d, originalWidth })
            {
                window.Width = width;
                await Task.Delay(150);
                foreach (string name in new[] { "ServerAddressTextBox", "PcbIdTextBox", "WindowSizeTextBox" })
                {
                    var combo = window.FindControl<ComboBox>(name == "WindowSizeTextBox" ? "WindowModeComboBox" : "ServerPresetComboBox")!;
                    VerifyBorderAlignment(window, combo, window.FindControl<TextBox>(name)!);
                }
            }
        }
        finally { window.Width = originalWidth; }
        Console.WriteLine("PASS: server preset and graphics textboxes align with dropdown borders at both window widths.");
    }

    private static void VerifyBorderAlignment(MainWindow window, ComboBox combo, TextBox input)
    {
        var comboBorder = combo.GetVisualDescendants().OfType<GlassCard>().Single(control => control.Name == "border");
        var inputBorder = input.GetVisualDescendants().OfType<GlassCard>().Single(control => control.Name == "PART_GlassBorder");
        Rect Bounds(Visual visual) => new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(window)!.Value);
        var comboRect = Bounds(comboBorder);
        var inputRect = Bounds(inputBorder);
        var inputCell = Bounds((Visual)input.Parent!);
        double rightInset = Bounds(combo).Right - comboRect.Right;
        Assert(Math.Abs(comboRect.Left - inputRect.Left) <= 0.5 && Math.Abs(inputCell.Right - rightInset - inputRect.Right) <= 0.5,
            $"{input.Name} visible borders are misaligned: dropdown left={comboRect.Left:F2}, input left={inputRect.Left:F2}, " +
            $"expected input right={inputCell.Right - rightInset:F2}, actual right={inputRect.Right:F2}.");
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
