using System;
using System.IO;

namespace LazyBootstrap.FileSystem;

internal static class LauncherLocation
{
    internal const string BaseDirEnvironmentVariable = "LAZYBOOTSTRAP_BASEDIR";
    private const string BaseDirArgumentName = "--basedir";

    internal static string GetConfigurationDirectory(string applicationDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(applicationDirectory));
        return string.Equals(directory.Name, "launcher", StringComparison.OrdinalIgnoreCase) && directory.Parent != null
            ? directory.Parent.FullName : directory.FullName;
    }

    internal static string FindOuterLauncher(string directory)
    {
        foreach (string name in new[] { "启动.exe", "Launcher.exe" })
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"未找到外层启动器：{directory}（启动.exe 或 Launcher.exe）。");
    }

    internal static string ResolveBaseDirectory(
        string[] args,
        string environmentBaseDirectory,
        string applicationDirectoryPath)
    {
        string argumentBaseDirectory = TryGetBaseDirectoryFromArguments(args);
        if (!string.IsNullOrWhiteSpace(argumentBaseDirectory))
        {
            return Path.GetFullPath(argumentBaseDirectory);
        }

        if (!string.IsNullOrWhiteSpace(environmentBaseDirectory))
        {
            return Path.GetFullPath(environmentBaseDirectory);
        }

        string normalizedApplicationDirectory = Path.GetFullPath(applicationDirectoryPath);
        string trimmedApplicationDirectory = normalizedApplicationDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var applicationDirectoryInfo = new DirectoryInfo(trimmedApplicationDirectory);
        if (!string.Equals(applicationDirectoryInfo.Name, "launcher", StringComparison.OrdinalIgnoreCase)
            || applicationDirectoryInfo.Parent == null
            || !Directory.Exists(Path.Combine(trimmedApplicationDirectory, "Libs")))
        {
            return normalizedApplicationDirectory;
        }

        return Path.GetFullPath(applicationDirectoryInfo.Parent.FullName);
    }

    private static string TryGetBaseDirectoryFromArguments(string[] args)
    {
        if (args == null)
        {
            return string.Empty;
        }

        for (var index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            if (argument.StartsWith($"{BaseDirArgumentName}=", StringComparison.OrdinalIgnoreCase))
            {
                return argument.Substring(BaseDirArgumentName.Length + 1).Trim('"');
            }

            if (string.Equals(argument, BaseDirArgumentName, StringComparison.OrdinalIgnoreCase)
                && index + 1 < args.Length)
            {
                return (args[index + 1] ?? string.Empty).Trim('"');
            }
        }

        return string.Empty;
    }

}
