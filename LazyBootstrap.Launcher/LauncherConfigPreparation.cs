using System;
using System.IO;
using System.Linq;
using System.Text;
using LazyBootstrap.FileSystem;
using LazyBootstrap.Serialization;

namespace LazyBootstrap.Launcher;

internal sealed class ConfigCreationPermissionException(string path, Exception inner)
    : IOException($"没有权限创建配置文件：{path}\n{inner.Message}", inner);

internal static class LauncherConfigPreparation
{
    internal static string Prepare(string launcherDirectory, string gameDirectory)
    {
        string target = Path.GetFullPath(Path.Combine(launcherDirectory, "config.toml"));
        string legacy = Path.GetFullPath(Path.Combine(gameDirectory, "launcher", "config.toml"));
        string operation = "迁移配置";
        try
        {
            if (!string.Equals(legacy, target, StringComparison.OrdinalIgnoreCase) && Exists(legacy))
            {
                byte[] content = File.ReadAllBytes(legacy);
                if (!Exists(target)) Create(target, content);
                else if (!SafeFileWriter.TryWriteAllBytes(target, content, null, out var error))
                    throw new IOException(error);
                File.Delete(legacy);
            }

            operation = "创建配置";
            if (!Exists(target)) Create(target, Encoding.UTF8.GetBytes(AppConfigDefaults.CreateDefaultConfigText()));

            operation = "检查配置读写权限";
            using (var probe = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            string probePath = Path.Combine(launcherDirectory, $".lazybootstrap.{Guid.NewGuid():N}.tmp");
            using (var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       1, FileOptions.DeleteOnClose)) { }

            operation = "读取配置";
            string text = File.ReadAllText(target);
            if (!string.IsNullOrEmpty(AppConfigStore.ValidateTomlText(text)))
            {
                operation = "备份并修复损坏配置";
                string backup = target + ".invalid." + DateTime.Now.ToString("yyyyMMddHHmmss") + "." + Guid.NewGuid().ToString("N") + ".bak";
                // Copy first: a failed repair leaves both the original and its backup intact.
                File.Copy(target, backup);
                text = AppConfigDefaults.CreateDefaultConfigText();
                Write(target, text);
            }

            operation = "补齐配置";
            string prepared = CompleteDefaults(target, text);
            if (!string.Equals(prepared, text, StringComparison.Ordinal)) Write(target, prepared);
            return target;
        }
        catch (ConfigCreationPermissionException) { throw; }
        catch (Exception ex)
        {
            throw new IOException($"{operation}失败。\n配置：{target}\n旧配置：{legacy}\n原因：{ex.Message}", ex);
        }
    }

    private static void Create(string path, byte[] content)
    {
        FileStream stream;
        // Classify only an actual new-file creation failure, not read, repair or sharing errors.
        try { stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (UnauthorizedAccessException ex) { throw new ConfigCreationPermissionException(path, ex); }
        try
        {
            using (stream)
            {
                stream.Write(content);
                stream.Flush(true);
            }
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException($"配置路径是目录：{path}");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void Write(string path, string text)
    {
        if (!SafeFileWriter.TryWriteAllText(path, text, AppConfigStore.ValidateTomlFile, out var error))
            throw new IOException(error);
    }

    private static string CompleteDefaults(string path, string text)
    {
        var config = new AppConfigStore(path, null);
        var document = AppConfigStore.TomlLineDocument.FromText(text);
        bool changed = false;
        foreach (var entry in AppConfigDefaults.Defaults)
        {
            string existing = config.ReadString(entry.Section, entry.Key, null);
            if (existing == null || (string.IsNullOrWhiteSpace(existing) && existing != entry.Value))
            {
                document.UpsertString(entry.Section, entry.Key, entry.Value);
                changed = true;
            }
        }

        const string none = "无";
        var presets = config.LoadServerPresets(none, AppConfigDefaults.AsphyxiaPresetName, AppConfigDefaults.AsphyxiaDefaultUrl);
        // Compare original presets with normalized ones, changing only the required fields.
        var original = new System.Collections.Generic.List<ServerPresetItem>();
        string active = none;
        bool hasPresets = false;
        document.LoadServerPresetsFromText(original, ref active, ref hasPresets);
        foreach (var preset in presets.Presets.Where(p => p.Name != none))
        {
            var prior = original.FirstOrDefault(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
            if (prior == null)
            {
                document.AppendServerPreset(preset);
                changed = true;
            }
            else if (prior.ServerUrl != preset.ServerUrl)
            {
                document.UpsertServerPresetString(preset.Name, "serverurl", preset.ServerUrl);
                changed = true;
            }
        }
        if (config.ReadString("Server", "activepreset", null) != presets.ActivePreset)
        {
            document.UpsertString("Server", "activepreset", presets.ActivePreset);
            changed = true;
        }
        return changed ? document.ToText() : text;
    }
}
