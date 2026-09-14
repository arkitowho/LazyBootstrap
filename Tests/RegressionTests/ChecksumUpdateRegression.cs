using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using LazyBootstrap.MediaUpdate;

internal static partial class UpdateRegression
{
    // Explicit fixture finalization only; neither validation nor installation regenerates checksums.
    private static void Seal(string package)
    {
        var manifest = new MediaUpdateChecksumManifest
        {
            SchemaVersion = 1, Algorithm = "SHA256", Files = Directory.GetFiles(package, "*", SearchOption.AllDirectories)
                .Where(p => !MediaUpdateSecurity.Same(Path.GetRelativePath(package, p), MediaUpdateChecksums.FileName))
                .OrderBy(p => p, StringComparer.Ordinal).Select(p => new MediaUpdateChecksumFile
                {
                    Path = Path.GetRelativePath(package, p).Replace('\\', '/'),
                    Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant()
                }).ToList()
        };
        Put(package, MediaUpdateChecksums.FileName, JsonSerializer.Serialize(manifest, MediaUpdateJsonContext.Default.MediaUpdateChecksumManifest));
    }

    private static void RejectChecksum(Action action, string detail)
    {
        try { action(); }
        catch (IOException ex)
        {
            Check(ex.Message.Contains("疑似被修改或损坏") && ex.Message.Contains(detail), "校验错误缺少原因或路径：" + ex.Message);
            return;
        }
        throw new Exception("损坏更新包未被拒绝");
    }

    private static void RunChecksumTests()
    {
        Test("缺失校验清单拒绝且不修改游戏", root =>
        {
            var (game, staging) = Pack(root, Delete("contents/old")); Put(game, "contents/old", "old");
            RejectChecksum(() => Apply(game, staging), "checksums.json"); Equal(game, "contents/old", "old");
        });
        Test("SHA256 已知摘要、中文路径、空文件和大文件", root =>
        {
            var (game, staging) = Pack(root, Copy("source", "contents/files"));
            Put(staging, "source/中文/日本.txt", "abc"); Put(staging, "source/empty", "");
            File.WriteAllBytes(Path.Combine(staging, "source/large"), new byte[3 * 1024 * 1024 + 17]);
            Seal(staging);
            string checksums = File.ReadAllText(Path.Combine(staging, MediaUpdateChecksums.FileName));
            Check(checksums.Contains("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")
                && checksums.Contains("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"), "已知 SHA256 摘要错误");
            var messages = new List<string>();
            using var engine = Prepare(game, staging, messages.Add);
            engine.Apply();
            Equal(game, "contents/files/中文/日本.txt", "abc"); Equal(game, "contents/files/empty", "");
            Check(new FileInfo(Path.Combine(game, "contents/files/large")).Length == 3 * 1024 * 1024 + 17, "大文件长度错误");
            Check(messages.Any(m => m.Contains("正在校验更新包 4/4")), "缺少文件校验进度");
        });
        foreach (string tampered in new[] { "update.json", "source/file", "说明.txt" })
        Test("拒绝文件摘要不符：" + tampered, root =>
        {
            var (game, staging) = Pack(root, Copy("source/file", "contents/file"));
            Put(staging, "source/file", "new"); Put(staging, "说明.txt", "readme"); Put(game, "contents/file", "old");
            Seal(staging); File.AppendAllText(Path.Combine(staging, tampered), " ");
            RejectChecksum(() => Apply(game, staging), tampered); Equal(game, "contents/file", "old");
            Check(!Directory.Exists(Path.Combine(game, ".media-update/active")), "校验失败创建了事务");
        });
        Test("缺失载荷和额外文件拒绝，包括未被操作使用的文件", root =>
        {
            var (game, staging) = Pack(root, Delete("contents/old")); Put(game, "contents/old", "old");
            Put(staging, "说明.txt", "readme"); Seal(staging); File.Delete(Path.Combine(staging, "说明.txt"));
            RejectChecksum(() => Apply(game, staging), "说明.txt");
            Put(staging, "说明.txt", "readme"); Put(staging, "extra", "extra");
            RejectChecksum(() => Apply(game, staging), "extra"); Equal(game, "contents/old", "old");
        });
        Test("校验清单字段、算法、摘要、重复路径与危险路径严格校验", root =>
        {
            var (game, staging) = Pack(root, Delete("contents/old")); Put(game, "contents/old", "old");
            Seal(staging); string path = Path.Combine(staging, MediaUpdateChecksums.FileName);
            string good = File.ReadAllText(path);
            var model = JsonSerializer.Deserialize(good, MediaUpdateJsonContext.Default.MediaUpdateChecksumManifest)!;
            foreach (string invalid in new[]
            {
                "{}", "null", good.Replace("SHA256", "MD5"), good.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"),
                good.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1"),
                good.Replace("\"algorithm\":", "\"unknown\": 1, \"algorithm\":"),
                good.Replace("\"SHA256\"", "null"), good.Replace("\"files\": [", "\"files\": [null,"),
                good.Replace(model.Files[0].Sha256, "ABC"), good.Replace(model.Files[0].Sha256, new string('A', 64)),
                good.Replace("\"path\":", "\"extra\": true, \"path\":"), good.Replace("\"path\": \"update.json\",", ""),
                good.Replace("update.json", "../outside"), good.Replace("update.json", "C:/outside"),
                good.Replace("update.json", "source/a:stream"), good.Replace("update.json", "source/CON.txt"),
                good.Replace("update.json", "checksums.json"), good.Replace("update.json", "source\\\\file")
            })
            {
                File.WriteAllText(path, invalid); RejectChecksum(() => Apply(game, staging), "疑似");
            }
            model.Files.Add(new MediaUpdateChecksumFile { Path = "UPDATE.JSON", Sha256 = model.Files[0].Sha256 });
            File.WriteAllText(path, JsonSerializer.Serialize(model, MediaUpdateJsonContext.Default.MediaUpdateChecksumManifest));
            RejectChecksum(() => Apply(game, staging), "大小写冲突"); Equal(game, "contents/old", "old");
        });
        Test("包外文件拒绝且内层同名校验清单正常参与校验", root =>
        {
            var (game, staging) = Pack(root, Delete("contents/old"));
            string wrapper = Path.Combine(staging, "wrapper"); Directory.CreateDirectory(wrapper);
            File.Move(Path.Combine(staging, "update.json"), Path.Combine(wrapper, "update.json"));
            Put(wrapper, "source/checksums.json", "payload"); Seal(wrapper);
            Put(staging, "extra.txt", "unchecked"); RejectChecksum(() => Apply(game, staging), "extra.txt");
            File.Delete(Path.Combine(staging, "extra.txt"));
            Apply(game, staging);
            File.AppendAllText(Path.Combine(wrapper, "source/checksums.json"), "changed");
            RejectChecksum(() => Apply(game, staging), "source/checksums.json");
        });
        Test("校验可取消且不创建事务", root =>
        {
            var (game, staging) = Pack(root, Delete("contents/old")); Put(game, "contents/old", "old"); Seal(staging);
            using var cancel = new CancellationTokenSource();
            bool cancelled = false;
            try
            {
                using var engine = Prepare(game, staging, message =>
                { if (message.Contains("正在校验更新包 1/")) cancel.Cancel(); }, cancel.Token);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !Directory.Exists(Path.Combine(game, ".media-update/active")), "校验取消未生效"); Equal(game, "contents/old", "old");
        });
    }
}
