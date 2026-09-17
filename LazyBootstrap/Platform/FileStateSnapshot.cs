using System;
using System.IO;
using LazyBootstrap.FileSystem;
using LazyBootstrap.Serialization;

namespace LazyBootstrap.Platform
{
    internal sealed class FileStateSnapshot
    {
        private readonly byte[] _content;

        private FileStateSnapshot(string path, bool existed, byte[] content)
        {
            Path = path;
            Existed = existed;
            _content = content ?? Array.Empty<byte>();
        }

        public string Path { get; }

        public bool Existed { get; }

        public static FileStateSnapshot Capture(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            string fullPath = System.IO.Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                return new FileStateSnapshot(fullPath, false, Array.Empty<byte>());
            }

            return new FileStateSnapshot(fullPath, true, File.ReadAllBytes(fullPath));
        }

        public void Restore(bool existingConfigOnly = false)
        {
            if (existingConfigOnly)
            {
                if (!Existed) return;
                if (!SafeFileWriter.TryWriteAllBytes(Path, _content, AppConfigStore.ValidateTomlFile, out var configError, existingOnly: true))
                    throw new IOException(configError);
                return;
            }

            if (Existed)
            {
                string directory = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (!SafeFileWriter.TryWriteAllBytes(Path, _content, null, out var error))
                {
                    throw new IOException(error);
                }

                return;
            }

            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
