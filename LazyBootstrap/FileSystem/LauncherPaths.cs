using System;
using SystemEnvironment = System.Environment;
using System.IO;
namespace LazyBootstrap.FileSystem
{

    internal sealed class LauncherPaths
    {
        private readonly string _defaultContentsDirectoryPath;
        private readonly string _defaultAsphyxiaDirectoryPath;

        public LauncherPaths(string baseDir, string applicationDirectoryPath, string configFilePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(baseDir);
            ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectoryPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(configFilePath);

            BaseDir = Path.GetFullPath(baseDir);
            ApplicationDirectoryPath = Path.GetFullPath(applicationDirectoryPath);
            ConfigFilePath = Path.GetFullPath(configFilePath);
            _defaultContentsDirectoryPath = Path.Combine(BaseDir, "contents");
            _defaultAsphyxiaDirectoryPath = Path.Combine(BaseDir, "asphyxia");
        }

        public static LauncherPaths Create(string[] args)
        {
            string applicationDirectoryPath = PathHelper.NormalizePath(AppDomain.CurrentDomain.BaseDirectory);
            string baseDirectoryPath = LauncherLocation.ResolveBaseDirectory(
                args,
                SystemEnvironment.GetEnvironmentVariable(LauncherLocation.BaseDirEnvironmentVariable),
                applicationDirectoryPath);
            string configFilePath = PathHelper.NormalizePath(Path.Combine(LauncherLocation.GetConfigurationDirectory(applicationDirectoryPath), "config.toml"));
            return new LauncherPaths(baseDirectoryPath, applicationDirectoryPath, configFilePath);
        }

        public string BaseDir { get; }

        public string ApplicationDirectoryPath { get; }

        public string ConfigFilePath { get; }

        public string GetContentsDirectoryPath()
        {
            return _defaultContentsDirectoryPath;
        }

        public string GetAsphyxiaDirectoryPath()
        {
            return _defaultAsphyxiaDirectoryPath;
        }

        public string GetSpicePath()
        {
            return Path.Combine(GetContentsDirectoryPath(), "spice64.exe");
        }

        public string GetAsphyxiaPath()
        {
            return Path.Combine(GetAsphyxiaDirectoryPath(), "asphyxia-core-x64.exe");
        }

        public string GetSpiceXmlPath()
        {
            string appDataDir = SystemEnvironment.GetFolderPath(SystemEnvironment.SpecialFolder.ApplicationData);
            return Path.Combine(appDataDir, "spicetools.xml");
        }

        public string GetLazySpiceXmlPath()
        {
            return Path.Combine(GetContentsDirectoryPath(), "lazy", "spicetools.xml");
        }

        public string ResolveSpiceXmlPath(bool useSystemSpiceConfig)
        {
            return useSystemSpiceConfig ? GetSpiceXmlPath() : GetLazySpiceXmlPath();
        }

        public string GetBundledLibsDirectoryPath()
        {
            return Path.Combine(ApplicationDirectoryPath, "Libs");
        }

        public string GetBundledSevenZipExecutablePath()
        {
            return Path.Combine(ApplicationDirectoryPath, "7za.exe");
        }

        public string GetLauncherExecutablePath()
        {
            return Path.Combine(BaseDir, "launcher", "LazyBootstrap.exe");
        }

        public string GetOuterLauncherExecutablePath()
        {
            return LauncherLocation.FindOuterLauncher(Path.GetDirectoryName(ConfigFilePath));
        }

        public string GetRuntimeDirectoryPath()
        {
            return Path.Combine(BaseDir, "runtime");
        }

        public string GetSavedataBackupDirectoryPath()
        {
            return Path.Combine(BaseDir, "savedata_backup");
        }

        public string GetUpdateStagingDirectoryPath()
        {
            return MediaUpdate.MediaUpdateProtocol.GetUpdateStagingDirectoryPath(BaseDir);
        }

        public string ResolveSevenZipExecutablePath()
        {
            string bundledPath = GetBundledSevenZipExecutablePath();
            if (File.Exists(bundledPath))
            {
                return bundledPath;
            }

            string baseDirPath = Path.Combine(BaseDir, "7za.exe");
            if (File.Exists(baseDirPath))
            {
                return baseDirPath;
            }

            return Path.Combine(BaseDir, "launcher", "7za.exe");
        }

    }
}
