using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LazyBootstrap.FileSystem;
using LazyBootstrap.Services;
using LazyBootstrap.MediaUpdate;

internal static class Program
{
    private static int Main()
    {
        int failed = 0;
        Run("同目录迁移保留存档", root =>
        {
            string game = Path.Combine(root, "game");
            string save = Path.Combine(game, "asphyxia", "savedata", "player.json");
            Write(save, "original");
            Directory.CreateDirectory(Path.Combine(game, "contents"));
            var service = CreateService(game);
            var entries = service.BuildMigrationEntries(Path.Combine(game, "contents"), Path.Combine(game, "asphyxia"))
                .Where(entry => entry.Id == "savedata").ToList();
            try { service.CopyEntries(entries); }
            catch (IOException) { }
            Assert(File.Exists(save) && File.ReadAllText(save) == "original", "原存档被删除或改变");
        }, ref failed);
        Run("更新同步不执行越界脚本", root =>
        {
            string game = Path.Combine(root, "game");
            string staging = Path.Combine(game, "update_tmp");
            Directory.CreateDirectory(Path.Combine(game, "contents"));
            Directory.CreateDirectory(Path.Combine(game, "asphyxia"));
            Write(Path.Combine(staging, "sync.bat"), "@echo off\r\necho test > \"%LAZY_KFC_UPDATE_GAME_PATH%\\..\\outside.txt\"\r\n");
            Write(Path.Combine(staging, "source", "contents", "resource.txt"), "updated");
            string outside = Path.Combine(root, "outside.txt");
            Write(outside, "original");
            new MediaUpdateSynchronizer(game, staging).Apply(static _ => { });
            Assert(File.ReadAllText(outside) == "original", "游戏目录外的文件被改变");
            Assert(File.ReadAllText(Path.Combine(game, "contents", "resource.txt")) == "updated", "正常资源未同步");
        }, ref failed);

        Run("迁移拒绝规范化后相同的路径", root =>
        {
            string save = Path.Combine(root, "saves");
            Write(Path.Combine(save, "player"), "original");
            ExpectIo(() => CreateService(root).ReplaceDirectory(save, Path.Combine(root, "SAVES", ".") + Path.DirectorySeparatorChar));
            Assert(File.ReadAllText(Path.Combine(save, "player")) == "original", "规范化路径导致存档改变");
        }, ref failed);
        Run("迁移拒绝互相包含的目录", root =>
        {
            string parent = Path.Combine(root, "saves");
            string child = Path.Combine(parent, "child");
            Write(Path.Combine(parent, "parent-save"), "parent");
            Write(Path.Combine(child, "child-save"), "child");
            var service = CreateService(root);
            ExpectIo(() => service.ReplaceDirectory(parent, child));
            ExpectIo(() => service.ReplaceDirectory(child, parent));
            Assert(File.ReadAllText(Path.Combine(parent, "parent-save")) == "parent", "父目录存档改变");
            Assert(File.ReadAllText(Path.Combine(child, "child-save")) == "child", "子目录存档改变");
        }, ref failed);
        Run("正常迁移完整替换并保留源数据", root =>
        {
            string source = Path.Combine(root, "source");
            string target = Path.Combine(root, "target");
            Write(Path.Combine(source, "nested", "player"), "new");
            Write(Path.Combine(target, "old"), "old");
            CreateService(root).ReplaceDirectory(source, target);
            Assert(File.ReadAllText(Path.Combine(target, "nested", "player")) == "new", "新存档未完整复制");
            Assert(!File.Exists(Path.Combine(target, "old")), "旧文件未替换");
            Assert(File.ReadAllText(Path.Combine(source, "nested", "player")) == "new", "源存档被改变");
            Assert(!Directory.EnumerateDirectories(root, ".savedata-*").Any(), "暂存目录未清理");
        }, ref failed);
        Run("读取源存档失败时保留目标存档", root =>
        {
            string source = Path.Combine(root, "source");
            string target = Path.Combine(root, "target");
            string locked = Path.Combine(source, "player");
            Write(locked, "new");
            Write(Path.Combine(target, "player"), "original");
            using var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            ExpectIo(() => CreateService(root).ReplaceDirectory(source, target));
            Assert(File.ReadAllText(Path.Combine(target, "player")) == "original", "复制失败后原存档丢失");
            Assert(!Directory.EnumerateDirectories(root, ".savedata-*").Any(), "失败后暂存目录未清理");
        }, ref failed);
        Run("迁移在写入任何文件前检查全部选项", root =>
        {
            string oldCard = Path.Combine(root, "old", "card0.txt");
            string card = Path.Combine(root, "new", "card0.txt");
            string saves = Path.Combine(root, "new", "savedata");
            Write(oldCard, "new-card");
            Write(card, "original-card");
            Write(Path.Combine(saves, "player"), "original-save");
            ExpectIo(() => CreateService(root).CopyEntries(new[]
            {
                new SavedataTransferEntry("card", "card", oldCard, card, "", false),
                new SavedataTransferEntry("savedata", "savedata", saves, saves, "", true)
            }));
            Assert(File.ReadAllText(card) == "original-card", "无效迁移仍覆盖了之前的选项");
            Assert(File.ReadAllText(Path.Combine(saves, "player")) == "original-save", "无效迁移仍改变存档");
        }, ref failed);
        Run("存档暂存与导入流程正常", root =>
        {
            string game = Path.Combine(root, "game");
            string save = Path.Combine(game, "asphyxia", "savedata", "player");
            string card = Path.Combine(game, "contents", "card0.txt");
            Write(save, "backup-save");
            Write(card, "backup-card");
            var service = CreateService(game);
            string staging = Path.Combine(root, "backup");
            service.StageEntries(service.GetCurrentSavedataEntries(), staging);
            Write(save, "changed");
            Write(card, "changed");
            service.CopyEntries(service.BuildArchiveEntriesFromDirectory(staging));
            Assert(File.ReadAllText(save) == "backup-save", "存档未还原");
            Assert(File.ReadAllText(card) == "backup-card", "卡号未还原");
        }, ref failed);
        Run("存档迁移拒绝目录联接别名", root =>
        {
            string source = Path.Combine(root, "real");
            string alias = Path.Combine(root, "alias");
            Write(Path.Combine(source, "savedata", "player"), "original");
            CreateJunction(alias, source);
            ExpectIo(() => CreateService(root).ReplaceDirectory(Path.Combine(alias, "savedata"), Path.Combine(source, "savedata")));
            Assert(File.ReadAllText(Path.Combine(source, "savedata", "player")) == "original", "目录联接导致存档改变");
        }, ref failed);
        Run("完整更新保留配置及更新器并镜像资源", root =>
        {
            var (game, staging, source) = CreateUpdate(root, nested: true);
            Write(Path.Combine(game, "launcher", "config.toml"), "user-config");
            Write(Path.Combine(game, "launcher", "MediaUpdater.exe"), "running-updater");
            Write(Path.Combine(game, "launcher", "old.dll"), "old");
            Write(Path.Combine(game, "launcher", "old-libs", "old.dll"), "old");
            Write(Path.Combine(game, "contents", "unrelated"), "keep");
            Write(Path.Combine(game, "contents", "data_mods", "omnimix", "old-song"), "old");
            Write(Path.Combine(game, "contents", "data_mods", "omnimix", "old-dir", "old-song"), "old");
            Write(Path.Combine(game, "contents", "data_mods", "_cache", "cache"), "old");
            Write(Path.Combine(source, "launcher", "config.toml"), "package-config");
            Write(Path.Combine(source, "launcher", "MediaUpdater.exe"), "new-updater");
            Write(Path.Combine(source, "launcher", "MediaUpdater.exe.pending"), "untrusted-pending");
            Write(Path.Combine(source, "launcher", "new.dll"), "new");
            Write(Path.Combine(source, "launcher", "Libs", "new.dll"), "new-library");
            Write(Path.Combine(source, "contents", "data_mods", "omnimix", "new-song"), "new-song");
            new MediaUpdateSynchronizer(game, staging).Apply(static _ => { });
            Assert(File.ReadAllText(Path.Combine(game, "launcher", "config.toml")) == "user-config", "用户配置被覆盖");
            Assert(File.ReadAllText(Path.Combine(game, "launcher", "MediaUpdater.exe")) == "running-updater", "正在运行的更新器被替换");
            Assert(File.ReadAllText(Path.Combine(game, "launcher", "MediaUpdater.exe.pending")) == "new-updater", "更新器未正确暂存");
            Assert(!File.Exists(Path.Combine(game, "launcher", "old.dll")) && !Directory.Exists(Path.Combine(game, "launcher", "old-libs")), "旧启动器文件未清理");
            Assert(File.ReadAllText(Path.Combine(game, "launcher", "Libs", "new.dll")) == "new-library", "新依赖未复制");
            Assert(File.ReadAllText(Path.Combine(game, "contents", "unrelated")) == "keep", "无关游戏资源被删除");
            Assert(!File.Exists(Path.Combine(game, "contents", "data_mods", "omnimix", "old-song"))
                && !Directory.Exists(Path.Combine(game, "contents", "data_mods", "omnimix", "old-dir")), "omnimix 旧资源未删除");
            Assert(File.ReadAllText(Path.Combine(game, "contents", "data_mods", "omnimix", "new-song")) == "new-song", "omnimix 新资源未复制");
            Assert(!Directory.Exists(Path.Combine(game, "contents", "data_mods", "_cache")), "缓存未清除");
            Assert(File.Exists(Path.Combine(game, "updater_log.txt")), "更新日志未生成");
            Assert(File.Exists(Path.Combine(source, "launcher", "new.dll")), "更新源被提前清理");
        }, ref failed);
        Run("资源更新不清理启动器", root =>
        {
            var (game, staging, source) = CreateUpdate(root);
            Write(Path.Combine(game, "launcher", "existing.dll"), "keep");
            Write(Path.Combine(game, "contents", "config.toml"), "user");
            Write(Path.Combine(source, "contents", "config.toml"), "package");
            new MediaUpdateSynchronizer(game, staging).Apply(static _ => { });
            Assert(File.ReadAllText(Path.Combine(game, "launcher", "existing.dll")) == "keep", "资源更新清理了启动器");
            Assert(File.ReadAllText(Path.Combine(game, "contents", "config.toml")) == "user", "配置被覆盖");
        }, ref failed);
        Run("更新拒绝游戏根目录或外部暂存目录", root =>
        {
            var (game, _, _) = CreateUpdate(root);
            ExpectIo(() => new MediaUpdateSynchronizer(game, game));
            ExpectIo(() => new MediaUpdateSynchronizer(game, Path.Combine(root, "outside")));
            Assert(Directory.Exists(Path.Combine(game, "contents")), "游戏目录被改变");
        }, ref failed);
        Run("更新拒绝写入自身暂存目录", root =>
        {
            var (game, staging, source) = CreateUpdate(root);
            Write(Path.Combine(source, "update_tmp", "payload"), "bad");
            ExpectIo(() => new MediaUpdateSynchronizer(game, staging));
            Assert(!File.Exists(Path.Combine(game, "contents", "resource.txt")), "拒绝之前已经写入文件");
        }, ref failed);
        Run("更新拒绝源目录联接", root =>
        {
            var (game, staging, source) = CreateUpdate(root);
            string outside = Path.Combine(root, "outside");
            Write(Path.Combine(outside, "sentinel"), "original");
            CreateJunction(Path.Combine(source, "linked"), outside);
            ExpectIo(() => new MediaUpdateSynchronizer(game, staging));
            Assert(File.ReadAllText(Path.Combine(outside, "sentinel")) == "original", "外部源目录被改变");
            Assert(!File.Exists(Path.Combine(game, "contents", "resource.txt")), "拒绝之前已经写入文件");
        }, ref failed);
        Run("更新拒绝目标目录联接且不清理启动器", root =>
        {
            var (game, staging, source) = CreateUpdate(root);
            string outside = Path.Combine(root, "outside");
            Write(Path.Combine(outside, "sentinel"), "original");
            CreateJunction(Path.Combine(game, "linked"), outside);
            Write(Path.Combine(source, "linked", "sentinel"), "changed");
            Write(Path.Combine(source, "launcher", "new.dll"), "new");
            Write(Path.Combine(game, "launcher", "old.dll"), "original-library");
            ExpectIo(() => new MediaUpdateSynchronizer(game, staging));
            Assert(File.ReadAllText(Path.Combine(outside, "sentinel")) == "original", "外部目标目录被改变");
            Assert(File.ReadAllText(Path.Combine(game, "launcher", "old.dll")) == "original-library", "预检失败前已经清理启动器");
        }, ref failed);
        Run("更新拒绝缓存目录联接", root =>
        {
            var (game, staging, _) = CreateUpdate(root);
            string outside = Path.Combine(root, "outside");
            Write(Path.Combine(outside, "sentinel"), "original");
            Directory.CreateDirectory(Path.Combine(game, "contents", "data_mods"));
            CreateJunction(Path.Combine(game, "contents", "data_mods", "_cache"), outside);
            ExpectIo(() => new MediaUpdateSynchronizer(game, staging));
            Assert(File.ReadAllText(Path.Combine(outside, "sentinel")) == "original", "外部缓存目标被改变");
        }, ref failed);
        Run("更新替换硬链接而不写穿外部文件", root =>
        {
            var (game, staging, _) = CreateUpdate(root);
            string outside = Path.Combine(root, "outside-file");
            string outsideLog = Path.Combine(root, "outside-log");
            Write(outside, "original-file");
            Write(outsideLog, "original-log");
            Assert(CreateHardLink(Path.Combine(game, "contents", "resource.txt"), outside, IntPtr.Zero), "无法创建测试硬链接");
            Assert(CreateHardLink(Path.Combine(game, "updater_log.txt"), outsideLog, IntPtr.Zero), "无法创建日志硬链接");
            new MediaUpdateSynchronizer(game, staging).Apply(static _ => { });
            Assert(File.ReadAllText(outside) == "original-file", "外部硬链接文件被改变");
            Assert(File.ReadAllText(outsideLog) == "original-log", "外部硬链接日志被改变");
            Assert(File.ReadAllText(Path.Combine(game, "contents", "resource.txt")) == "new-resource", "硬链接位置未更新");
        }, ref failed);
        Run("空更新包在清理之前被拒绝", root =>
        {
            var (game, staging, source) = CreateUpdate(root);
            string resource = Path.GetFullPath(Path.Combine(source, "contents", "resource.txt"));
            Assert(DirectorySafety.IsWithin(resource, root), "测试删除路径越界");
            File.Delete(resource);
            Directory.CreateDirectory(Path.Combine(source, "launcher"));
            Write(Path.Combine(game, "launcher", "old.dll"), "keep");
            ExpectIo(() => new MediaUpdateSynchronizer(game, staging));
            Assert(File.ReadAllText(Path.Combine(game, "launcher", "old.dll")) == "keep", "空更新包删除了启动器");
        }, ref failed);
        Run("已取消更新不修改文件", root =>
        {
            var (game, staging, _) = CreateUpdate(root);
            var synchronizer = new MediaUpdateSynchronizer(game, staging);
            bool cancelled = false;
            try { synchronizer.Apply(static _ => { }, new CancellationToken(canceled: true)); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && !File.Exists(Path.Combine(game, "contents", "resource.txt")), "取消更新仍写入文件");
        }, ref failed);
        Console.WriteLine($"失败用例数：{failed}");
        return failed == 0 ? 0 : 1;
    }

