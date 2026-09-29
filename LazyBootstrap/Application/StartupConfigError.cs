using System;
using System.IO;

namespace LazyBootstrap.Application;

internal static class StartupConfigError
{
    // Only for exceptions from AppConfigStore.ReadExistingText: its validation
    // errors already include the configuration path and parser diagnostics.
    internal static string Format(string path, Exception error)
    {
        string message = error switch
        {
            FileNotFoundException or DirectoryNotFoundException => $"无法读取配置：{path}",
            InvalidDataException => error.Message,
            _ => $"无法读取配置：{path}\n\n{error.Message}"
        };
        return message + "\n\n请通过外层 Launcher（启动.exe）启动以准备配置。";
    }
}
