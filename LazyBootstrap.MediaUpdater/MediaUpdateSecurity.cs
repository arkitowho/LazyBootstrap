using System;
using System.IO;
using System.Linq;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.MediaUpdate;

internal static class MediaUpdateSecurity
{
    public const string StateFolder = MediaUpdateProtocol.UpdateStateFolderName;
    public const string UpdaterPath = "launcher/MediaUpdater.exe";
    public const string PendingPath = UpdaterPath + ".pending";

    public static string ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) throw new IOException("必须使用非空相对路径。");
        string normalized = path.Replace('\\', '/');
        foreach (string part in normalized.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')
                || part.Any(c => c < 32 || "<>:\"|?*".Contains(c))) throw new IOException("更新路径含有非法片段：" + path);
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$"
                || ((stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem.Length == 4 && "123456789¹²³".Contains(stem[3])))
                throw new IOException("更新路径不能使用 Windows 设备名：" + path);
        }
        return normalized;
    }

    public static string ResolveDestination(string relativePath, string gamePath, bool internalPending = false)
    {
        string relative = ValidateRelativePath(relativePath);
        string first = relative.Split('/')[0];
        if (first.Equals(StateFolder, StringComparison.OrdinalIgnoreCase)
            || first.Equals(MediaUpdateProtocol.UpdateStagingFolderName, StringComparison.OrdinalIgnoreCase)
            || first.Equals(MediaUpdateProtocol.UpdateLogFileName, StringComparison.OrdinalIgnoreCase)
            || relative.Split('/').Any(p => p.StartsWith(".media-update-", StringComparison.OrdinalIgnoreCase))
            || (!internalPending && (Same(relative, PendingPath) || relative.StartsWith(PendingPath + "/", StringComparison.OrdinalIgnoreCase))))
            throw new IOException("更新目标属于更新程序内部保留路径：" + relative);
        string destination = DirectorySafety.Normalize(Path.Combine(gamePath, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!DirectorySafety.IsWithin(destination, gamePath) || Same(destination, DirectorySafety.Normalize(gamePath)))
            throw new IOException("更新目标超出游戏目录。");
        return destination;
    }

    public static bool Same(string left, string right) => string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
