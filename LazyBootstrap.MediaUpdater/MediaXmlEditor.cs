using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.XPath;

namespace LazyBootstrap.MediaUpdate;

// XML is transformed entirely in memory; only the installation engine writes the resulting bytes.
internal static class MediaXmlEditor
{
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    public static void Validate(MediaUpdateOperation operation, CancellationToken cancel = default)
    {
        int index = 0;
        try
        {
            var namespaces = CreateNamespaces(new NameTable(), operation);
            foreach (var edit in operation.Edits)
            {
                cancel.ThrowIfCancellationRequested();
                index++;
                if (edit == null || string.IsNullOrWhiteSpace(edit.XPath)) throw new IOException("必须指定 XPath。");
                var expression = XPathExpression.Compile(edit.XPath);
                expression.SetContext(namespaces);
                bool valid = edit.Action switch
                {
                    "setValue" => edit.Value != null && edit.Name == null && edit.Xml == null,
                    "addAttribute" => edit.Value != null && edit.Name != null && edit.Xml == null,
                    "replaceElement" or "appendChild" or "insertBefore" or "insertAfter" => edit.Xml != null && edit.Value == null && edit.Name == null,
                    "remove" => edit.Xml == null && edit.Value == null && edit.Name == null,
                    _ => false
                };
                if (!valid) throw new IOException("XML 动作未知，或包含缺失／不适用的字段。");
                if (edit.Name != null) ResolveAttributeName(edit.Name, namespaces);
                if (edit.Xml != null) ParseFragment(edit.Xml);
                if (edit.Value != null) XmlConvert.VerifyXmlChars(edit.Value);
            }
        }
        catch (Exception ex) when (IsInputError(ex))
        {
            throw Error(operation, index, ex);
        }
    }

    public static byte[] Apply(byte[] bytes, MediaUpdateOperation operation, CancellationToken cancel = default,
        Action<int, MediaXmlEdit> skipped = null)
    {
        int index = 0;
        try
        {
            cancel.ThrowIfCancellationRequested();
            var (encoding, bomLength, original) = Decode(bytes, operation.Encoding ?? "auto");
            cancel.ThrowIfCancellationRequested();
            var document = Parse(original);
            cancel.ThrowIfCancellationRequested();
            var namespaces = CreateNamespaces(document.NameTable, operation);
            string newline = DetectNewline(original);
            bool finalNewline = original.EndsWith('\n') || original.EndsWith('\r');
            bool changed = false;
            foreach (var edit in operation.Edits)
            {
                cancel.ThrowIfCancellationRequested();
                index++;
                var matches = document.SelectNodes(edit.XPath, namespaces);
                if (matches == null || matches.Count != 1)
                    throw new IOException($"XPath 必须恰好匹配一个节点，实际匹配 {matches?.Count ?? 0} 个。");
                XmlNode target = matches[0]!;
                if (target is XmlAttribute namespaceAttribute && IsNamespaceDeclaration(namespaceAttribute))
                    throw new IOException("不能通过属性操作修改 XML 命名空间声明。");
                bool editChanged = false;
                switch (edit.Action)
                {
                    case "setValue":
                        editChanged = SetValue(document, target, edit.Value);
                        break;
                    case "addAttribute":
                    {
                        var element = RequireElement(target);
                        var (prefix, local, uri) = ResolveAttributeName(edit.Name, namespaces);
                        if (element.HasAttribute(local, uri))
                        {
                            if (element.GetAttribute(local, uri) == edit.Value) break;
                            throw new IOException("同名属性已存在且值不同，新增操作不会覆盖它。");
                        }
                        var addedAttribute = document.CreateAttribute(prefix, local, uri);
                        addedAttribute.Value = edit.Value;
                        element.Attributes.Append(addedAttribute);
                        editChanged = true;
                        break;
                    }
                    case "remove":
                        if (target is XmlAttribute attribute) attribute.OwnerElement!.RemoveAttributeNode(attribute);
                        else
                        {
                            var element = RequireElement(target);
                            var parent = RequireElement(element.ParentNode);
                            if (CanFormat(parent) && IsIndentation(element.PreviousSibling)) parent.RemoveChild(element.PreviousSibling!);
                            parent.RemoveChild(element);
                        }
                        editChanged = true;
                        break;
                    case "replaceElement":
                    case "appendChild":
                    case "insertBefore":
                    case "insertAfter":
                    {
                        var element = RequireElement(target);
                        var inserted = (XmlElement)document.ImportNode(ParseFragment(edit.Xml), true);
                        if (AlreadyPresent(element, inserted, edit.Action, cancel)) break;
                        Insert(element, inserted, edit.Action, newline);
                        editChanged = true;
                        break;
                    }
                    default: throw new IOException("未知 XML 编辑动作。");
                }
                if (editChanged) changed = true;
                else skipped?.Invoke(index, edit);
            }

            // An identical edit must not normalize formatting, touch timestamps, or require write access.
            cancel.ThrowIfCancellationRequested();
            if (!changed) return bytes;

            var settings = new XmlWriterSettings
            {
                Indent = false,
                OmitXmlDeclaration = document.FirstChild is not XmlDeclaration,
                NewLineHandling = NewLineHandling.Replace,
                NewLineChars = newline,
                CheckCharacters = true
            };
            cancel.ThrowIfCancellationRequested();
            using var output = new EncodingStringWriter(encoding);
            using (var writer = XmlWriter.Create(output, settings)) document.Save(writer);
            cancel.ThrowIfCancellationRequested();
            string serialized = output.ToString();
            if (finalNewline && !serialized.EndsWith('\n') && !serialized.EndsWith('\r')) serialized += newline;
            else if (!finalNewline) serialized = serialized.TrimEnd('\r', '\n');
            Parse(serialized);
            cancel.ThrowIfCancellationRequested();
            byte[] content = encoding.GetBytes(serialized);
            cancel.ThrowIfCancellationRequested();
            var result = new byte[bomLength + content.Length];
            bytes.AsSpan(0, bomLength).CopyTo(result);
            content.CopyTo(result, bomLength);
            return result;
        }
        catch (Exception ex) when (IsInputError(ex))
        {
            throw Error(operation, index, ex);
        }
    }

