using System.IO;
using System.Linq;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.MediaUpdate
{
    internal static class MediaUpdateProtocol
    {
        public const string LauncherProcessImageFileName = "LazyBootstrap.exe";
        public const string SyncBatchFileName = "sync.bat";
        public const string UpdateStagingFolderName = "update_tmp";
        public const string GameLauncherExeName = "启动.exe";
        public const string MediaUpdaterExecutableFileName = "MediaUpdater.exe";

        public static bool IsValidGameRoot(string baseDir)
        {
            return Directory.Exists(Path.Combine(baseDir, "contents"))
                   && Directory.Exists(Path.Combine(baseDir, "asphyxia"));
        }

        public static string FindShallowestFile(string root, string fileName)
        {
            if (!Directory.Exists(root))
            {
                return string.Empty;
            }

            DirectorySafety.EnsureTreeHasNoLinks(root);
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .OrderBy(p => p.Length)
                .FirstOrDefault() ?? string.Empty;
        }
    }
}
