using System;
using System.IO;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.MediaUpdate
{
    internal static class MediaUpdateSecurity
    {
        public const string BlockedNonGamePathMessage =
            "更新路径不安全，已停止更新。请检查更新目录及其中的符号链接或目录联接。";

        public static void ValidateStagingDirectory(string stagingPath, string gamePath)
        {
            string expectedStaging = Path.Combine(DirectorySafety.Normalize(gamePath), MediaUpdateProtocol.UpdateStagingFolderName);
            if (!string.Equals(DirectorySafety.Normalize(stagingPath), expectedStaging, StringComparison.OrdinalIgnoreCase))
                throw new IOException("更新临时目录必须是游戏根目录下的 update_tmp，不能使用游戏目录本身或其他目录。");
            DirectorySafety.EnsureTreeHasNoLinks(stagingPath);
        }

        public static string ResolveDestination(string relativePath, string gamePath)
        {
            string destination = DirectorySafety.Normalize(Path.Combine(gamePath, relativePath));
            if (!DirectorySafety.IsWithin(destination, gamePath)
                || string.Equals(destination, DirectorySafety.Normalize(gamePath), StringComparison.OrdinalIgnoreCase)
                || DirectorySafety.IsWithin(destination, Path.Combine(gamePath, MediaUpdateProtocol.UpdateStagingFolderName)))
                throw new IOException("更新文件的目标路径超出了允许范围。");
            DirectorySafety.EnsureNoLinks(destination);
            return destination;
        }
    }
}
