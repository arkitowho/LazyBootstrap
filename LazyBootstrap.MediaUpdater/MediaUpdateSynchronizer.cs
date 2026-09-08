using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.MediaUpdate
{
    /// <summary>
    /// Implements the fixed Packaging/sync.bat file layout without executing package-supplied commands.
    /// sync.bat is retained only as a legacy package-layout marker.
    /// </summary>
    internal sealed class MediaUpdateSynchronizer
    {
        private readonly string _gamePath;
        private readonly string _stagingPath;
        private readonly string _sourcePath;
        private readonly bool _replaceLauncher;
        private readonly List<string> _directories;
        private readonly List<string> _files;

        public MediaUpdateSynchronizer(string gamePath, string stagingPath)
        {
            _gamePath = DirectorySafety.Normalize(gamePath);
            _stagingPath = DirectorySafety.Normalize(stagingPath);
            MediaUpdateSecurity.ValidateStagingDirectory(_stagingPath, _gamePath);
            if (!MediaUpdateProtocol.IsValidGameRoot(_gamePath))
                throw new IOException("游戏目录中未找到 contents 或 asphyxia。");

            string marker = MediaUpdateProtocol.FindShallowestFile(_stagingPath, MediaUpdateProtocol.SyncBatchFileName);
            if (string.IsNullOrEmpty(marker)) throw new IOException("更新包中未找到 sync.bat 目录标记。");
            _sourcePath = Path.Combine(Path.GetDirectoryName(marker)!, "source");
            if (!Directory.Exists(_sourcePath)) throw new IOException("更新包中未找到与 sync.bat 同级的 source 目录。");
            DirectorySafety.EnsureTreeHasNoLinks(_sourcePath);

            _directories = Directory.EnumerateDirectories(_sourcePath, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_sourcePath, path)).OrderBy(path => path.Length).ToList();
            _files = Directory.EnumerateFiles(_sourcePath, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_sourcePath, path)).ToList();
            if (_files.Count == 0) throw new IOException("更新包的 source 目录为空，已停止更新。");
            _replaceLauncher = Directory.Exists(Path.Combine(_sourcePath, "launcher"));
            ValidateTargets();
        }

        // Validate the complete operation before deleting old launcher files or writing anything.
        private void ValidateTargets()
        {
            foreach (string relative in _directories)
            {
                string target = Destination(relative);
                if (File.Exists(target)) throw new IOException($"更新目录与现有文件冲突：{target}");
            }
            foreach (string relative in _files)
            {
                string target = Destination(relative);
                if (Directory.Exists(target)) throw new IOException($"更新文件与现有目录冲突：{target}");
            }
            if (_replaceLauncher) DirectorySafety.EnsureTreeHasNoLinks(Destination("launcher"));
            if (Directory.Exists(Path.Combine(_sourcePath, "contents", "data_mods", "omnimix")))
                DirectorySafety.EnsureTreeHasNoLinks(Destination(Path.Combine("contents", "data_mods", "omnimix")));
            DirectorySafety.EnsureTreeHasNoLinks(Destination(Path.Combine("contents", "data_mods", "_cache")));
            Destination("updater_log.txt");
            Destination(Path.Combine("launcher", "MediaUpdater.exe.pending"));
        }

        public void Apply(Action<string> log, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(log);
            cancellationToken.ThrowIfCancellationRequested();
            MediaUpdateSecurity.ValidateStagingDirectory(_stagingPath, _gamePath);
            ValidateTargets();

            string logTemp = Destination(".updater-log-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var writer = new StreamWriter(new FileStream(logTemp, FileMode.CreateNew, FileAccess.Write, FileShare.Read)))
                {
                    void Report(string message)
                    {
                        writer.WriteLine(message);
                        log(message);
                    }
                    try { ApplyFiles(Report, cancellationToken); }
                    catch (Exception ex)
                    {
                        writer.WriteLine("更新失败：" + ex.Message);
                        throw;
                    }
                }
            }
            finally
            {
                // Replace the directory entry rather than writing through an existing hard link.
                if (File.Exists(logTemp)) File.Move(logTemp, Destination("updater_log.txt"), overwrite: true);
            }
        }

        private void ApplyFiles(Action<string> log, CancellationToken cancellationToken)
        {
            if (_replaceLauncher)
            {
                string launcher = Destination("launcher");
                if (Directory.Exists(launcher))
                {
                    foreach (string file in Directory.GetFiles(launcher))
                    {
                        string name = Path.GetFileName(file);
                        if (name.Equals("MediaUpdater.exe", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("config.toml", StringComparison.OrdinalIgnoreCase)) continue;
                        DeleteFile(Path.GetRelativePath(_gamePath, file));
                    }
                    foreach (string directory in Directory.GetDirectories(launcher))
                        DeleteDirectory(Path.GetRelativePath(_gamePath, directory));
                }
            }

            foreach (string relative in _directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Destination(relative));
            }
            foreach (string relative in _files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = Path.GetFileName(relative);
                if (name.Equals("config.toml", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("MediaUpdater.exe", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("MediaUpdater.exe.pending", StringComparison.OrdinalIgnoreCase)) continue;
                CopyFile(relative, relative);
                log("已同步：" + relative);
            }

            string updater = Path.Combine("launcher", "MediaUpdater.exe");
            if (_replaceLauncher && File.Exists(Path.Combine(_sourcePath, updater)))
            {
                CopyFile(updater, updater + ".pending");
                log("更新程序已暂存，下次启动时替换。");
            }

            string omnimix = Path.Combine("contents", "data_mods", "omnimix");
            if (Directory.Exists(Path.Combine(_sourcePath, omnimix)))
            {
                MirrorRemovedEntries(omnimix, cancellationToken);
                log("omnimix 镜像同步完成。");
            }
            DeleteDirectory(Path.Combine("contents", "data_mods", "_cache"));
            log("资源同步完成。");
        }

        private void MirrorRemovedEntries(string relative, CancellationToken cancellationToken)
        {
            string target = Destination(relative);
            foreach (string file in Directory.GetFiles(target))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string child = Path.GetRelativePath(_gamePath, file);
                if (!File.Exists(Path.Combine(_sourcePath, child))
                    && !Path.GetFileName(file).Equals("config.toml", StringComparison.OrdinalIgnoreCase)) DeleteFile(child);
            }
            foreach (string directory in Directory.GetDirectories(target))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string child = Path.GetRelativePath(_gamePath, directory);
                if (!Directory.Exists(Path.Combine(_sourcePath, child))) DeleteDirectory(child);
                else MirrorRemovedEntries(child, cancellationToken);
            }
        }

        private void CopyFile(string sourceRelative, string destinationRelative)
        {
            string source = Path.Combine(_sourcePath, sourceRelative);
            DirectorySafety.EnsureNoLinks(source);
            string destination = Destination(destinationRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string temporary = Destination(destinationRelative + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(source, temporary, overwrite: false);
                File.Move(temporary, Destination(destinationRelative), overwrite: true);
            }
            finally
            {
                DirectorySafety.EnsureNoLinks(temporary);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private string Destination(string relative) => MediaUpdateSecurity.ResolveDestination(relative, _gamePath);

        private void DeleteFile(string relative)
        {
            string target = Destination(relative);
            File.Delete(target);
        }

        private void DeleteDirectory(string relative)
        {
            string target = Destination(relative);
            DirectorySafety.EnsureTreeHasNoLinks(target);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}
