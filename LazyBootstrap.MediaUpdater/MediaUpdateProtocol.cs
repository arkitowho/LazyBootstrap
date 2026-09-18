using System.IO;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.MediaUpdate
{
    internal static class MediaUpdateProtocol
    {
        public const string ManifestFileName = "update";
        public const string UpdateStateFolderName = ".media-update";
        public const string UpdateStagingFolderName = "tmp";
        public const string UpdateLogFileName = "update_log.txt";
        public const string MediaUpdaterExecutableFileName = "MediaUpdater.exe";

        public static string GetUpdateStagingDirectoryPath(string game) =>
            Path.Combine(DirectorySafety.Normalize(game), UpdateStateFolderName, UpdateStagingFolderName);

    }
}
