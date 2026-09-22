using System;
using System.IO;
using System.Text;

namespace LazyBootstrap.FileSystem
{
    internal static class SafeFileWriter
    {
        private const int BufferSize = 4096;
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        public static bool TryWriteAllText(string path, string content, Func<string, string> validateFile, out string error)
        {
            return TryWriteAllBytes(path, Utf8NoBom.GetBytes(content ?? string.Empty), validateFile, out error);
        }

        public static bool TryWriteAllBytes(string path, byte[] content, Func<string, string> validateFile, out string error)
            => TryWrite(path, content, validateFile, out error, existingOnly: false);

        public static bool TryReplaceExistingText(string path, string content, Func<string, string> validateFile, out string error)
            => TryReplaceExistingBytes(path, Utf8NoBom.GetBytes(content ?? string.Empty), validateFile, out error);

        public static bool TryReplaceExistingBytes(string path, byte[] content, Func<string, string> validateFile, out string error)
            => TryWrite(path, content, validateFile, out error, existingOnly: true);

        private static bool TryWrite(string path, byte[] content, Func<string, string> validateFile, out string error, bool existingOnly)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = "Target path is empty.";
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            string directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                error = "Target directory is empty.";
                return false;
            }

            string tempPath = Path.Combine(directory, $"{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

            try
            {
                if (!existingOnly) Directory.CreateDirectory(directory);
                WriteBytesToNewFile(tempPath, content ?? Array.Empty<byte>());

                string tempValidationError = ValidateFile(tempPath, validateFile);
                if (!string.IsNullOrWhiteSpace(tempValidationError))
                {
                    error = tempValidationError;
                    return false;
                }

                if (existingOnly)
                {
                    // Never fall back to Move: a disappeared destination must stay absent.
                    using (var probe = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    string existingError = ValidateFile(fullPath, validateFile);
                    if (!string.IsNullOrWhiteSpace(existingError))
                        throw new InvalidDataException(existingError);
                    File.Replace(tempPath, fullPath, null);
                }
                else
                {
                    ReplaceWithTempFile(tempPath, fullPath);
                }
                tempPath = null;

                string targetValidationError = ValidateFile(fullPath, validateFile);
                if (string.IsNullOrWhiteSpace(targetValidationError))
                {
                    return true;
                }

                error = $"Written file failed validation: {targetValidationError}";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                DeleteTempFile(tempPath);
            }
        }

        private static void WriteBytesToNewFile(string path, byte[] content)
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.WriteThrough);

            stream.Write(content, 0, content.Length);
            stream.Flush(true);
        }

        private static void ReplaceWithTempFile(string tempPath, string targetPath)
        {
            if (!File.Exists(targetPath))
            {
                File.Move(tempPath, targetPath);
                return;
            }

            try
            {
                File.Replace(tempPath, targetPath, null, true);
            }
            catch (PlatformNotSupportedException)
            {
                File.Move(tempPath, targetPath, true);
            }
            catch (IOException)
            {
                File.Move(tempPath, targetPath, true);
            }
        }

        private static string ValidateFile(string path, Func<string, string> validateFile)
        {
            return validateFile?.Invoke(path) ?? string.Empty;
        }

        private static void DeleteTempFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