    private static bool IsInputError(Exception ex) => ex is IOException or XmlException or XPathException or ArgumentException or InvalidOperationException;
    private static IOException Error(MediaUpdateOperation operation, int index, Exception error)
    {
        string xpath = index > 0 && index <= operation.Edits.Count ? operation.Edits[index - 1]?.XPath ?? "未指定" : "未执行";
        string detail = $"编辑 {index}，XPath「{xpath}」：{error.Message}";
        return new MediaUpdateException(operation.Target, detail, error, $"无法修改 XML {operation.Target}：{detail}");
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreWhitespace = false,
        IgnoreComments = false,
        CheckCharacters = true
    };

    private static XmlDocument Parse(string text)
    {
        var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        using var input = new StringReader(text);
        using var reader = XmlReader.Create(input, ReaderSettings());
        document.Load(reader);
        if (document.DocumentElement == null) throw new IOException("XML 缺少根元素。");
        return document;
    }

    private static XmlElement ParseFragment(string text)
    {
        var fragment = Parse(text);
        if (fragment.ChildNodes.Cast<XmlNode>().Any(n => n != fragment.DocumentElement && n.NodeType != XmlNodeType.Whitespace))
            throw new IOException("插入片段只能包含一个根元素及外围空白，不能包含 XML 声明、DTD 或其他顶层节点。");
        return fragment.DocumentElement!;
    }

    private static XmlNamespaceManager CreateNamespaces(XmlNameTable table, MediaUpdateOperation operation)
    {
        var result = new XmlNamespaceManager(table);
        if (operation.Namespaces == null) return result;
        foreach (var pair in operation.Namespaces)
        {
            XmlConvert.VerifyNCName(pair.Key);
            if (string.IsNullOrWhiteSpace(pair.Value) || pair.Key == "xmlns" || pair.Value == XmlnsNamespace
                || (pair.Key == "xml") != (pair.Value == XmlNamespace)) throw new IOException("XML 命名空间映射无效。");
            result.AddNamespace(pair.Key, pair.Value);
        }
        return result;
    }

