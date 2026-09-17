using System;
using Avalonia;
using Serilog;
using LazyBootstrap.Application;
using LazyBootstrap.Platform;
using LazyBootstrap.Serialization;
using System.Runtime.InteropServices;

namespace LazyBootstrap
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            ApplicationComposition composition = null;

            try
            {
                AppServices.InitializeSerilog(args);
                Log.Information("LazyBootstrap process started.");
                if (!MediaUpdaterPendingUpdateService.ApplyPendingUpdate(AppServices.Paths.ApplicationDirectoryPath))
                    ShowUpdateError("资源更新已完成，但更新器替换失败。已保留待替换文件，下次启动将重试。");

                composition = new ApplicationComposition(AppServices.Paths);
                App.Composition = composition;

                try
                {
                    composition.AppConfig.ReadExistingText();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Configuration could not be read.");
                    MessageBoxW(nint.Zero,
                        $"无法读取配置：{AppServices.Paths.ConfigFilePath}\n\n{ex.Message}\n\n请通过外层 Launcher（启动.exe）启动以准备配置。",
                        "配置读取失败", 0x10);
                    Environment.ExitCode = 1;
                    return;
                }
                Log.Information("Configuration loaded without modification.");

                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                Log.Information("Avalonia lifetime ended.");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "LazyBootstrap startup failed.");
                Environment.ExitCode = -1;
            }
            finally
            {
                App.Composition = null;
                composition?.Dispose();
                AppServices.Dispose();
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(nint window, string text, string caption, uint type);

        private static void ShowUpdateError(string message) => MessageBoxW(nint.Zero, message, "更新提示", 0x10);

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .With(new Win32PlatformOptions
                {
                    CompositionMode =
                    [
                        Win32CompositionMode.WinUIComposition,
                        Win32CompositionMode.DirectComposition,
                        Win32CompositionMode.RedirectionSurface
                    ]
                })
                .UsePlatformDetect()
                .LogToTrace();
    }
}