    private static SavedataTransferService CreateService(string game) =>
        new(new LauncherPaths(game, Path.Combine(game, "launcher"), Path.Combine(game, "launcher", "config.toml")));

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void ExpectIo(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new Exception("预期拒绝操作，但操作未被拒绝");
    }

    private static (string Game, string Staging, string Source) CreateUpdate(string root, bool nested = false)
    {
        string game = Path.Combine(root, "game");
        string staging = Path.Combine(game, "update_tmp");
        string package = nested ? Path.Combine(staging, "UPDATE_LAZY_KFC") : staging;
        string source = Path.Combine(package, "source");
        Directory.CreateDirectory(Path.Combine(game, "contents"));
        Directory.CreateDirectory(Path.Combine(game, "asphyxia"));
        Write(Path.Combine(package, "sync.bat"), "@echo off\r\nexit /b 0\r\n");
        Write(Path.Combine(source, "contents", "resource.txt"), "new-resource");
        return (game, staging, source);
    }

    private static void CreateJunction(string link, string target)
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:REGRESSION_LINK -Target $env:REGRESSION_TARGET -ErrorAction Stop | Out-Null");
        start.Environment["REGRESSION_LINK"] = link;
        start.Environment["REGRESSION_TARGET"] = target;
        using var process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert(process.ExitCode == 0, "无法创建测试目录联接：" + error);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    private static void Run(string name, Action<string> test, ref int failed)
    {
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        string root = Path.GetFullPath(Path.Combine(temp, "LazyBootstrap-regression-" + Guid.NewGuid().ToString("N")));
        if (!root.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new Exception("测试路径越界");
        Directory.CreateDirectory(root);
        try
        {
            test(root);
            Console.WriteLine("通过: " + name);
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine("失败: " + name + " — " + ex.Message);
        }
        finally
        {
            RemoveFixture(root, root);
        }
    }

    private static void RemoveFixture(string root, string directory)
    {
        Assert(DirectorySafety.IsWithin(directory, root), "测试清理路径越界");
        // Unlink junctions before deleting their targets; never recurse through them.
        foreach (string entry in Directory.GetFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry);
            else File.Delete(entry);
        }
        foreach (string entry in Directory.GetFileSystemEntries(directory))
        {
            if (Directory.Exists(entry)) RemoveFixture(root, entry);
            else File.Delete(entry);
        }
        Directory.Delete(directory);
    }
}
