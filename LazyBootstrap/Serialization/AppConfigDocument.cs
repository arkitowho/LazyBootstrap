using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Serialization;

namespace LazyBootstrap.Serialization;

internal sealed class ServerPresetItem
{
    public string Name { get; set; } = string.Empty;

    public string ServerUrl { get; set; } = string.Empty;

    public string PcbId { get; set; } = string.Empty;

    public override string ToString() => Name;
}

internal static class TomlTextShared
{
    public static string EscapeTomlString(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    public static string BuildStringLine(string key, string value)
    {
        return $"{key} = \"{EscapeTomlString(value)}\"";
    }

    public static void NormalizeBlankLines(List<string> lines, bool preserveSectionSeparator)
    {
        if (lines == null || lines.Count == 0)
        {
            return;
        }

        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                break;
            }

            lines.RemoveAt(i);
        }

        for (int i = lines.Count - 1; i > 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]) || !string.IsNullOrWhiteSpace(lines[i - 1]))
            {
                continue;
            }

            if (preserveSectionSeparator && IsSectionSeparator(lines, i))
            {
                continue;
            }

            lines.RemoveAt(i);
        }
    }

    private static bool IsSectionSeparator(List<string> lines, int blankLineIndex)
    {
        string previous = string.Empty;
        string next = string.Empty;

        for (int i = blankLineIndex - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                previous = lines[i].Trim();
                break;
            }
        }

        for (int i = blankLineIndex + 1; i < lines.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                next = lines[i].Trim();
                break;
            }
        }

        return !string.IsNullOrWhiteSpace(previous)
            && !string.IsNullOrWhiteSpace(next)
            && !previous.StartsWith("[", StringComparison.Ordinal)
            && next.StartsWith("[", StringComparison.Ordinal);
    }
}

// One in-memory document per operation; no file access or cross-operation cache.
internal sealed class AppConfigDocument
{
    private readonly string _original;
    private readonly TomlLineDocument _lines;
    private TomlTable _model;
    private bool _changed;
    private bool _modelDirty;
    internal string ModelError { get; private set; }

    private AppConfigDocument(string text)
    {
        _original = text;
        _lines = TomlLineDocument.FromText(text);
        LoadModel(text);
    }

    private void LoadModel(string text)
    {
        _model = null;
        ModelError = null;
        try { _model = TomlSerializer.Deserialize(text, AppConfigTomlContext.Default.TomlTable) ?? new TomlTable(); }
        catch (Exception ex) { ModelError = ex.Message; }
        _modelDirty = false;
    }

    public static AppConfigDocument Parse(string text)
    {
        string error = Validate(text);
        if (!string.IsNullOrEmpty(error)) throw new InvalidDataException(error);
        return new AppConfigDocument(text ?? string.Empty);
    }

    public static string Validate(string text)
    {
        try { SyntaxParser.ParseStrict(text ?? string.Empty, "config.toml", true); return string.Empty; }
        catch (Exception ex) { return ex.Message; }
    }

    public string ReadString(string section, string key, string defaultValue = "")
    {
        section = NormalizeName(section);
        key = NormalizeName(key);
        if (string.IsNullOrWhiteSpace(key)) return defaultValue;
        if (_modelDirty) LoadModel(_lines.ToText());
        if (_model != null && TryReadModelValue(_model, section, key, out var value)) return value;
        return _lines.TryReadString(section, key, out value) ? value : defaultValue;
    }

    public void UpsertString(string section, string key, string value)
    {
        _lines.UpsertString(NormalizeName(section), NormalizeName(key), value);
        Changed();
    }

    public bool RemoveKey(string section, string key)
    {
        if (!_lines.RemoveKey(NormalizeName(section), NormalizeName(key))) return false;
        Changed();
        return true;
    }

    public (List<ServerPresetItem> Presets, string ActivePreset) LoadServerPresets(
        string nonePresetName, string asphyxiaPresetName, string asphyxiaDefaultUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nonePresetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(asphyxiaPresetName);
        ArgumentNullException.ThrowIfNull(asphyxiaDefaultUrl);
        var (presets, active) = ReadPresets(nonePresetName);
        EnsureServerPresetDefaults(presets, asphyxiaPresetName, asphyxiaDefaultUrl);
        return (presets, presets.FirstOrDefault(p => string.Equals(p.Name, active, StringComparison.OrdinalIgnoreCase))?.Name ?? nonePresetName);
    }

