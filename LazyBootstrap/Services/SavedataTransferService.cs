using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.Services
{

    internal sealed class SavedataTransferService
    {
        private readonly LauncherPaths _paths;

        public SavedataTransferService(LauncherPaths paths)
        {
            ArgumentNullException.ThrowIfNull(paths);
            _paths = paths;
        }

        public List<SavedataTransferEntry> GetCurrentSavedataEntries()
        {
            var entries = GetCurrentSavedataTargets();
            return entries
                .Where(entry => entry.IsDirectory ? Directory.Exists(entry.SourcePath) : File.Exists(entry.SourcePath))
                .ToList();
        }

        public List<SavedataTransferEntry> GetCurrentSavedataTargets()
        {
            string contentsDirectory = _paths.GetContentsDirectoryPath();
            string asphyxiaDirectory = _paths.GetAsphyxiaDirectoryPath();

            return new List<SavedataTransferEntry>
            {
                new(
                    "card0",
                    "card0.txt",
                    Path.Combine(contentsDirectory, "card0.txt"),
                    Path.Combine(contentsDirectory, "card0.txt"),
                    Path.Combine("contents", "card0.txt"),
                    isDirectory: false),
                new(
                    "card1",
                    "card1.txt",
                    Path.Combine(contentsDirectory, "card1.txt"),
                    Path.Combine(contentsDirectory, "card1.txt"),
                    Path.Combine("contents", "card1.txt"),
                    isDirectory: false),
                new(
                    "savedata",
                    "savedata",
                    Path.Combine(asphyxiaDirectory, "savedata"),
                    Path.Combine(asphyxiaDirectory, "savedata"),
                    Path.Combine("asphyxia", "savedata"),
                    isDirectory: true),
                new(
                    "config",
                    "config.ini",
                    Path.Combine(asphyxiaDirectory, "config.ini"),
                    Path.Combine(asphyxiaDirectory, "config.ini"),
                    Path.Combine("asphyxia", "config.ini"),
                    isDirectory: false)
            };
        }

        public List<SavedataTransferEntry> BuildArchiveEntriesFromDirectory(string extractionDirectory)
        {
            var targets = GetCurrentSavedataTargets();
            var extractedEntries = new List<SavedataTransferEntry>();

            foreach (var target in targets)
            {
                string extractedPath = Path.Combine(extractionDirectory, target.ArchiveRelativePath);
                if (target.IsDirectory)
                {
                    if (!Directory.Exists(extractedPath))
                    {
                        continue;
                    }
                }
                else if (!File.Exists(extractedPath))
                {
                    continue;
                }

                extractedEntries.Add(new SavedataTransferEntry(
                    target.Id,
                    target.DisplayName,
                    extractedPath,
                    target.DestinationPath,
                    target.ArchiveRelativePath,
                    target.IsDirectory));
            }

            return extractedEntries;
        }

        public List<SavedataTransferEntry> BuildMigrationEntries(string sourceGameDirectory, string sourceAsphyxiaDirectory)
        {
            string sourceContentsDirectory = ResolveMigrationGameDirectory(sourceGameDirectory);
            string targetContentsDirectory = _paths.GetContentsDirectoryPath();
            string targetAsphyxiaDirectory = _paths.GetAsphyxiaDirectoryPath();

            var entries = new List<SavedataTransferEntry>();
            AddFileEntryIfExists(entries, "card0", "card0.txt", Path.Combine(sourceContentsDirectory, "card0.txt"), Path.Combine(targetContentsDirectory, "card0.txt"));
            AddFileEntryIfExists(entries, "card1", "card1.txt", Path.Combine(sourceContentsDirectory, "card1.txt"), Path.Combine(targetContentsDirectory, "card1.txt"));
            AddFileEntryIfExists(entries, "config", "config.ini", Path.Combine(sourceAsphyxiaDirectory, "config.ini"), Path.Combine(targetAsphyxiaDirectory, "config.ini"));

            string sourceSavedataDirectory = Path.Combine(sourceAsphyxiaDirectory, "savedata");
            if (Directory.Exists(sourceSavedataDirectory))
            {
                entries.Add(new SavedataTransferEntry(
                    "savedata",
                    "savedata",
                    sourceSavedataDirectory,
                    Path.Combine(targetAsphyxiaDirectory, "savedata"),
                    Path.Combine("asphyxia", "savedata"),
                    isDirectory: true));
            }

            return entries;
        }

        public static bool HasExistingTargets(IEnumerable<SavedataTransferEntry> entries)
        {
            return entries.Any(entry => entry.IsDirectory ? Directory.Exists(entry.DestinationPath) : File.Exists(entry.DestinationPath));
        }

        public void ReplaceDirectory(string sourceDirectory, string destinationDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
            sourceDirectory = DirectorySafety.Normalize(sourceDirectory);
            destinationDirectory = DirectorySafety.Normalize(destinationDirectory);
            ValidateTransferPaths(sourceDirectory, destinationDirectory, isDirectory: true);

            string parent = Path.GetDirectoryName(destinationDirectory);
            Directory.CreateDirectory(parent);
            string staging = Path.Combine(parent, ".savedata-staging-" + Guid.NewGuid().ToString("N"));
            string backup = Path.Combine(parent, ".savedata-backup-" + Guid.NewGuid().ToString("N"));
            bool movedOriginal = false;
            try
            {
                // Finish all potentially failing reads before moving the original save out of the way.
                CopyDirectoryRecursive(sourceDirectory, staging);
                DirectorySafety.EnsureNoLinks(destinationDirectory);
                if (Directory.Exists(destinationDirectory))
                {
                    Directory.Move(destinationDirectory, backup);
                    movedOriginal = true;
                }

                try
                {
                    Directory.Move(staging, destinationDirectory);
                }
                catch (Exception replacementError)
                {
                    if (movedOriginal)
                    {
                        try { Directory.Move(backup, destinationDirectory); }
                        catch (Exception restoreError)
                        {
                            throw new IOException($"存档替换未完成，原存档已保留在：{backup}",
                                new AggregateException(replacementError, restoreError));
                        }
                    }
                    throw;
                }

                // A failed backup cleanup must not turn a successful replacement into data loss.
                DeleteDirectoryIfExists(backup);
            }
            finally
            {
                DeleteDirectoryIfExists(staging);
            }
        }

        public void CopyFile(string sourcePath, string destinationPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
            ValidateTransferPaths(sourcePath, destinationPath, isDirectory: false);

            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        public void CopyDirectoryRecursive(string sourceDirectory, string destinationDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
            ValidateTransferPaths(sourceDirectory, destinationDirectory, isDirectory: true);
            CopyDirectoryContents(sourceDirectory, destinationDirectory);
        }

        private static void CopyDirectoryContents(string sourceDirectory, string destinationDirectory)
        {
            DirectorySafety.EnsureNoLinks(sourceDirectory);
            DirectorySafety.EnsureNoLinks(destinationDirectory);
            Directory.CreateDirectory(destinationDirectory);

            foreach (var file in Directory.GetFiles(sourceDirectory))
            {
                string destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
                DirectorySafety.EnsureNoLinks(file);
                DirectorySafety.EnsureNoLinks(destinationFile);
                File.Copy(file, destinationFile, overwrite: true);
            }

            foreach (var directory in Directory.GetDirectories(sourceDirectory))
            {
                string destinationSubDirectory = Path.Combine(destinationDirectory, Path.GetFileName(directory));
                CopyDirectoryContents(directory, destinationSubDirectory);
            }
        }

        public void StageEntries(IEnumerable<SavedataTransferEntry> entries, string stagingDirectory)
        {
            foreach (var entry in entries)
            {
                string stagedPath = Path.Combine(stagingDirectory, entry.ArchiveRelativePath);
                if (entry.IsDirectory)
                {
                    CopyDirectoryRecursive(entry.SourcePath, stagedPath);
                    continue;
                }

                CopyFile(entry.SourcePath, stagedPath);
            }
        }

        public void CopyEntries(IEnumerable<SavedataTransferEntry> entries)
        {
            ArgumentNullException.ThrowIfNull(entries);
            var transfers = entries.ToList();
            // Reject the entire selection before copying even the first file.
            foreach (var entry in transfers)
                ValidateTransferPaths(entry.SourcePath, entry.DestinationPath, entry.IsDirectory);

            foreach (var entry in transfers)
            {
                if (entry.IsDirectory)
                {
                    ReplaceDirectory(entry.SourcePath, entry.DestinationPath);
                    continue;
                }

                CopyFile(entry.SourcePath, entry.DestinationPath);
            }
        }

        public string CreateTemporaryWorkingDirectory(string purpose)
        {
            string path = Path.Combine(Path.GetTempPath(), "LazyBootstrap", purpose, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        public void DeleteDirectoryIfExists(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                DirectorySafety.EnsureTreeHasNoLinks(path);
                Directory.Delete(path, recursive: true);
            }
            catch
            {
            }
        }

        private static string ResolveMigrationGameDirectory(string sourceGameDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourceGameDirectory))
            {
                return string.Empty;
            }

            string directCard0Path = Path.Combine(sourceGameDirectory, "card0.txt");
            string directCard1Path = Path.Combine(sourceGameDirectory, "card1.txt");
            if (File.Exists(directCard0Path) || File.Exists(directCard1Path))
            {
                return sourceGameDirectory;
            }

            string nestedContentsDirectory = Path.Combine(sourceGameDirectory, "contents");
            string nestedCard0Path = Path.Combine(nestedContentsDirectory, "card0.txt");
            string nestedCard1Path = Path.Combine(nestedContentsDirectory, "card1.txt");
            if (File.Exists(nestedCard0Path) || File.Exists(nestedCard1Path))
            {
                return nestedContentsDirectory;
            }

            return sourceGameDirectory;
        }

        private static void AddFileEntryIfExists(List<SavedataTransferEntry> entries, string id, string displayName, string sourcePath, string destinationPath)
        {
            if (!File.Exists(sourcePath))
            {
                return;
            }

            entries.Add(new SavedataTransferEntry(id, displayName, sourcePath, destinationPath, string.Empty, isDirectory: false));
        }

        private static void ValidateTransferPaths(string source, string destination, bool isDirectory)
        {
            DirectorySafety.EnsureSeparateTrees(source, destination);
            if (isDirectory)
            {
                if (!Directory.Exists(source))
                    throw new DirectoryNotFoundException($"未找到源目录：{source}");
                DirectorySafety.EnsureTreeHasNoLinks(source);
                DirectorySafety.EnsureTreeHasNoLinks(destination);
            }
            else
            {
                if (!File.Exists(source)) throw new FileNotFoundException("未找到源文件。", source);
                DirectorySafety.EnsureNoLinks(source);
                DirectorySafety.EnsureNoLinks(destination);
            }
        }
    }

    internal sealed class SavedataTransferEntry
    {
        public SavedataTransferEntry(string id, string displayName, string sourcePath, string destinationPath, string archiveRelativePath, bool isDirectory)
        {
            Id = id ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            SourcePath = sourcePath ?? string.Empty;
            DestinationPath = destinationPath ?? string.Empty;
            ArchiveRelativePath = archiveRelativePath ?? string.Empty;
            IsDirectory = isDirectory;
        }

        public string Id { get; }

        public string DisplayName { get; }

        public string SourcePath { get; }

        public string DestinationPath { get; }

        public string ArchiveRelativePath { get; }

        public bool IsDirectory { get; }
    }
}
