using System;
using System.Collections.Generic;
using System.Linq;

namespace LazyBootstrap.Serialization
{
    internal static class AppConfigDefaults
    {
        public const string SettingSectionName = "Setting";
        public const string DisplaySectionName = "Display";

        internal const string ServerSectionName = "Server";
        internal const string AsphyxiaPresetName = "Asphyxia";
        internal const string AsphyxiaDefaultUrl = "http://localhost:8083";

        internal static readonly ConfigDefaultEntry[] Defaults =
        [
            new ConfigDefaultEntry(SettingSectionName, "noasphyxia", "false"),
            new ConfigDefaultEntry(SettingSectionName, "auto-launch", "false"),
            new ConfigDefaultEntry(SettingSectionName, "disable-fso", "false"),
            new ConfigDefaultEntry(SettingSectionName, "compatlayer", "false"),
            new ConfigDefaultEntry(SettingSectionName, "cl-rendermode", "dx9on12"),
            new ConfigDefaultEntry(SettingSectionName, "use-system-config", "false"),

            new ConfigDefaultEntry(DisplaySectionName, "displayconfigure", "false"),
            new ConfigDefaultEntry(DisplaySectionName, "exitrestore", "true"),
            new ConfigDefaultEntry(DisplaySectionName, "mode", "single"),
            new ConfigDefaultEntry(DisplaySectionName, "maindisplayid", ""),
            new ConfigDefaultEntry(DisplaySectionName, "subdisplayid", ""),
            new ConfigDefaultEntry(DisplaySectionName, "subrotation", "0"),
            new ConfigDefaultEntry(DisplaySectionName, "mainrotation", "0"),
            new ConfigDefaultEntry(DisplaySectionName, "mainresolution", "640x480"),
            new ConfigDefaultEntry(DisplaySectionName, "subresolution", "640x480"),
            new ConfigDefaultEntry(DisplaySectionName, "mainrefresh", "59"),
            new ConfigDefaultEntry(DisplaySectionName, "subrefresh", "59")
        ];

        internal static string CreateDefaultConfigText()
        {
            var lines = new List<string>();
            AppendDefaultSection(lines, SettingSectionName);
            lines.Add(string.Empty);
            AppendDefaultSection(lines, DisplaySectionName);
            lines.Add(string.Empty);
            lines.Add($"[{ServerSectionName}]");
            lines.Add(TomlTextShared.BuildStringLine("activepreset", AsphyxiaPresetName));
            lines.Add(string.Empty);
            lines.Add("[[Server.Presets]]");
            lines.Add(TomlTextShared.BuildStringLine("name", AsphyxiaPresetName));
            lines.Add(TomlTextShared.BuildStringLine("serverurl", AsphyxiaDefaultUrl));
            lines.Add(TomlTextShared.BuildStringLine("pcbid", string.Empty));
            return string.Join(Environment.NewLine, lines);
        }

        private static void AppendDefaultSection(List<string> lines, string sectionName)
        {
            lines.Add($"[{sectionName}]");
            foreach (var entry in Defaults.Where(item => string.Equals(item.Section, sectionName, StringComparison.Ordinal)))
            {
                lines.Add(TomlTextShared.BuildStringLine(entry.Key, entry.Value));
            }
        }

        internal readonly record struct ConfigDefaultEntry(string Section, string Key, string Value);
    }
}