    private (List<ServerPresetItem> Presets, string ActivePreset) ReadPresets(string nonePresetName)
    {
        if (_modelDirty) LoadModel(_lines.ToText());
        var presets = new List<ServerPresetItem> { new() { Name = nonePresetName } };
        string active = nonePresetName;
        if (_model != null) LoadServerPresetsFromModel(_model, presets, ref active);
        else _lines.LoadServerPresetsFromText(presets, ref active);
        return (presets, active);
    }

    public void SaveServerPresets(IEnumerable<ServerPresetItem> presets, string activePreset, string nonePresetName)
    {
        ArgumentNullException.ThrowIfNull(presets);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonePresetName);
        _lines.RemoveArrayTableBlocks("Server.Presets");
        _lines.UpsertString("Server", "activepreset", activePreset ?? nonePresetName);
        foreach (var preset in presets.Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name)
                     && !string.Equals(p.Name, nonePresetName, StringComparison.OrdinalIgnoreCase)))
            _lines.AppendServerPreset(preset);
        Changed();
    }

    public void CompleteDefaults()
    {
        // Read the original semantic model once before applying comment-preserving edits.
        var missing = AppConfigDefaults.Defaults.Where(entry =>
        {
            string existing = ReadString(entry.Section, entry.Key, null);
            return existing == null || (string.IsNullOrWhiteSpace(existing) && existing != entry.Value);
        }).ToArray();
        const string none = "无";
        var original = ReadPresets(none).Presets;
        var prepared = LoadServerPresets(none, AppConfigDefaults.AsphyxiaPresetName, AppConfigDefaults.AsphyxiaDefaultUrl);
        string active = ReadString("Server", "activepreset", null);
        foreach (var entry in missing) UpsertString(entry.Section, entry.Key, entry.Value);
        foreach (var preset in prepared.Presets.Where(p => p.Name != none))
        {
            var prior = original.FirstOrDefault(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
            if (prior == null) { _lines.AppendServerPreset(preset); Changed(); }
            else if (prior.ServerUrl != preset.ServerUrl)
            {
                _lines.UpsertServerPresetString(preset.Name, "serverurl", preset.ServerUrl);
                Changed();
            }
        }
        if (active != prepared.ActivePreset) UpsertString("Server", "activepreset", prepared.ActivePreset);
    }

    private void Changed() { _changed = true; _modelDirty = true; }
    public void NormalizeBlankLines(bool preserveSectionSeparator)
    {
        _lines.NormalizeBlankLines(preserveSectionSeparator);
        Changed();
    }
    public string ToText() => _changed ? _lines.ToText() : _original;

    private static bool TryReadModelValue(TomlTable model, string sectionName, string keyName, out string value)
    {
        value = string.Empty;
        if (!TryGetSectionTable(model, sectionName, out var section))
        {
            return false;
        }

        if (!TryGetValue(section, keyName, out var rawValue))
        {
            return false;
        }

        value = ConvertTomlValue(rawValue);
        return true;
    }

    private static bool TryGetSectionTable(TomlTable model, string sectionName, out TomlTable section)
    {
        section = null;
        if (model == null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sectionName))
        {
            section = model;
            return true;
        }

        TomlTable current = model;
        foreach (var segment in sectionName.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryGetValue(current, segment, out var next) || next is not TomlTable nextTable)
            {
                return false;
            }

            current = nextTable;
        }

        section = current;
        return true;
    }

    private static bool TryGetValue(TomlTable table, string key, out object value)
    {
        value = null;
        if (table == null || string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        if (table.TryGetValue(key, out value))
        {
            return true;
        }

        foreach (var entry in table)
        {
            if (string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        return false;
    }

    private static string ConvertTomlValue(object value)
    {
        return value switch
        {
            null => string.Empty,
            string text => text,
            bool boolean => boolean ? "true" : "false",
            int number => number.ToString(CultureInfo.InvariantCulture),
            long number => number.ToString(CultureInfo.InvariantCulture),
            float number => number.ToString(CultureInfo.InvariantCulture),
            double number => number.ToString(CultureInfo.InvariantCulture),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static void LoadServerPresetsFromModel(
        TomlTable model,
        List<ServerPresetItem> presets,
        ref string activePreset)
    {
        if (TryGetSectionTable(model, "Server", out var serverTable))
        {
            if (TryGetValue(serverTable, "activepreset", out var active))
            {
                activePreset = ConvertTomlValue(active);
            }

            if (TryGetValue(serverTable, "Presets", out var serverPresets))
            {
                AddPresetsFromObject(serverPresets, presets);
            }
        }

    }

    private static void AddPresetsFromObject(object value, List<ServerPresetItem> presets)
    {
        if (value is TomlTableArray array)
        {
            foreach (var table in array)
            {
                AddPresetFromTable(table, presets);
            }

            return;
        }

        if (value is TomlArray tomlArray)
        {
            foreach (var item in tomlArray)
            {
                if (item is TomlTable table)
                {
                    AddPresetFromTable(table, presets);
                }
            }
        }
    }

    private static void AddPresetFromTable(TomlTable table, List<ServerPresetItem> presets)
    {
        if (table == null)
        {
            return;
        }

        string name = TryGetValue(table, "name", out var rawName) ? ConvertTomlValue(rawName) : string.Empty;
        if (string.IsNullOrWhiteSpace(name)
            || presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        presets.Add(new ServerPresetItem
        {
            Name = name,
            ServerUrl = TryGetValue(table, "serverurl", out var serverUrl) ? ConvertTomlValue(serverUrl) : string.Empty,
            PcbId = TryGetValue(table, "pcbid", out var pcbId) ? ConvertTomlValue(pcbId) : string.Empty
        });
    }

    private static void EnsureServerPresetDefaults(
        List<ServerPresetItem> presets,
        string asphyxiaPresetName,
        string asphyxiaDefaultUrl)
    {
        var existing = presets.FirstOrDefault(p => string.Equals(p.Name, asphyxiaPresetName, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            presets.Add(new ServerPresetItem
            {
                Name = asphyxiaPresetName,
                ServerUrl = asphyxiaDefaultUrl,
                PcbId = string.Empty
            });
            return;
        }

        if (!string.IsNullOrWhiteSpace(existing.ServerUrl))
        {
            return;
        }

        existing.ServerUrl = asphyxiaDefaultUrl;
    }

    private static string NormalizeName(string value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private sealed class TomlLineDocument
    {
        private readonly List<string> _lines;
        private readonly string _newLine;

        private TomlLineDocument(IEnumerable<string> lines, string newLine)
        {
            _lines = lines?.ToList() ?? new List<string>();
            _newLine = newLine;
        }

        public static TomlLineDocument Empty()
        {
            return new TomlLineDocument(Array.Empty<string>(), Environment.NewLine);
        }

        public static TomlLineDocument FromText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return Empty();
            }

            var lines = new List<string>();
            using var reader = new StringReader(text);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lines.Add(line);
            }

            // Retain the source newline style, including newlines inside multiline values.
            int newline = text.IndexOf('\n');
            string separator = newline < 0 ? Environment.NewLine
                : newline > 0 && text[newline - 1] == '\r' ? "\r\n" : "\n";
            return new TomlLineDocument(lines, separator);
        }

        public string ToText()
        {
            return string.Join(_newLine, _lines);
        }

        public void NormalizeBlankLines(bool preserveSectionSeparator)
        {
            TomlTextShared.NormalizeBlankLines(_lines, preserveSectionSeparator);
        }

        public bool TryReadString(string sectionName, string keyName, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrWhiteSpace(keyName))
            {
                return false;
            }

            if (!TryGetSectionBounds(sectionName, out _, out var contentStart, out var contentEndExclusive))
            {
                return false;
            }

            for (int i = contentStart; i < contentEndExclusive; i++)
            {
                if (!TrySplitKeyValue(_lines[i], out var parsedKey, out var rawValue, out _)
                    || !string.Equals(parsedKey, keyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                value = ParseScalarToString(rawValue);
                return true;
            }

            return false;
        }

        public void UpsertString(string sectionName, string keyName, string value)
        {
            string valueLine = TomlTextShared.BuildStringLine(keyName, value ?? string.Empty);
            if (!TryGetSectionBounds(sectionName, out var headerIndex, out var contentStart, out var contentEndExclusive))
            {
                AppendSectionWithLine(sectionName, valueLine);
                return;
            }

            for (int i = contentStart; i < contentEndExclusive; i++)
            {
                if (!TrySplitKeyValue(_lines[i], out var parsedKey, out _, out var trailingComment)
                    || !string.Equals(parsedKey, keyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string indent = GetIndent(_lines[i]);
                _lines[i] = AppendTrailingComment(indent + valueLine, trailingComment);
                return;
            }

            int insertIndex = contentEndExclusive;
            while (insertIndex > contentStart && string.IsNullOrWhiteSpace(_lines[insertIndex - 1]))
            {
                insertIndex--;
            }

            if (headerIndex >= 0 || string.IsNullOrWhiteSpace(sectionName))
            {
                _lines.Insert(insertIndex, valueLine);
            }
        }

        public bool RemoveKey(string sectionName, string keyName)
        {
            if (string.IsNullOrWhiteSpace(keyName)
                || !TryGetSectionBounds(sectionName, out _, out var contentStart, out var contentEndExclusive))
            {
                return false;
            }

            bool removed = false;
            for (int i = contentEndExclusive - 1; i >= contentStart; i--)
            {
                if (!TrySplitKeyValue(_lines[i], out var parsedKey, out _, out _)
                    || !string.Equals(parsedKey, keyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _lines.RemoveAt(i);
                removed = true;
            }

            return removed;
        }

        public void RemoveArrayTableBlocks(string sectionName)
        {
            for (int i = 0; i < _lines.Count;)
            {
                if (!TryGetArraySectionName(_lines[i], out var parsedSection)
                    || !string.Equals(parsedSection, sectionName, StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    continue;
                }

                int end = i + 1;
                while (end < _lines.Count && !IsAnySectionHeader(_lines[end]))
                {
                    end++;
                }

                RemoveRange(i, end - i);
            }
        }

        public void AppendServerPreset(ServerPresetItem preset)
        {
            if (_lines.Count > 0 && !string.IsNullOrWhiteSpace(_lines[^1]))
            {
                _lines.Add(string.Empty);
            }

            _lines.Add("[[Server.Presets]]");
            _lines.Add(TomlTextShared.BuildStringLine("name", preset.Name ?? string.Empty));
            _lines.Add(TomlTextShared.BuildStringLine("serverurl", preset.ServerUrl ?? string.Empty));
            _lines.Add(TomlTextShared.BuildStringLine("pcbid", preset.PcbId ?? string.Empty));
        }

        // Update one field without discarding other preset fields or comments.
        public void UpsertServerPresetString(string presetName, string key, string value)
        {
            for (int start = 0; start < _lines.Count; start++)
            {
                if (!TryGetArraySectionName(_lines[start], out var section)
                    || !string.Equals(section, "Server.Presets", StringComparison.OrdinalIgnoreCase)) continue;
                int end = start + 1;
                while (end < _lines.Count && !IsAnySectionHeader(_lines[end])) end++;
                bool matches = false;
                for (int i = start + 1; i < end; i++)
                    if (TrySplitKeyValue(_lines[i], out var name, out var raw, out _)
                        && string.Equals(name, "name", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ParseScalarToString(raw), presetName, StringComparison.OrdinalIgnoreCase)) matches = true;
                if (!matches) continue;
                string line = TomlTextShared.BuildStringLine(key, value);
                for (int i = start + 1; i < end; i++)
                {
                    if (!TrySplitKeyValue(_lines[i], out var name, out _, out var comment)
                        || !string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) continue;
                    _lines[i] = AppendTrailingComment(GetIndent(_lines[i]) + line, comment);
                    return;
                }
                _lines.Insert(end, line);
                return;
            }
            throw new InvalidDataException($"无法定位服务器预设：{presetName}");
        }

        public void LoadServerPresetsFromText(
            List<ServerPresetItem> presets,
            ref string activePreset)
        {
            ServerPresetItem current = null;
            bool inServerSection = false;

            void CommitCurrent()
            {
                if (current == null
                    || string.IsNullOrWhiteSpace(current.Name)
                    || presets.Any(p => string.Equals(p.Name, current.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                presets.Add(current);
            }

            foreach (var rawLine in _lines)
            {
                if (TryGetStandardSectionName(rawLine, out var standardSection))
                {
                    if (current != null)
                    {
                        CommitCurrent();
                        current = null;
                    }

                    inServerSection = string.Equals(standardSection, "Server", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (TryGetArraySectionName(rawLine, out var arraySection))
                {
                    if (current != null)
                    {
                        CommitCurrent();
                    }

                    bool isPresetSection = string.Equals(arraySection, "Server.Presets", StringComparison.OrdinalIgnoreCase);
                    current = isPresetSection ? new ServerPresetItem() : null;
                    inServerSection = false;
                    continue;
                }

                if (inServerSection)
                {
                    if (TrySplitKeyValue(rawLine, out var key, out var rawValue, out _)
                        && string.Equals(key, "activepreset", StringComparison.OrdinalIgnoreCase))
                    {
                        activePreset = ParseScalarToString(rawValue);
                    }

                    continue;
                }

                if (current == null
                    || !TrySplitKeyValue(rawLine, out var presetKey, out var presetRawValue, out _))
                {
                    continue;
                }

                string value = ParseScalarToString(presetRawValue);
                if (string.Equals(presetKey, "name", StringComparison.OrdinalIgnoreCase))
                {
                    current.Name = value;
                }
                else if (string.Equals(presetKey, "serverurl", StringComparison.OrdinalIgnoreCase))
                {
                    current.ServerUrl = value;
                }
                else if (string.Equals(presetKey, "pcbid", StringComparison.OrdinalIgnoreCase))
                {
                    current.PcbId = value;
                }
            }

            CommitCurrent();
        }

        private bool TryGetSectionBounds(
            string sectionName,
            out int headerIndex,
            out int contentStart,
            out int contentEndExclusive)
        {
            headerIndex = -1;
            contentStart = 0;
            contentEndExclusive = _lines.Count;

            if (string.IsNullOrWhiteSpace(sectionName))
            {
                for (int i = 0; i < _lines.Count; i++)
                {
                    if (IsAnySectionHeader(_lines[i]))
                    {
                        contentEndExclusive = i;
                        break;
                    }
                }

                return true;
            }

            for (int i = 0; i < _lines.Count; i++)
            {
                if (TryGetStandardSectionName(_lines[i], out var parsedSection)
                    && string.Equals(parsedSection, sectionName, StringComparison.OrdinalIgnoreCase))
                {
                    headerIndex = i;
                    contentStart = i + 1;
                    break;
                }
            }

            if (headerIndex < 0)
            {
                return false;
            }

            contentEndExclusive = _lines.Count;
            for (int i = contentStart; i < _lines.Count; i++)
            {
                if (IsAnySectionHeader(_lines[i]))
                {
                    contentEndExclusive = i;
                    break;
                }
            }

            return true;
        }

        private void AppendSectionWithLine(string sectionName, string valueLine)
        {
            if (_lines.Count > 0 && !string.IsNullOrWhiteSpace(_lines[^1]))
            {
                _lines.Add(string.Empty);
            }

            if (!string.IsNullOrWhiteSpace(sectionName))
            {
                _lines.Add($"[{sectionName}]");
            }

            _lines.Add(valueLine);
        }

        private void RemoveRange(int index, int count)
        {
            if (count <= 0)
            {
                return;
            }

            _lines.RemoveRange(index, count);
        }

        private static bool IsAnySectionHeader(string line)
        {
            return TryGetStandardSectionName(line, out _) || TryGetArraySectionName(line, out _);
        }

        private static bool TryGetStandardSectionName(string line, out string sectionName)
        {
            sectionName = string.Empty;
            string header = StripInlineComment(line).Trim();
            if (string.IsNullOrWhiteSpace(header)
                || !header.StartsWith("[", StringComparison.Ordinal)
                || !header.EndsWith("]", StringComparison.Ordinal)
                || header.StartsWith("[[", StringComparison.Ordinal)
                || header.EndsWith("]]", StringComparison.Ordinal))
            {
                return false;
            }

            sectionName = header.Substring(1, header.Length - 2).Trim();
            return !string.IsNullOrWhiteSpace(sectionName);
        }

        private static bool TryGetArraySectionName(string line, out string sectionName)
        {
            sectionName = string.Empty;
            string header = StripInlineComment(line).Trim();
            if (string.IsNullOrWhiteSpace(header)
                || !header.StartsWith("[[", StringComparison.Ordinal)
                || !header.EndsWith("]]", StringComparison.Ordinal)
                || header.Length <= 4)
            {
                return false;
            }

            sectionName = header.Substring(2, header.Length - 4).Trim();
            return !string.IsNullOrWhiteSpace(sectionName);
        }

        private static bool TrySplitKeyValue(string line, out string key, out string rawValue, out string trailingComment)
        {
            key = string.Empty;
            rawValue = string.Empty;
            trailingComment = string.Empty;

            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                return false;
            }

            int equalsIndex = FindUnquotedEquals(line);
            if (equalsIndex <= 0)
            {
                return false;
            }

            key = NormalizeKeyText(line.Substring(0, equalsIndex).Trim());
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            string valueWithComment = line.Substring(equalsIndex + 1);
            trailingComment = ExtractTrailingComment(valueWithComment, out rawValue);
            rawValue = rawValue.Trim();
            return true;
        }

        private static int FindUnquotedEquals(string line)
        {
            bool inDoubleQuote = false;
            bool inSingleQuote = false;
            bool escaped = false;

            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\' && inDoubleQuote)
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                    continue;
                }

                if (ch == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inSingleQuote;
                    continue;
                }

                if (ch == '=' && !inDoubleQuote && !inSingleQuote)
                {
                    return i;
                }
            }

            return -1;
        }

        private static string ExtractTrailingComment(string text, out string valuePart)
        {
            bool inDoubleQuote = false;
            bool inSingleQuote = false;
            bool escaped = false;

            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\' && inDoubleQuote)
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"' && !inSingleQuote)
                {
                    inDoubleQuote = !inDoubleQuote;
                    continue;
                }

                if (ch == '\'' && !inDoubleQuote)
                {
                    inSingleQuote = !inSingleQuote;
                    continue;
                }

                if (ch == '#' && !inDoubleQuote && !inSingleQuote)
                {
                    valuePart = text.Substring(0, i);
                    return text.Substring(i).TrimEnd();
                }
            }

            valuePart = text;
            return string.Empty;
        }

        private static string StripInlineComment(string line)
        {
            ExtractTrailingComment(line ?? string.Empty, out var valuePart);
            return valuePart;
        }

        private static string ParseScalarToString(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return string.Empty;
            }

            try
            {
                var table = TomlSerializer.Deserialize($"value = {rawValue}", AppConfigTomlContext.Default.TomlTable) ?? new TomlTable();
                return table.TryGetValue("value", out var value)
                    ? ConvertTomlValue(value)
                    : string.Empty;
            }
            catch
            {
                return ParseLegacyScalar(rawValue);
            }
        }

        private static string ParseLegacyScalar(string rawValue)
        {
            string value = StripInlineComment(rawValue).Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                return UnescapeBasicString(value.Substring(1, value.Length - 2));
            }

            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                return value.Substring(1, value.Length - 2);
            }

            return value;
        }

        private static string UnescapeBasicString(string value)
        {
            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char ch = value[i];
                if (ch != '\\' || i + 1 >= value.Length)
                {
                    builder.Append(ch);
                    continue;
                }

                char escaped = value[++i];
                builder.Append(escaped switch
                {
                    'b' => '\b',
                    't' => '\t',
                    'n' => '\n',
                    'f' => '\f',
                    'r' => '\r',
                    '"' => '"',
                    '\\' => '\\',
                    _ => escaped
                });
            }

            return builder.ToString();
        }

        private static string NormalizeKeyText(string key)
        {
            if (key.Length >= 2 && key[0] == '"' && key[^1] == '"')
            {
                return ParseLegacyScalar(key);
            }

            if (key.Length >= 2 && key[0] == '\'' && key[^1] == '\'')
            {
                return key.Substring(1, key.Length - 2);
            }

            return key;
        }

        private static string GetIndent(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return string.Empty;
            }

            int length = 0;
            while (length < line.Length && char.IsWhiteSpace(line[length]))
            {
                length++;
            }

            return length == 0 ? string.Empty : line.Substring(0, length);
        }

        private static string AppendTrailingComment(string line, string comment)
        {
            return string.IsNullOrWhiteSpace(comment)
                ? line
                : $"{line} {comment.TrimStart()}";
        }
    }
}

[TomlSerializable(typeof(TomlTable))]
internal partial class AppConfigTomlContext : TomlSerializerContext
{
}