    private static (string Prefix, string Local, string Uri) ResolveAttributeName(string name, XmlNamespaceManager namespaces)
    {
        string[] parts = name.Split(':');
        if (parts.Length is < 1 or > 2) throw new IOException("属性名不是有效的 XML 限定名。");
        foreach (string part in parts) XmlConvert.VerifyNCName(part);
        string prefix = parts.Length == 2 ? parts[0] : "";
        string local = parts[^1];
        if (prefix == "xmlns" || (prefix.Length == 0 && local == "xmlns")) throw new IOException("不能新增命名空间声明属性。");
        string uri = prefix.Length == 0 ? "" : namespaces.LookupNamespace(prefix);
        if (uri == null || uri == XmlnsNamespace) throw new IOException("属性名前缀没有对应的命名空间映射。");
        return (prefix, local, uri);
    }

    private static XmlElement RequireElement(XmlNode node) => node as XmlElement ?? throw new IOException("此动作需要元素节点，不能操作文档、文本或其他节点。");
    private static bool IsNamespaceDeclaration(XmlAttribute attribute) => attribute.NamespaceURI == XmlnsNamespace || attribute.Name == "xmlns" || attribute.Prefix == "xmlns";

    private static bool SetValue(XmlDocument document, XmlNode target, string value)
    {
        if (target is XmlAttribute attribute)
        {
            if (attribute.Value == value) return false;
            attribute.Value = value;
            return true;
        }
        var element = RequireElement(target);
        if (element.ChildNodes.OfType<XmlElement>().Any()) throw new IOException("setValue 只能修改叶元素，不能清空子元素。");
        if (element.InnerText == value) return false;
        XmlNode[] textNodes = element.ChildNodes.Cast<XmlNode>().Where(n => n.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace).ToArray();
        var replacement = document.CreateTextNode(value);
        if (textNodes.Length == 0) element.PrependChild(replacement);
        else
        {
            element.ReplaceChild(replacement, textNodes[0]);
            foreach (var text in textNodes.Skip(1)) element.RemoveChild(text);
        }
        return true;
    }

    private static bool AlreadyPresent(XmlElement target, XmlElement inserted, string action, CancellationToken cancel)
    {
        if (action == "replaceElement") return Equivalent(target, inserted, cancel);
        // Search only direct children, or siblings on the requested side of the anchor.
        // This also handles multiple insertions after the same anchor in one edit list.
        if (action != "appendChild") RequireElement(target.ParentNode);
        for (XmlNode candidate = action == "appendChild" ? target.FirstChild
                 : action == "insertBefore" ? target.PreviousSibling : target.NextSibling;
             candidate != null;
             candidate = action == "insertBefore" ? candidate.PreviousSibling : candidate.NextSibling)
        {
            cancel.ThrowIfCancellationRequested();
            if (candidate is XmlElement element && Equivalent(element, inserted, cancel)) return true;
        }
        return false;
    }

    private static bool Equivalent(XmlElement left, XmlElement right, CancellationToken cancel)
    {
        var pending = new Stack<(XmlNode Left, XmlNode Right)>();
        pending.Push((left, right));
        while (pending.TryPop(out var pair))
        {
            cancel.ThrowIfCancellationRequested();
            if (pair.Left is XmlElement a && pair.Right is XmlElement b)
            {
                if (a.LocalName != b.LocalName || a.NamespaceURI != b.NamespaceURI) return false;
                var attributes = a.Attributes.Cast<XmlAttribute>().Where(attr => !IsNamespaceDeclaration(attr)).ToArray();
                if (attributes.Length != b.Attributes.Cast<XmlAttribute>().Count(attr => !IsNamespaceDeclaration(attr))) return false;
                foreach (var attribute in attributes)
                {
                    cancel.ThrowIfCancellationRequested();
                    var other = b.GetAttributeNode(attribute.LocalName, attribute.NamespaceURI);
                    if (other == null || other.Value != attribute.Value) return false;
                }
                // Formatting added by Insert is not content. Preserve whitespace in leaves,
                // mixed content and xml:space=preserve, and compare comments/PI in order.
                bool ignoreIndent = CanFormat(a) && CanFormat(b)
                    && a.ChildNodes.OfType<XmlElement>().Any() && b.ChildNodes.OfType<XmlElement>().Any();
                var ac = ComparisonChildren(a, ignoreIndent, cancel);
                var bc = ComparisonChildren(b, ignoreIndent, cancel);
                if (ac.Count != bc.Count) return false;
                for (int i = 0; i < ac.Count; i++) pending.Push((ac[i], bc[i]));
            }
            else if (pair.Left.NodeType != pair.Right.NodeType || pair.Left.Name != pair.Right.Name || pair.Left.Value != pair.Right.Value)
                return false;
        }
        return true;
    }

