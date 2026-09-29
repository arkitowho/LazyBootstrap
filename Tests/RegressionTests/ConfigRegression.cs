using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using LazyBootstrap.Application;
using LazyBootstrap.FileSystem;
using LazyBootstrap.Launcher;
using LazyBootstrap.Platform;
using LazyBootstrap.Serialization;

internal static partial class UpdateRegression
{
    private static void RunConfigTests()
    {
        foreach (bool missingDirectory in new[] { false, true })
        Test("配置读取弹窗不重复缺失原因：" + (missingDirectory ? "目录" : "文件"), root =>
        {
            string path = Path.Combine(root, missingDirectory ? "missing/config.toml" : "config.toml");
            var store = new AppConfigStore(path, null);
            try { store.ReadExistingText(); throw new Exception("预期读取缺失配置失败"); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                Check(missingDirectory ? ex is DirectoryNotFoundException : ex is FileNotFoundException, "缺失场景未覆盖预期异常");
                string original = ex.ToString();
                string message = StartupConfigError.Format(path, ex);
                Check(message == $"无法读取配置：{path}\n\n请通过外层 Launcher（启动.exe）启动以准备配置。", "仍有重复缺失说明或丢失恢复建议");
                Check(ex.ToString() == original, "格式化改变了供日志使用的原始异常");
            }
            Check(!File.Exists(path), "弹窗处理创建了配置");
        });
        Test("配置格式错误弹窗仅显示一次路径并保留解析详情", root =>
        {
            const string broken = "[Setting\ninvalid";
            string path = Path.Combine(root, "config.toml");
            File.WriteAllText(path, broken);
            var store = new AppConfigStore(path, null);
            try { store.ReadExistingText(); throw new Exception("预期配置格式错误"); }
            catch (InvalidDataException ex)
            {
                string message = StartupConfigError.Format(path, ex);
                Check(message.StartsWith(ex.Message + "\n\n", StringComparison.Ordinal), "配置错误原文被修改");
                Check(message.Split(path).Length == 2 && !message.Contains("无法读取配置"), "重复添加了配置路径或外层说明");
                Check(message.Contains(AppConfigDocument.Validate(broken)), "丢失了解析器原始详情");
                Check(message.EndsWith("请通过外层 Launcher（启动.exe）启动以准备配置。", StringComparison.Ordinal), "丢失恢复建议");
            }
            Equal(root, "config.toml", broken);
        });
        foreach (bool denyRead in new[] { false, true })
        Test("配置读取弹窗保留系统原因：" + (denyRead ? "权限不足" : "文件占用"), root =>
        {
            string path = LauncherConfigPreparation.Prepare(root, root);
            using var held = denyRead ? null : File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            using var denied = denyRead ? new DeniedAccess(path, FileSystemRights.ReadData) : null;
            try { new AppConfigStore(path, null).ReadExistingText(); throw new Exception("预期系统访问错误"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                string message = StartupConfigError.Format(path, ex);
                Check(message.Contains("\n\n" + ex.Message + "\n\n"), "系统访问错误未按原文保留");
                Check(message.StartsWith($"无法读取配置：{path}", StringComparison.Ordinal), "丢失失败操作或配置路径");
            }
        });
        Test("配置读取弹窗保留未知英文异常且不按文字猜测类型", root =>
        {
            const string reason = "Could not find file 'dependency.dll'. Additional diagnostic: E_TEST_42";
            var error = new InvalidOperationException(reason);
            string message = StartupConfigError.Format(Path.Combine(root, "config.toml"), error);
            Check(message.Contains("\n\n" + reason + "\n\n"), "未知英文错误被翻译或按文字删除");
        });
        Test("新建显示配置的分辨率和刷新率保持空，重复启动不填入固定值", root =>
        {
            string path = LauncherConfigPreparation.Prepare(root, root);
            var store = new AppConfigStore(path, null);
            foreach (string key in new[] { "mainresolution", "subresolution", "mainrefresh", "subrefresh" })
                Check(store.ReadString("Display", key, null) == string.Empty, $"新建配置预填了 {key}");
            Check(!store.ReadBool("Display", "displayconfigure", true), "新建配置自动启用了显示设置");
            string original = File.ReadAllText(path);
            LauncherConfigPreparation.Prepare(root, root);
            Equal(root, "config.toml", original);
        });
        Test("补齐显示配置使用空值且保留已保存的显示模式", root =>
        {
            Put(root, "config.toml", "[Display]\nmainresolution = \"2560x1440\"\nmainrefresh = \"144\"\nsubresolution = \"\"\n");
            string path = LauncherConfigPreparation.Prepare(root, root);
            var store = new AppConfigStore(path, null);
            Check(store.ReadString("Display", "mainresolution") == "2560x1440"
                && store.ReadString("Display", "mainrefresh") == "144", "补齐配置覆盖了已保存模式");
            Check(store.ReadString("Display", "subresolution", null) == string.Empty, "补齐配置覆盖了空分辨率");
            Check(store.ReadString("Display", "subrefresh", null) == string.Empty, "缺失的刷新率未补为空值");
        });
        Test("共享文档仅修改内存且配置读取不缓存外部变更", root =>
        {
            string path = Path.Combine(root, "config.toml");
            const string original = "# keep\n[Setting]\nnoasphyxia = \"true\" # user\ncustom = \"unknown\"\n";
            File.WriteAllText(path, original);
            var document = AppConfigDocument.Parse(original);
            document.CompleteDefaults();
            Check(File.ReadAllText(path) == original, "内存文档修改了磁盘");
            string prepared = document.ToText();
            Check(prepared.Contains("# keep") && prepared.Contains("# user") && prepared.Contains("custom = \"unknown\""), "补齐丢失用户字段或注释");
            Check(AppConfigDocument.Parse(prepared).ReadString("Setting", "noasphyxia") == "true", "补齐覆盖用户值");
            foreach (string newline in new[] { "\n", "\r\n" })
            {
                var multiline = AppConfigDocument.Parse($"[Setting]{newline}note = \"\"\"first{newline}second\"\"\"{newline}");
                string originalNote = multiline.ReadString("Setting", "note");
                multiline.UpsertString("Setting", "auto-launch", "true");
                Check(multiline.ReadString("Setting", "note") == originalNote, "编辑后改变了多行 TOML 值");
                Check(AppConfigDocument.Parse(multiline.ToText()).ReadString("Setting", "note") == originalNote, "保存后改变了多行 TOML 值");
            }
            var store = new AppConfigStore(path, null);
            Check(store.ReadBool("Setting", "noasphyxia", false), "首次读取错误");
            File.WriteAllText(path, original.Replace("\"true\"", "\"false\"").Replace("unknown", "external"));
            Check(!store.ReadBool("Setting", "noasphyxia", true), "跨操作缓存了旧配置");
            store.WriteString("Setting", "auto-launch", "true");
            Check(store.ReadString("Setting", "custom") == "external", "保存覆盖外部变更");
        });

        Test("配置快照恢复完整字节且不恢复损坏目标", root =>
        {
            string path = LauncherConfigPreparation.Prepare(root, root);
            byte[] original = File.ReadAllBytes(path);
            var store = new AppConfigStore(path, null);
            var snapshot = store.CaptureSnapshot();
            store.WriteString("Setting", "compatlayer", "true");
            snapshot.Restore();
            Check(File.ReadAllBytes(path).SequenceEqual(original), "配置恢复改变原始字节");
            File.WriteAllText(path, "[broken");
            RejectConfig(snapshot.Restore);
            Equal(root, "config.toml", "[broken");
        });

        Test("显示配置与启用状态一起保存，保留其他设置和注释", root =>
        {
            string path = LauncherConfigPreparation.Prepare(root, root);
            var store = new AppConfigStore(path, null);
            store.WriteString("Display", "mainscreen", "0");
            File.AppendAllText(path, "\n# user comment\n");
            store.WriteSection("Display", new Dictionary<string, string>
            {
                ["displayconfigure"] = "true", ["maindisplayid"] = "current-monitor",
                ["mainresolution"] = "1920x1080", ["mainrefresh"] = "120", ["mainrotation"] = "90"
            }, "mainscreen");
            Check(store.ReadBool("Display", "displayconfigure", false), "启用状态未保存");
            Check(store.ReadString("Display", "maindisplayid") == "current-monitor"
                && store.ReadString("Display", "mainresolution") == "1920x1080"
                && store.ReadString("Display", "mainrefresh") == "120"
                && store.ReadString("Display", "mainrotation") == "90", "显示配置未完整保存");
            Check(store.ReadString("Display", "mainscreen", null) == null, "旧编号未移除");
            Check(store.ReadString("Server", "activepreset") == "Asphyxia" && File.ReadAllText(path).Contains("# user comment"), "其他配置被改变");
        });
        Test("显示配置保存失败不能只写入启用状态，也不能重建配置", root =>
        {
            string path = LauncherConfigPreparation.Prepare(root, root); string original = File.ReadAllText(path);
            var store = new AppConfigStore(path, null);
            var values = new Dictionary<string, string> { ["displayconfigure"] = "true", ["mainresolution"] = "1920x1080" };
            using (var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                RejectConfig(() => store.WriteSection("Display", values));
            Equal(root, "config.toml", original);
            File.Delete(path);
            RejectConfig(() => store.WriteSection("Display", values));
            Check(!File.Exists(path), "显示配置保存重建了配置文件");
        });
        Test("配置首次创建在外层，重复准备不改写", root =>
        {
            string path = LauncherConfigPreparation.Prepare(root, root);
            Check(path == Path.Combine(root, "config.toml"), "配置位置错误");
            Check(!File.Exists(Path.Combine(root, "launcher/config.toml")), "创建了旧位置配置");
            string original = File.ReadAllText(path);
            DateTime modified = File.GetLastWriteTimeUtc(path);
            LauncherConfigPreparation.Prepare(root, root);
            Check(File.ReadAllText(path) == original && File.GetLastWriteTimeUtc(path) == modified, "重复启动重写了完整配置");
            Check(new AppConfigStore(path, null).ReadString("Server", "activepreset") == "Asphyxia", "默认预设错误");
        });
        Test("旧配置覆盖新位置并删除，后续仅使用新位置", root =>
        {
            string original = AppConfigDefaults.CreateDefaultConfigText().Replace("noasphyxia = \"false\"", "noasphyxia = \"true\"");
            Put(root, "launcher/config.toml", original); Put(root, "config.toml", "invalid destination");
            string path = LauncherConfigPreparation.Prepare(root, root);
            Check(File.ReadAllText(path) == original, "旧配置未完整覆盖");
            Check(!File.Exists(Path.Combine(root, "launcher/config.toml")), "旧文件未删除");
            var store = new AppConfigStore(path, null); store.WriteString("Setting", "noasphyxia", "false");
            LauncherConfigPreparation.Prepare(root, root);
            Check(!store.ReadBool("Setting", "noasphyxia", true), "旧配置被重复导入");
        });
        Test("迁移源与目标相同不删除配置", root =>
        {
            string outer = Path.Combine(root, "launcher"); Directory.CreateDirectory(outer);
            string text = AppConfigDefaults.CreateDefaultConfigText(); Put(outer, "config.toml", text);
            Check(File.ReadAllText(LauncherConfigPreparation.Prepare(outer, root)) == text, "同路径迁移损坏了配置");
        });
        Test("迁移源被占用保留两处原文件", root =>
        {
            Put(root, "launcher/config.toml", "source"); Put(root, "config.toml", "destination");
            using (var held = File.Open(Path.Combine(root, "launcher/config.toml"), FileMode.Open, FileAccess.Read, FileShare.None))
                RejectConfig(() => LauncherConfigPreparation.Prepare(root, root), root);
            Equal(root, "launcher/config.toml", "source"); Equal(root, "config.toml", "destination");
        });
        Test("迁移目标被占用保留两处原文件", root =>
        {
            Put(root, "launcher/config.toml", "source"); Put(root, "config.toml", "destination");
            using (var held = File.Open(Path.Combine(root, "config.toml"), FileMode.Open, FileAccess.Read, FileShare.None))
                RejectConfig(() => LauncherConfigPreparation.Prepare(root, root), root);
            Equal(root, "launcher/config.toml", "source"); Equal(root, "config.toml", "destination");
        });
        Test("迁移复制成功但源无法删除时停止并保留源", root =>
        {
            string old = Path.Combine(root, "launcher/config.toml"); string text = AppConfigDefaults.CreateDefaultConfigText(); Put(root, "launcher/config.toml", text);
            using (var held = File.Open(old, FileMode.Open, FileAccess.Read, FileShare.Read))
                RejectConfig(() => LauncherConfigPreparation.Prepare(root, root), root);
            Equal(root, "launcher/config.toml", text); Equal(root, "config.toml", text);
        });
        Test("损坏配置备份后修复", root =>
        {
            const string broken = "[Setting\ninvalid"; Put(root, "config.toml", broken);
            string path = LauncherConfigPreparation.Prepare(root, root);
            string backup = Directory.GetFiles(root, "config.toml.invalid.*.bak").Single();
            Check(File.ReadAllText(backup) == broken, "损坏原文未备份");
            Check(new AppConfigStore(path, null).ReadExistingText() == AppConfigDefaults.CreateDefaultConfigText(), "默认配置修复错误");
        });
        Test("补齐默认项和预设保留用户值、未知字段及注释", root =>
        {
            Put(root, "config.toml", "# user comment\n[Setting]\nnoasphyxia = \"true\" # keep\ncustom = 42\n[Server]\nactivepreset = \"Custom\"\n[[Server.Presets]]\nname = \"Custom\"\nserverurl = \"https://example.invalid\"\nextra = \"keep\" # extra\n[[Server.Presets]]\nname = \"Asphyxia\"\nserverurl = \"\" # url comment\nextra = 17\n");
            string path = LauncherConfigPreparation.Prepare(root, root); string content = File.ReadAllText(path);
            var store = new AppConfigStore(path, null);
            Check(store.ReadBool("Setting", "noasphyxia", false) && store.ReadString("Server", "activepreset") == "Custom", "用户值被覆盖");
            foreach (string retained in new[] { "# user comment", "# keep", "custom = 42", "# extra", "extra = 17", "# url comment" })
                Check(content.Contains(retained), "丢失内容：" + retained);
            Check(store.LoadServerPresets("无", "Asphyxia", "http://localhost:8083").Presets.Single(p => p.Name == "Asphyxia").ServerUrl == "http://localhost:8083", "预设 URL 未补齐");
            string before = content; LauncherConfigPreparation.Prepare(root, root);
            Check(File.ReadAllText(path) == before, "补齐不幂等");
        });
        foreach (string problem in new[] { "locked", "readonly", "deny-read", "deny-write", "deny-create" })
        {
            Test("配置准备失败停止：" + problem, root =>
            {
                string path = Path.Combine(root, "config.toml"); string text = AppConfigDefaults.CreateDefaultConfigText(); Put(root, "config.toml", text);
                FileStream held = null; DeniedAccess denied = null;
                try
                {
                    if (problem == "locked") held = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    if (problem == "readonly") File.SetAttributes(path, FileAttributes.ReadOnly);
                    if (problem == "deny-read") denied = new DeniedAccess(path, FileSystemRights.ReadData);
                    if (problem == "deny-write") denied = new DeniedAccess(path, FileSystemRights.WriteData);
                    if (problem == "deny-create") denied = new DeniedAccess(root, FileSystemRights.CreateFiles);
                    try { LauncherConfigPreparation.Prepare(root, root); throw new Exception("预期配置准备失败"); }
                    catch (ConfigCreationPermissionException) { throw new Exception("已有配置的访问错误不应触发提权"); }
                    catch (IOException ex) { Check(ex.Message.Contains(root), "错误未包含路径"); }
                }
                finally { held?.Dispose(); denied?.Dispose(); File.SetAttributes(path, FileAttributes.Normal); }
                Equal(root, "config.toml", text);
                Check(!Directory.GetFiles(root, "*.bak").Any() && !Directory.GetFiles(root, "*.tmp").Any(), "访问错误产生了备份或遗留临时文件");
            });
        }
        Test("缺失配置且目录拒绝创建时不产生配置", root =>
        {
            using (var denied = new DeniedAccess(root, FileSystemRights.CreateFiles))
            {
                try { LauncherConfigPreparation.Prepare(root, root); throw new Exception("创建应失败"); }
                catch (ConfigCreationPermissionException ex) { Check(ex.Message.Contains(root), "提权错误未包含路径"); }
            }
            Check(!File.Exists(Path.Combine(root, "config.toml")), "创建失败仍有配置");
        });
        Test("迁移创建新配置被拒绝时可提权且保留旧配置", root =>
        {
            string original = AppConfigDefaults.CreateDefaultConfigText(); Put(root, "launcher/config.toml", original);
            using (var denied = new DeniedAccess(root, FileSystemRights.CreateFiles))
            {
                try { LauncherConfigPreparation.Prepare(root, root); throw new Exception("迁移创建应失败"); }
                catch (ConfigCreationPermissionException) { }
            }
            Equal(root, "launcher/config.toml", original);
            Check(!File.Exists(Path.Combine(root, "config.toml")), "失败留下了新配置");
        });
        Test("主程序读取缺失或损坏配置不创建不修复", root =>
        {
            string path = Path.Combine(root, "config.toml"); var store = new AppConfigStore(path, null);
            RejectConfig(() => store.ReadExistingText()); Check(!File.Exists(path), "主程序创建了配置");
            Put(root, "config.toml", "[broken"); RejectConfig(() => store.ReadExistingText());
            Equal(root, "config.toml", "[broken"); Check(Directory.GetFiles(root).Length == 1, "主程序备份或修复了配置");
        });
        Test("主程序正常读取不改写，用户修改可以保存", root =>
        {
            string path = LauncherConfigPreparation.Prepare(root, root); string original = File.ReadAllText(path);
            var store = new AppConfigStore(path, null); store.ReadExistingText(); store.ReadBool("Setting", "compatlayer", false); store.ReadInt("Display", "mainrefresh", 59);
            var presets = store.LoadServerPresets("无", "Asphyxia", "http://localhost:8083");
            Check(File.ReadAllText(path) == original, "读取时写回了配置");
            store.WriteString("Setting", "noasphyxia", "true"); Check(store.ReadBool("Setting", "noasphyxia", false), "设置未保存");
            store.SaveServerPresets(presets.Presets, "无", "无"); Check(store.ReadString("Server", "activepreset") == "无", "预设未保存");
        });
        Test("运行中配置删除或损坏，所有保存入口均拒绝", root =>
        {
            string path = LauncherConfigPreparation.Prepare(root, root); var store = new AppConfigStore(path, null);
            var presets = store.LoadServerPresets("无", "Asphyxia", "http://localhost:8083");
            foreach (bool corrupted in new[] { false, true })
            {
                if (corrupted) File.WriteAllText(path, "[broken"); else File.Delete(path);
                RejectConfig(() => store.WriteString("Setting", "noasphyxia", "true"));
                RejectConfig(() => store.RemoveKey("Setting", "noasphyxia"));
                RejectConfig(() => store.SaveServerPresets(presets.Presets, "无", "无"));
                if (corrupted) Equal(root, "config.toml", "[broken"); else Check(!File.Exists(path), "保存重建了配置");
            }
        });
        Test("替换前配置消失不会通过 Move 重建", root =>
        {
            string path = LauncherConfigPreparation.Prepare(root, root);
            bool result = SafeFileWriter.TryReplaceExistingText(path, AppConfigDefaults.CreateDefaultConfigText(), candidate =>
            {
                if (candidate == path) File.Delete(path);
                return string.Empty;
            }, out _);
            Check(!result && !File.Exists(path), "配置在替换竞态中被重建");
            Check(!Directory.GetFiles(root, "*.tmp").Any(), "替换失败遗留临时文件");
        });
        foreach (string problem in new[] { "locked", "readonly", "deny-write" })
        {
            Test("运行中配置不能写入时保留原文：" + problem, root =>
            {
                string path = LauncherConfigPreparation.Prepare(root, root); string original = File.ReadAllText(path);
                var store = new AppConfigStore(path, null);
                FileStream held = null; DeniedAccess denied = null;
                try
                {
                    if (problem == "locked") held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (problem == "readonly") File.SetAttributes(path, FileAttributes.ReadOnly);
                    if (problem == "deny-write") denied = new DeniedAccess(path, FileSystemRights.WriteData);
                    RejectConfig(() => store.WriteString("Setting", "noasphyxia", "true"), path);
                }
                finally { held?.Dispose(); denied?.Dispose(); File.SetAttributes(path, FileAttributes.Normal); }
                Equal(root, "config.toml", original);
                Check(!Directory.GetFiles(root, "*.tmp").Any(), "保存失败遗留临时文件");
            });
        }
        Test("兼容层设置失败后的配置恢复也不能重建文件", root =>
        {
            string path = LauncherConfigPreparation.Prepare(root, root); var snapshot = new AppConfigStore(path, null).CaptureSnapshot();
            File.Delete(path); RejectConfig(() => snapshot.Restore());
            Check(!File.Exists(path), "设置失败恢复重建了配置");
        });
        Test("配置位置独立于工作目录及游戏路径覆盖", root =>
        {
            string app = Path.Combine(root, "launcher"); Directory.CreateDirectory(Path.Combine(app, "Libs"));
            string other = Path.Combine(root, "other"); Directory.CreateDirectory(other);
            string previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = other;
                Check(LauncherLocation.GetConfigurationDirectory(app) == root, "配置受工作目录影响");
                Check(LauncherLocation.ResolveBaseDirectory(new[] { "--basedir", other }, root, app) == other, "参数优先级错误");
                Check(LauncherLocation.ResolveBaseDirectory(Array.Empty<string>(), other, app) == other, "环境变量未生效");
                Check(LauncherLocation.ResolveBaseDirectory(Array.Empty<string>(), null, app) == root, "默认游戏目录错误");
                Put(other, "launcher/config.toml", AppConfigDefaults.CreateDefaultConfigText());
                Check(LauncherConfigPreparation.Prepare(root, other) == Path.Combine(root, "config.toml"), "游戏目录覆盖改变配置位置");
                Check(!File.Exists(Path.Combine(other, "launcher/config.toml")), "未从指定游戏目录迁移");
                Check(LauncherLocation.GetConfigurationDirectory(other) == other, "开发布局路径错误");
            }
            finally { Environment.CurrentDirectory = previous; }
        });
        Test("启动入口优先中文名且不回退到主程序", root =>
        {
            Put(root, "launcher/LazyBootstrap.exe", "main"); Put(root, "LazyBootstrap.exe", "main");
            RejectConfig(() => LauncherLocation.FindOuterLauncher(root), root);
            Put(root, "Launcher.exe", "outer"); Check(LauncherLocation.FindOuterLauncher(root) == Path.Combine(root, "Launcher.exe"), "未使用外层入口");
            Put(root, "启动.exe", "outer"); Check(LauncherLocation.FindOuterLauncher(root) == Path.Combine(root, "启动.exe"), "中文入口优先级错误");
        });
    }

    private static void RejectConfig(Action action, string expectedPath = null)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Check(expectedPath == null || ex.Message.Contains(expectedPath), "错误未包含路径：" + ex.Message);
            return;
        }
        throw new Exception("预期配置操作失败，但实际成功");
    }
}
