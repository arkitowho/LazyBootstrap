using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LazyBootstrap.MediaUpdate;

internal sealed class MediaUpdateManifest
{
    public int SchemaVersion { get; set; }
    public List<MediaUpdateOperation> Operations { get; set; } = [];

    internal static MediaUpdateManifest Parse(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            ValidateJson(document.RootElement);
            var manifest = JsonSerializer.Deserialize(bytes, MediaUpdateJsonContext.Default.MediaUpdateManifest);
            if (manifest == null || manifest.SchemaVersion != 1 || manifest.Operations == null || manifest.Operations.Count == 0)
                throw new IOException("更新清单版本或操作列表无效。");
            foreach (var operation in manifest.Operations) ValidateOperation(operation);
            return manifest;
        }
        catch (JsonException ex) { throw new IOException("更新清单 JSON 无效：" + ex.Message, ex); }
    }

    internal static void ValidateJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new IOException("JSON 中存在重复字段：" + property.Name);
                if (property.Value.ValueKind == JsonValueKind.Null) throw new IOException("JSON 字段不能为 null：" + property.Name);
                ValidateJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) ValidateJson(child);
    }

    private static void ValidateOperation(MediaUpdateOperation op)
    {
        if (op == null) throw new IOException("更新操作不能为空。");
        MediaUpdateSecurity.ValidateRelativePath(op.Target);
        if (op.Type is "copy" or "mirror")
        {
            MediaUpdateSecurity.ValidateRelativePath(op.Source);
            if (!op.Source.Replace('\\', '/').StartsWith("source/", StringComparison.OrdinalIgnoreCase)
                && !op.Source.Equals("source", StringComparison.OrdinalIgnoreCase))
                throw new IOException("更新源必须位于包内 source 目录。");
            if (op.Edits != null || op.Encoding != null || op.Namespaces != null) throw new IOException("复制操作不能包含 XML 编辑字段。");
        }
        else if (op.Type == "delete")
        {
            if (op.Source != null || op.Edits != null || op.Encoding != null || op.Namespaces != null) throw new IOException("删除操作含有无效字段。");
        }
        else if (op.Type == "editXml")
        {
            if (op.Source != null || op.Edits == null || op.Edits.Count == 0) throw new IOException("XML 编辑操作无效。");
            if ((op.Encoding ?? "auto") is not ("auto" or "utf-8" or "utf-16le" or "utf-16be" or "gbk" or "shift-jis"))
                throw new IOException("不支持的文本编码：" + op.Encoding);
            MediaXmlEditor.Validate(op);
        }
        else throw new IOException("未知更新操作：" + op.Type);
    }
}

internal sealed class MediaUpdateOperation
{
    public string Type { get; set; }
    public string Source { get; set; }
    public string Target { get; set; }
    public string Encoding { get; set; }
    public Dictionary<string, string> Namespaces { get; set; }
    public List<MediaXmlEdit> Edits { get; set; }
}

internal sealed class MediaXmlEdit
{
    public string Action { get; set; }
    [JsonPropertyName("xpath")] public string XPath { get; set; }
    public string Name { get; set; }
    public string Value { get; set; }
    public string Xml { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MediaUpdateManifest))]
[JsonSerializable(typeof(MediaUpdateChecksumManifest))]
internal partial class MediaUpdateJsonContext : JsonSerializerContext;
