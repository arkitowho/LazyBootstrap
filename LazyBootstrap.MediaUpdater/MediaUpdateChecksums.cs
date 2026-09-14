using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.MediaUpdate;

internal sealed class MediaUpdateChecksumManifest
{
    public required int SchemaVersion { get; set; }
    public required string Algorithm { get; set; }
    public required List<MediaUpdateChecksumFile> Files { get; set; }
}

internal sealed class MediaUpdateChecksumFile
{
    public required string Path { get; set; }
    public required string Sha256 { get; set; }
}

internal static class MediaUpdateChecksums
{
    public const string FileName = "checksums.json";

    // Returns only the package location. There is no persistent verification handoff.
    public static string Verify(string game, string staging, Action<string> report = null,
        CancellationToken cancel = default, Action<string> log = null)
    {
        using var session = new MediaUpdateLog(game, false);
        void Write(string message) { session.Write(message); log?.Invoke(message); }
        try
        {
            cancel.ThrowIfCancellationRequested();
            report?.Invoke("正在校验更新包...");
            Write("Package SHA256 verification started.");
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Collect(staging);
            var manifests = files.Where(p => Path.GetFileName(p).Equals("update.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (manifests.Length != 1) throw new IOException("更新包必须包含且仅包含一份 update.json；不支持旧版 sync.bat 更新包。");
            string manifestPath = manifests[0];
            string package = Path.GetDirectoryName(manifestPath)!;
            foreach (string path in files)
                if (!DirectorySafety.IsWithin(path, package)) throw new IOException("文件位于包根目录之外：" + Path.GetRelativePath(staging, path));
            string checksumPath = files.SingleOrDefault(p => MediaUpdateSecurity.Same(Path.GetRelativePath(package, p), FileName));
            if (checksumPath == null) throw new IOException("缺少校验清单：" + FileName);
            byte[] checksumBytes = File.ReadAllBytes(checksumPath);
            using var json = JsonDocument.Parse(checksumBytes);
            MediaUpdateManifest.ValidateJson(json.RootElement);
            var checksums = JsonSerializer.Deserialize(checksumBytes, MediaUpdateJsonContext.Default.MediaUpdateChecksumManifest);
            if (checksums == null || checksums.SchemaVersion != 1 || checksums.Algorithm != "SHA256"
                || checksums.Files == null || checksums.Files.Count == 0)
                throw new IOException("checksums.json 的版本、算法或文件列表无效。");
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in checksums.Files)
            {
                if (entry == null) throw new IOException("校验清单包含空文件项。");
                string relative = MediaUpdateSecurity.ValidateRelativePath(entry.Path);
                if (relative != entry.Path) throw new IOException("校验路径必须使用 /：" + entry.Path);
                if (MediaUpdateSecurity.Same(relative, FileName)) throw new IOException("校验清单不能包含自身。");
                if (entry.Sha256 == null || entry.Sha256.Length != 64 || entry.Sha256.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
                    throw new IOException("SHA256 摘要格式无效：" + relative);
                if (!expected.TryAdd(relative, entry.Sha256)) throw new IOException("校验路径重复或大小写冲突：" + relative);
            }
            var payloads = files.Where(p => !MediaUpdateSecurity.Same(p, checksumPath)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var actual = payloads.Select(p => Path.GetRelativePath(package, p).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string relative in expected.Keys)
                if (!actual.Contains(relative)) throw new IOException("清单中的文件缺失：" + relative);
            foreach (string relative in actual)
                if (!expected.ContainsKey(relative)) throw new IOException("存在未列入清单的文件：" + relative);

            byte[] manifestBytes = null;
            int completed = 0;
            foreach (string path in payloads)
            {
                cancel.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(package, path).Replace('\\', '/');
                string digest;
                if (MediaUpdateSecurity.Same(path, manifestPath))
                {
                    manifestBytes = File.ReadAllBytes(path);
                    digest = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
                }
                else
                {
                    using var input = File.OpenRead(path);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    byte[] buffer = new byte[128 * 1024];
                    int read;
                    while ((read = input.Read(buffer)) > 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        hash.AppendData(buffer.AsSpan(0, read));
                    }
                    digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }
                Write("Verifying file: " + relative);
                if (digest != expected[relative]) throw new IOException("SHA256 不符：" + relative);
                report?.Invoke($"正在校验更新包 {++completed}/{payloads.Length}：{relative}");
            }
            cancel.ThrowIfCancellationRequested();
            var manifest = MediaUpdateManifest.Parse(manifestBytes);
            foreach (var op in manifest.Operations) MediaUpdateSecurity.ResolveDestination(op.Target, game);
            Write("Package SHA256 verification completed.");
            return package;

            void Collect(string directory)
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancel.ThrowIfCancellationRequested();
                    string relative = MediaUpdateSecurity.ValidateRelativePath(Path.GetRelativePath(staging, path));
                    if (!seen.Add(relative)) throw new IOException("包内路径大小写冲突：" + relative);
                    if (Directory.Exists(path)) Collect(path);
                    else files.Add(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Write("Package verification failed: " + ex.Message);
            throw new IOException("更新包疑似被修改或损坏，已停止更新。" + ex.Message, ex);
        }
    }
}
