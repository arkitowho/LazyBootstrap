using System;
using System.Collections.Generic;
using System.IO;

namespace LazyBootstrap.FileSystem
{
    internal static class DirectorySafety
    {
        public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        public static bool IsWithin(string path, string root)
        {
            path = Normalize(path);
            root = Normalize(root);
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }

        public static void EnsureSeparateTrees(string source, string destination)
        {
            if (IsWithin(source, destination) || IsWithin(destination, source))
                throw new IOException("源目录与目标目录不能相同或互相包含，请重新选择目录。");
        }

        // Check every existing ancestor too: a normal-looking child can be behind a junction.
        public static void EnsureNoLinks(string path)
        {
            for (string current = Normalize(path); current != null; current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException($"为保护目录内外的数据，不能操作包含符号链接或目录联接的路径：{current}");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        public static void EnsureTreeHasNoLinks(string root)
        {
            EnsureNoLinks(root);
            if (!Directory.Exists(root)) return;

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                EnsureNoLinks(directory);
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException($"为保护目录内外的数据，不能操作符号链接或目录联接：{entry}");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
        }
    }
}
