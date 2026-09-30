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
using Avalonia.Threading;
using LazyBootstrap;
using LazyBootstrap.UI;
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
                await RunAsync();
                Console.WriteLine("PASS: spicecfg exit updates subdisplay and landscape toggles in both directions on the settings page.");
                exitCode = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { cancellation.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(cancellation.Token);
        return exitCode;
    }

    private static async Task RunAsync()
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
            window.Hide();
        }
        finally { Directory.Delete(root, true); }
    }

    private static object? Invoke(MainWindow window, string name, params object[] args) =>
        typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(window, args);
    private static async Task InvokeAsync(MainWindow window, string name, params object[] args) =>
        await (Task)Invoke(window, name, args)!;
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
