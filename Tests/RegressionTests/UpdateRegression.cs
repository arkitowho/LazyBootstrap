using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using LazyBootstrap.FileSystem;
using LazyBootstrap.MediaUpdate;
using LazyBootstrap.Platform;

internal static partial class UpdateRegression
{
    private static int _failed;

    public static int RunAll()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("更新测试仅支持 Windows。");
        Test("覆盖合并、镜像、删除及配置不再受保护", root =>
        {
            var (game, package) = Pack(root, Copy("source/contents", "contents"), Mirror("source/launcher", "launcher"), Delete("contents/cache"));
            Put(package, "source/contents/config.toml", "added");
            Put(package, "source/launcher/config.toml", "replaced");
            Put(package, "source/launcher/MediaUpdater.exe", "new-updater");
            Put(game, "contents/keep", "keep"); Put(game, "contents/cache/CONFIG.TOML", "deleted");
            Put(game, "launcher/config.toml", "old"); Put(game, "launcher/old/deep/config.toml", "deleted");
            Put(game, "launcher/MediaUpdater.exe", "running");
            Seal(package); Apply(game, package);
            Equal(game, "contents/config.toml", "added"); Equal(game, "launcher/config.toml", "replaced"); Equal(game, "contents/keep", "keep");
            Check(!Directory.Exists(Path.Combine(game, "contents/cache")) && !Directory.Exists(Path.Combine(game, "launcher/old")), "配置仍受到删除保护");
            Equal(game, "launcher/MediaUpdater.exe", "running"); Equal(game, "launcher/MediaUpdater.exe.pending", "new-updater");
            CheckNoTransaction(game);
        });
        Test("空镜像、不存在的删除与重复安装", root =>
        {
            var (game, package) = Pack(root, Mirror("source/empty", "contents/old"), Delete("contents/missing"));
            Directory.CreateDirectory(Path.Combine(package, "source/empty")); Put(game, "contents/old/obsolete", "old"); Seal(package);
            Apply(game, package); Apply(game, package);
            Check(Directory.Exists(Path.Combine(game, "contents/old")) && !Directory.EnumerateFileSystemEntries(Path.Combine(game, "contents/old")).Any(), "空镜像错误");
        });
        Test("操作顺序、双向类型转换与连续 XML 编辑", root =>
        {
            var (game, package) = Pack(root, Delete("contents/item"), Copy("source/item", "contents/item"),
                Edit("contents/item/config.xml", Value("/config/@value", "first")),
                Edit("contents/item/config.xml", Value("/config/@value", "second")), Mirror("source/tree", "contents/tree"));
            Put(game, "contents/item", "file"); Put(package, "source/item/config.xml", "<config value='old'/>");
            Put(game, "contents/tree/a/old", "old"); Put(game, "contents/tree/b", "old");
            Put(package, "source/tree/a", "file"); Put(package, "source/tree/b/new", "child");
            Seal(package); Apply(game, package);
            Check(File.ReadAllText(Path.Combine(game, "contents/item/config.xml")).Contains("second"), "XML 未使用前序结果");
            Equal(game, "contents/tree/a", "file"); Equal(game, "contents/tree/b/new", "child");
        });
        Test("预演成功不创建目录、不截断文件、不删除目标", root =>
        {
            var (game, package) = Pack(root, Copy("source/a", "contents/a"), Delete("contents/old"), Copy("source/a", "contents/new/deep/a"));
            Put(package, "source/a", "new"); Put(game, "contents/a", "original-long"); Put(game, "contents/old", "old"); Seal(package);
            using var engine = Prepare(game, package);
            Equal(game, "contents/a", "original-long"); Equal(game, "contents/old", "old");
            Check(!Directory.Exists(Path.Combine(game, "contents/new")), "预演创建了正式目录"); CheckNoTransaction(game);
        });
        foreach (string problem in new[] { "locked-target", "readonly", "hidden", "locked-source", "locked-delete", "type-conflict" })
        Test("预演后项失败阻止全部修改：" + problem, root =>
        {
            var (game, package) = Pack(root, Copy("source/a", "contents/a"),
                problem == "locked-delete" ? Delete("contents/tree") : Copy("source/b", "contents/b"));
            Put(package, "source/a", "new-a"); Put(package, "source/b", "new-b");
            Put(game, "contents/a", "old-a"); Put(game, "contents/b", "old-b"); Put(game, "contents/tree/deep/file", "old"); Seal(package);
            string target = Path.Combine(game, "contents/b"); FileStream held = null;
            try
            {
                if (problem == "locked-target") held = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (problem == "readonly") File.SetAttributes(target, FileAttributes.ReadOnly);
                if (problem == "hidden") File.SetAttributes(target, FileAttributes.Hidden);
                if (problem == "locked-source") held = File.Open(Path.Combine(package, "source/b"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                if (problem == "locked-delete") held = File.Open(Path.Combine(game, "contents/tree/deep/file"), FileMode.Open, FileAccess.Read, FileShare.Read);
                if (problem == "type-conflict") { File.Delete(target); Directory.CreateDirectory(target); }
                // Bypass launcher hashing to exercise the updater's own source-access probe.
                Reject(() => { using var engine = MediaUpdateEngine.Prepare(game, package); });
                Equal(game, "contents/a", "old-a"); Equal(game, "contents/tree/deep/file", "old"); CheckNoTransaction(game);
            }
            finally { held?.Dispose(); if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal); }
        });
        foreach (string problem in new[] { "overwrite", "create-file", "create-directory", "new-parents", "delete-directory" })
        Test("拒绝访问权限导致整包不安装：" + problem, root =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            string target = problem == "create-directory" ? "contents/restricted/new" : problem == "new-parents" ? "contents/restricted/new/deep/file" : "contents/restricted/file";
            var op = problem == "delete-directory" ? Delete("contents/restricted")
                : Copy(problem == "create-directory" ? "source/empty" : "source/b", target);
            var (game, package) = Pack(root, Copy("source/a", "contents/a"), op);
            Put(package, "source/a", "new"); Put(package, "source/b", "new"); Directory.CreateDirectory(Path.Combine(package, "source/empty"));
            Put(game, "contents/a", "old"); Directory.CreateDirectory(Path.Combine(game, "contents/restricted"));
            if (problem == "overwrite") Put(game, target, "old-b");
            Seal(package);
            string denied = Path.Combine(game, problem == "overwrite" ? target : "contents/restricted");
            var rights = problem switch
            {
                "overwrite" => FileSystemRights.WriteData,
                "create-file" => FileSystemRights.CreateFiles,
                "create-directory" or "new-parents" => FileSystemRights.CreateDirectories,
                _ => FileSystemRights.Delete
            };
            using var permission = new DeniedAccess(denied, rights);
            using var parentPermission = problem == "delete-directory" ? new DeniedAccess(Path.Combine(game, "contents"), FileSystemRights.DeleteSubdirectoriesAndFiles) : null;
            Reject(() => { using var engine = MediaUpdateEngine.Prepare(game, package); });
            Equal(game, "contents/a", "old");
            Check(!Directory.Exists(Path.Combine(game, "contents/restricted/new")), "权限预演创建了目标");
            if (problem == "overwrite") Equal(game, target, "old-b");
        });
        Test("安装中途失败保留已写入内容并停止后续项", root =>
        {
            var (game, package) = Pack(root, Copy("source/a", "contents/a"), Copy("source/b", "contents/b"), Copy("source/c", "contents/c"));
            foreach (string name in new[] { "a", "b", "c" }) { Put(package, "source/" + name, "new"); Put(game, "contents/" + name, "old"); }
            Seal(package); using var engine = Prepare(game, package);
            string blocked = Path.Combine(game, "contents/b"); File.SetAttributes(blocked, FileAttributes.ReadOnly);
            try
            {
                try { engine.Apply(); throw new Exception("未触发安装失败"); }
                catch (IOException ex) { Check(ex.Message.Contains("部分文件可能已更新") && ex.Message.Contains("contents/b"), "失败信息缺少文件与部分安装状态"); }
                Equal(game, "contents/a", "new"); Equal(game, "contents/b", "old"); Equal(game, "contents/c", "old"); CheckNoTransaction(game);
            }
            finally { File.SetAttributes(blocked, FileAttributes.Normal); }
        });
        Test("安装取消在文件边界停止且不回滚", root =>
        {
            var (game, package) = Pack(root, Copy("source/a", "contents/a"), Copy("source/b", "contents/b"));
            Put(package, "source/a", "new"); Put(package, "source/b", "new"); Put(game, "contents/a", "old"); Put(game, "contents/b", "old"); Seal(package);
            using var cancel = new CancellationTokenSource();
            using var engine = Prepare(game, package, message => { if (message.StartsWith("正在安装 1/")) cancel.Cancel(); });
            Reject(() => engine.Apply(cancel.Token)); Equal(game, "contents/a", "new"); Equal(game, "contents/b", "old");
        });
        Test("预演取消不安装", root =>
        {
            var (game, package) = Pack(root, Delete("contents/a")); Put(game, "contents/a", "old"); Seal(package);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { using var engine = MediaUpdateEngine.Prepare(game, package, cancel: cancel.Token); throw new Exception("取消未生效"); }
            catch (OperationCanceledException) { }
            Equal(game, "contents/a", "old");
        });
        Test("更新器 pending 最后写入且下次启动无备份替换", root =>
        {
            var (game, package) = Pack(root, Copy("source/updater", "launcher/MediaUpdater.exe"), Copy("source/a", "contents/a"));
            Put(package, "source/updater", "new-updater"); Put(package, "source/a", "new"); Put(game, "launcher/MediaUpdater.exe", "running");
            Put(game, "contents/a", "old"); Seal(package);
            var messages = new List<string>();
            using (var engine = Prepare(game, package, messages.Add)) engine.Apply();
            Check(messages.Last(m => m.StartsWith("正在安装 ")).EndsWith("MediaUpdater.exe.pending"), "pending 未最后安装");
            Equal(game, "launcher/MediaUpdater.exe", "running");
            Put(game, ".media-update/active/backup/old", "legacy");
            Check(MediaUpdaterPendingUpdateService.ApplyPendingUpdate(Path.Combine(game, "launcher")), "pending 替换失败");
            Equal(game, "launcher/MediaUpdater.exe", "new-updater"); Equal(game, ".media-update/active/backup/old", "legacy");
            Check(!File.Exists(Path.Combine(game, "launcher/MediaUpdater.exe.bak")), "创建了备份");
            Check(MediaUpdaterPendingUpdateService.ApplyPendingUpdate(Path.Combine(game, "launcher")), "没有 pending 时失败");
        });
        Test("镜像和父目录删除保留运行中的更新器", root =>
        {
            var (game, package) = Pack(root, Delete("launcher")); Put(game, "launcher/MediaUpdater.exe", "running"); Put(game, "launcher/config.toml", "delete"); Seal(package);
            Apply(game, package); Equal(game, "launcher/MediaUpdater.exe", "running"); Check(!File.Exists(Path.Combine(game, "launcher/config.toml")), "配置未删除");
        });
        Test("pending 替换占用重试与失败保留", root =>
        {
            string app = Path.Combine(root, "launcher"); Put(app, "MediaUpdater.exe", "old"); Put(app, "MediaUpdater.exe.pending", "new");
            using (var held = File.Open(Path.Combine(app, "MediaUpdater.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
                Check(!MediaUpdaterPendingUpdateService.ApplyPendingUpdate(app), "占用时仍替换");
            Equal(app, "MediaUpdater.exe.pending", "new"); Equal(app, "MediaUpdater.exe", "old");
            var transient = File.Open(Path.Combine(app, "MediaUpdater.exe"), FileMode.Open, FileAccess.Read, FileShare.Read);
            var release = System.Threading.Tasks.Task.Run(() => { Thread.Sleep(400); transient.Dispose(); });
            try { Check(MediaUpdaterPendingUpdateService.ApplyPendingUpdate(app), "占用解除后未重试"); }
            finally { release.GetAwaiter().GetResult(); transient.Dispose(); }
            Equal(app, "MediaUpdater.exe", "new");
        });
        foreach (string path in new[] { "../outside", "C:/outside", ".media-update/file", "launcher/MediaUpdater.exe.pending", "contents/CON", "contents/a:stream" })
        Test("基础目标路径约束：" + path, root =>
        {
            var (game, package) = Pack(root, Copy("source/a", path)); Put(package, "source/a", "new"); Seal(package);
            Reject(() => Apply(game, package)); CheckNoTransaction(game);
        });
        RunChecksumTests(); RunXmlTests(); RunProcessTests(); RunConfigTests(); RunLogTests();
        return _failed;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class DeniedAccess : IDisposable
    {
        private readonly FileSystemInfo _entry;
        private readonly FileSystemSecurity _original;
        public DeniedAccess(string path, FileSystemRights rights)
        {
            _entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            _original = Get();
            var changed = Get();
            using var identity = WindowsIdentity.GetCurrent();
            changed.AddAccessRule(new FileSystemAccessRule(identity.User!, rights, AccessControlType.Deny));
            Set(changed);
        }
        private FileSystemSecurity Get() => _entry is DirectoryInfo dir ? dir.GetAccessControl() : ((FileInfo)_entry).GetAccessControl();
        private void Set(FileSystemSecurity security)
        {
            if (_entry is DirectoryInfo dir) dir.SetAccessControl((DirectorySecurity)security);
            else ((FileInfo)_entry).SetAccessControl((FileSecurity)security);
        }
        public void Dispose()
        {
            var restored = Get();
            restored.SetSecurityDescriptorBinaryForm(_original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            Set(restored);
        }
    }

    private static void Test(string name, Action<string> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "LazyBootstrap-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root); Console.WriteLine("通过：" + name); }
        catch (Exception ex) { _failed++; Console.WriteLine("失败：" + name + " — " + ex); }
        finally
        {
            if (!DirectorySafety.IsWithin(root, Path.Combine(Path.GetTempPath(), "LazyBootstrap-update-tests"))) throw new IOException("测试清理路径越界");
            try { Directory.Delete(root, true); } catch { }
        }
    }
    private static void CheckNoTransaction(string game)
    {
        foreach (string path in new[] { "active", "verified-package.json", "lock" })
            Check(!Path.Exists(Path.Combine(game, ".media-update", path)), "残留已移除状态：" + path);
    }
    private static (string Game, string Staging) Pack(string root, params MediaUpdateOperation[] ops)
    {
        string game = Path.Combine(root, "game"), staging = Path.Combine(game, ".media-update", "tmp");
        Directory.CreateDirectory(Path.Combine(game, "contents")); Directory.CreateDirectory(Path.Combine(game, "asphyxia"));
        Manifest(staging, ops); return (game, staging);
    }
    private static void Manifest(string staging, params MediaUpdateOperation[] ops) => Put(staging, "update",
        JsonSerializer.Serialize(new MediaUpdateManifest { Operations = ops.ToList() }, MediaUpdateJsonContext.Default.MediaUpdateManifest));
    private static MediaUpdateOperation Copy(string source, string target) => new() { Type = "copy", Source = source, Target = target };
    private static MediaUpdateOperation Mirror(string source, string target) => new() { Type = "mirror", Source = source, Target = target };
    private static MediaUpdateOperation Delete(string target) => new() { Type = "delete", Target = target };
    private static MediaUpdateOperation Edit(string target, params MediaXmlEdit[] edits) => new() { Type = "editXml", Target = target, Edits = edits.ToList() };
    private static void Apply(string game, string staging) { using var engine = Prepare(game, staging); engine.Apply(); }
    private static string Verify(string game, string staging, Action<string> report = null, CancellationToken cancel = default)
        => MediaUpdateChecksums.Verify(game, staging, report, cancel);
    private static MediaUpdateEngine Prepare(string game, string staging, Action<string> report = null, CancellationToken cancel = default)
        => MediaUpdateEngine.Prepare(game, Verify(game, staging, report, cancel), report, cancel);
    private static void Put(string root, string path, string text) { string full = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text); }
    private static void Equal(string root, string path, string expected) => Check(File.ReadAllText(Path.Combine(root, path)) == expected, "内容不符：" + path);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("无效操作未被拒绝"); }
}
