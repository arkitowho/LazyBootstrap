using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.Serialization;

internal class AppConfigStore
{
    private readonly string _path;
    private readonly object _sync = new object();
    private readonly ILogger<AppConfigStore> _logger;

    public AppConfigStore(string tomlPath, ILogger<AppConfigStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tomlPath);
        _path = new FileInfo(tomlPath).FullName;
        _logger = logger;
    }

    // Reading never creates, repairs or probes write permissions.
    internal string ReadExistingText()
    {
        string text = File.ReadAllText(_path, Encoding.UTF8);
        string error = AppConfigDocument.Validate(text);
        if (!string.IsNullOrEmpty(error))
            throw new InvalidDataException($"配置格式错误：{_path}\n{error}");
        return text;
    }

    public void WriteString(string section, string key, string value)
    {
        lock (_sync)
        {
            string sectionName = NormalizeName(section);
            string keyName = NormalizeName(key);
            if (string.IsNullOrWhiteSpace(keyName))
            {
                return;
            }

            var document = LoadDocumentLocked();
            document.UpsertString(sectionName, keyName, value ?? string.Empty);
            WriteDocumentLocked(document);
        }
    }

    public void RemoveKey(string section, string key)
    {
        lock (_sync)
        {
            string sectionName = NormalizeName(section);
            string keyName = NormalizeName(key);
            if (string.IsNullOrWhiteSpace(keyName))
            {
                return;
            }

            var document = LoadDocumentLocked();
            if (document.RemoveKey(sectionName, keyName))
            {
                WriteDocumentLocked(document);
            }
        }
    }

    public void WriteSection(string section, IReadOnlyDictionary<string, string> values, params string[] removeKeys)
    {
        ArgumentNullException.ThrowIfNull(values);
        lock (_sync)
        {
            string sectionName = NormalizeName(section);
            var document = LoadDocumentLocked();
            foreach (var entry in values)
                document.UpsertString(sectionName, entry.Key, entry.Value ?? string.Empty);
            foreach (string key in removeKeys)
                document.RemoveKey(sectionName, key);
            // Persist the selection and enabled flag together; failed writes leave both unchanged.
            WriteDocumentLocked(document);
        }
    }

    public string ReadString(string section, string key, string defaultValue = "")
    {
        if (string.IsNullOrWhiteSpace(NormalizeName(key))) return defaultValue;
        lock (_sync)
        {
            var document = LoadDocumentLocked();
            if (!string.IsNullOrWhiteSpace(document.ModelError))
                _logger?.LogWarning("Falling back to text-level config read because TOML model loading failed: {Error}", document.ModelError);
            return document.ReadString(section, key, defaultValue);
        }
    }

    public bool ReadBool(string section, string key, bool defaultValue)
    {
        var value = ReadString(section, key, defaultValue ? "true" : "false");
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    public int ReadInt(string section, string key, int defaultValue)
    {
        var value = ReadString(section, key, defaultValue.ToString(CultureInfo.InvariantCulture));
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : defaultValue;
    }

    public (List<ServerPresetItem> Presets, string ActivePreset) LoadServerPresets(
        string nonePresetName, string asphyxiaPresetName, string asphyxiaDefaultUrl)
    {
        lock (_sync)
        {
            var document = LoadDocumentLocked();
            if (!string.IsNullOrWhiteSpace(document.ModelError))
                _logger?.LogWarning("Falling back to text-level server preset read because TOML model loading failed: {Error}", document.ModelError);
            return document.LoadServerPresets(nonePresetName, asphyxiaPresetName, asphyxiaDefaultUrl);
        }
    }

    public void SaveServerPresets(IEnumerable<ServerPresetItem> presets, string activePreset, string nonePresetName)
    {
        lock (_sync)
        {
            var document = LoadDocumentLocked();
            document.SaveServerPresets(presets, activePreset, nonePresetName);
            WriteDocumentLocked(document, preserveSectionSeparator: false);
        }
    }

    private AppConfigDocument LoadDocumentLocked()
    {
        try { return AppConfigDocument.Parse(File.ReadAllText(_path, Encoding.UTF8)); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"配置格式错误：{_path}\n{ex.Message}", ex); }
    }

    private void WriteDocumentLocked(AppConfigDocument document, bool preserveSectionSeparator = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.NormalizeBlankLines(preserveSectionSeparator);
        WriteTextLocked(document.ToText());
    }

    private void WriteTextLocked(string content)
    {
        var validationError = AppConfigDocument.Validate(content ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            throw new InvalidDataException($"Serialized TOML failed validation: {validationError}");
        }

        if (!SafeFileWriter.TryReplaceExistingText(_path, content ?? string.Empty, ValidateTomlFile, out var error))
        {
            throw new IOException($"无法保存配置：{_path}\n{error}");
        }
    }

    internal static string ValidateTomlFile(string path)
    {
        try { return AppConfigDocument.Validate(File.ReadAllText(path, Encoding.UTF8)); }
        catch (Exception ex) { return ex.Message; }
    }

    internal Snapshot CaptureSnapshot()
    {
        lock (_sync) return new Snapshot(this, File.Exists(_path) ? File.ReadAllBytes(_path) : null);
    }

    internal sealed class Snapshot
    {
        private readonly AppConfigStore _store;
        private readonly byte[] _content;
        internal Snapshot(AppConfigStore store, byte[] content) { _store = store; _content = content; }
        public void Restore()
        {
            if (_content == null) return;
            lock (_store._sync)
                if (!SafeFileWriter.TryReplaceExistingBytes(_store._path, _content, ValidateTomlFile, out var error))
                    throw new IOException(error);
        }
    }

    private static string NormalizeName(string value) => (value ?? string.Empty).Trim();
}