    private static List<XmlNode> ComparisonChildren(XmlElement parent, bool ignoreIndent, CancellationToken cancel)
    {
        var result = new List<XmlNode>();
        var text = new StringBuilder();
        foreach (XmlNode child in parent.ChildNodes)
        {
            cancel.ThrowIfCancellationRequested();
            if (ignoreIndent && IsIndentation(child)) continue;
            if (child.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                text.Append(child.Value);
            else
            {
                if (text.Length > 0) result.Add(parent.OwnerDocument!.CreateTextNode(text.ToString()));
                text.Clear();
                result.Add(child);
            }
        }
        if (text.Length > 0) result.Add(parent.OwnerDocument!.CreateTextNode(text.ToString()));
        return result;
    }

    private static bool CanFormat(XmlElement element)
    {
        for (XmlElement current = element; current != null; current = current.ParentNode as XmlElement)
        {
            string space = current.GetAttribute("space", XmlNamespace);
            if (space == "preserve") return false;
            if (space == "default") break;
        }
        return !element.ChildNodes.Cast<XmlNode>().Any(n => n.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace && !string.IsNullOrWhiteSpace(n.Value));
    }

    private static bool IsIndentation(XmlNode node) => node != null && node.NodeType is XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace or XmlNodeType.Text
        && string.IsNullOrWhiteSpace(node.Value);

    private static string IndentOf(XmlNode node)
    {
        if (node.ParentNode is XmlDocument) return "";
        if (!IsIndentation(node.PreviousSibling)) return null;
        string whitespace = node.PreviousSibling!.Value!;
        int last = Math.Max(whitespace.LastIndexOf('\n'), whitespace.LastIndexOf('\r'));
        return last < 0 ? null : whitespace[(last + 1)..];
    }

    private static string IndentStep(XmlElement parent)
    {
        for (XmlElement current = parent; current != null; current = current.ParentNode as XmlElement)
        {
            string outer = IndentOf(current) ?? "";
            foreach (var child in current.ChildNodes.OfType<XmlElement>())
            {
                string inner = IndentOf(child);
                if (inner != null && inner.Length > outer.Length && inner.StartsWith(outer, StringComparison.Ordinal)) return inner[outer.Length..];
            }
        }
        return "    ";
    }

    private static void FormatInserted(XmlElement element, string indent, string step, string newline)
    {
        if (!CanFormat(element) || !element.ChildNodes.OfType<XmlElement>().Any()) return;
        foreach (XmlNode whitespace in element.ChildNodes.Cast<XmlNode>().Where(IsIndentation).ToArray()) element.RemoveChild(whitespace);
        foreach (XmlNode child in element.ChildNodes.Cast<XmlNode>().ToArray())
        {
            element.InsertBefore(element.OwnerDocument!.CreateWhitespace(newline + indent + step), child);
            if (child is XmlElement nested) FormatInserted(nested, indent + step, step, newline);
        }
        element.AppendChild(element.OwnerDocument!.CreateWhitespace(newline + indent));
    }

    private static void Insert(XmlElement target, XmlElement inserted, string action, string newline)
    {
        var document = target.OwnerDocument!;
        if (action == "replaceElement")
        {
            string indent = IndentOf(target) ?? "";
            string step = IndentStep(target.ParentNode as XmlElement ?? target);
            target.ParentNode!.ReplaceChild(inserted, target);
            FormatInserted(inserted, indent, step, newline);
            return;
        }
        var parent = action == "appendChild" ? target : RequireElement(target.ParentNode);
        bool format = CanFormat(parent);
        string parentIndent = IndentOf(parent) ?? "";
        string childIndent = (action == "appendChild" ? parent.ChildNodes.OfType<XmlElement>().Select(IndentOf).FirstOrDefault(i => i != null) : IndentOf(target))
            ?? parentIndent + IndentStep(parent);
        if (action == "appendChild")
        {
            XmlNode closing = IsIndentation(parent.LastChild) ? parent.LastChild : null;
            if (format)
            {
                if (closing == null) closing = parent.AppendChild(document.CreateWhitespace(newline + parentIndent));
                parent.InsertBefore(document.CreateWhitespace(newline + childIndent), closing);
                parent.InsertBefore(inserted, closing);
                closing!.Value = newline + parentIndent;
            }
            else parent.AppendChild(inserted);
        }
        else if (action == "insertBefore")
        {
            bool hasIndent = IsIndentation(target.PreviousSibling);
            parent.InsertBefore(inserted, target);
            if (format)
            {
                if (!hasIndent) parent.InsertBefore(document.CreateWhitespace(newline + childIndent), inserted);
                parent.InsertBefore(document.CreateWhitespace(newline + childIndent), target);
            }
        }
        else
        {
            parent.InsertAfter(inserted, target);
            if (format)
            {
                parent.InsertBefore(document.CreateWhitespace(newline + childIndent), inserted);
                if (!IsIndentation(inserted.NextSibling))
                    parent.InsertAfter(document.CreateWhitespace(newline + (inserted.NextSibling == null ? parentIndent : childIndent)), inserted);
            }
        }
        if (format) FormatInserted(inserted, childIndent, IndentStep(parent), newline);
    }

    private static string DetectNewline(string text)
    {
        int index = text.IndexOfAny(['\r', '\n']);
        return index < 0 ? "\r\n" : text[index] == '\n' ? "\n" : index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
    }

    private static (Encoding Encoding, int BomLength, string Text) Decode(byte[] bytes, string requested)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            throw new IOException("不支持 UTF-32 XML。");
        int bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3
            : bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) || bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) ? 2 : 0;
        int physical = bom == 3 ? 65001 : bom == 2 ? (bytes[0] == 0xff ? 1200 : 1201)
            : bytes.Length >= 2 && bytes[0] == '<' && bytes[1] == 0 ? 1200
            : bytes.Length >= 2 && bytes[0] == 0 && bytes[1] == '<' ? 1201 : 0;
        int requestedCode = requested == "auto" ? 0 : ResolveEncoding(requested).CodePage;
        int prefixCode = physical != 0 ? physical : requestedCode is 1200 or 1201 ? requestedCode : 20127;
        // Only the declaration is decoded initially; its syntax is ASCII even for GBK and Shift-JIS.
        int count = Math.Min(bytes.Length - bom, 4096);
        if (prefixCode is 1200 or 1201) count -= count % 2;
        string prefix = Encoding.GetEncoding(prefixCode).GetString(bytes, bom, count);
        string declaration = null;
        if (prefix.StartsWith("<?xml", StringComparison.Ordinal) && prefix.Length > 5 && char.IsWhiteSpace(prefix[5]))
        {
            int end = prefix.IndexOf("?>", StringComparison.Ordinal);
            if (end < 0) throw new IOException("XML 声明缺失结束标记或过长。");
            using var header = XmlReader.Create(new StringReader(prefix[..(end + 2)] + "<probe/>"), ReaderSettings());
            header.Read();
            declaration = header.GetAttribute("encoding");
        }
        int declaredCode = declaration == null ? 0 : ResolveEncoding(declaration).CodePage;
        bool genericUtf16 = string.Equals(declaration, "utf-16", StringComparison.OrdinalIgnoreCase);
        int code = requestedCode != 0 ? requestedCode : physical != 0 ? physical : declaredCode != 0 ? declaredCode : 65001;
        if ((physical != 0 && physical != code) || (declaredCode != 0 && declaredCode != code && !(genericUtf16 && code is 1200 or 1201)))
            throw new IOException("XML 编码声明、BOM／字节序与指定编码不一致。");
        Encoding encoding = Encoding.GetEncoding(code, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        return (encoding, bom, encoding.GetString(bytes, bom, bytes.Length - bom));
    }

    private static Encoding ResolveEncoding(string name)
    {
        if (name.Equals("utf-16le", StringComparison.OrdinalIgnoreCase)) name = "utf-16";
        if (name.Equals("utf-16be", StringComparison.OrdinalIgnoreCase)) name = "unicodeFFFE";
        if (name.Equals("shift-jis", StringComparison.OrdinalIgnoreCase)) name = "shift_jis";
        Encoding encoding = Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        if (encoding.CodePage is not (65001 or 1200 or 1201 or 936 or 932)) throw new IOException("不支持此 XML 编码：" + name);
        return encoding;
    }

    private sealed class EncodingStringWriter(Encoding encoding) : StringWriter(System.Globalization.CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => encoding;
    }
}
