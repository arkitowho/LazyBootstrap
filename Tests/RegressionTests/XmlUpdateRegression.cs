using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using LazyBootstrap.MediaUpdate;

internal static partial class UpdateRegression
{
    private static void RunXmlTests()
    {
        Test("大量 XML 编辑可取消且取消不会包装成 XML 错误", _ =>
        {
            byte[] bytes = Encoding.UTF8.GetBytes("<config><value>old</value></config>");
            var edits = Enumerable.Repeat(Value("/config/value", "new"), 1_000_000).ToArray();
            var operation = Edit("contents/config.xml", edits);
            using var cancel = new CancellationTokenSource();
            var clock = Stopwatch.StartNew();
            cancel.CancelAfter(30);
            try { MediaXmlEditor.Apply(bytes, operation, cancel.Token); throw new Exception("XML 编辑忽略了取消"); }
            catch (OperationCanceledException ex) { Check(ex.CancellationToken == cancel.Token, "取消令牌丢失"); }
            Check(clock.Elapsed < TimeSpan.FromSeconds(3), "取消后仍继续处理 XML 编辑列表");
            Check(Encoding.UTF8.GetString(bytes) == "<config><value>old</value></config>", "XML 预演修改了输入");
            try { MediaXmlEditor.Validate(operation, cancel.Token); throw new Exception("XML 校验忽略了取消"); }
            catch (OperationCanceledException) { }
        });

        Test("XML 属性筛选、文本与属性修改", _ =>
        {
            string xml = TransformXml("<config><option name='a' value='0'/><label><!--keep-->旧值<?keep yes?></label></config>",
                Value("/config/option[@name='a']/@value", "true & <quoted>\""), Value("/config/label", "新值"));
            var root = XDocument.Parse(xml).Root!;
            Check(root.Element("option")!.Attribute("value")!.Value == "true & <quoted>\"", "属性值转义错误");
            Check(root.Element("label")!.Value == "新值" && xml.Contains("<!--keep-->") && xml.Contains("<?keep yes?>"), "叶元素注释或处理指令丢失");
        });
        Test("XML 新增属性与命名空间限定名", _ =>
        {
            var op = Edit("contents/config.xml", new MediaXmlEdit { Action = "addAttribute", XPath = "/config", Name = "enabled", Value = "true" },
                new MediaXmlEdit { Action = "addAttribute", XPath = "/config", Name = "p:mode", Value = "new" });
            op.Namespaces = new() { ["p"] = "urn:parameter" };
            var root = XDocument.Parse(TransformXml("<config/>", op)).Root!;
            Check(root.Attribute("enabled")!.Value == "true" && root.Attribute(XName.Get("mode", "urn:parameter"))!.Value == "new", "新增属性错误");
        });
        Test("XML 三种插入位置与顺序依赖", _ =>
        {
            string xml = TransformXml("<config>\n  <option name='anchor'/>\n</config>\n",
                Fragment("insertBefore", "/config/option", "<option name='before'/>") ,
                Fragment("insertAfter", "/config/option[@name='anchor']", "<option name='after'/>") ,
                Fragment("appendChild", "/config", "<group><child value='1'/></group>"),
                Value("/config/group/child/@value", "2"));
            var root = XDocument.Parse(xml).Root!;
            Check(root.Elements().Select(e => (string)e.Attribute("name") ?? e.Name.LocalName).SequenceEqual(new[] { "before", "anchor", "after", "group" }), "插入位置错误");
            Check(xml.Contains("\n  <group>\n    <child value=\"2\""), "未沿用相邻元素缩进");
            Check(xml.EndsWith('\n') && !xml.Contains('\r'), "换行风格改变");
        });
        Test("XML 元素替换及元素与属性删除", _ =>
        {
            string xml = TransformXml("<config keep='yes' obsolete='x'><old/><remove/></config>",
                Fragment("replaceElement", "/config/old", "<new><nested/></new>"),
                new MediaXmlEdit { Action = "remove", XPath = "/config/@obsolete" },
                new MediaXmlEdit { Action = "remove", XPath = "/config/remove" });
            var root = XDocument.Parse(xml).Root!;
            Check(root.Attribute("keep")!.Value == "yes" && root.Attribute("obsolete") == null && root.Elements().Single().Name == "new", "替换或删除错误");
            Check(XDocument.Parse(TransformXml("<old/>", Fragment("replaceElement", "/old", "<new/>"))).Root!.Name == "new", "根元素替换失败");
        });
        Test("XML 默认命名空间与显式 XPath 前缀", _ =>
        {
            var op = Edit("contents/config.xml", Value("/c:config/c:option/@value", "new"),
                Fragment("appendChild", "/c:config", "<option xmlns='urn:config' value='added'/>") );
            op.Namespaces = new() { ["c"] = "urn:config" };
            var root = XDocument.Parse(TransformXml("<config xmlns='urn:config'><option value='old'/></config>", op)).Root!;
            Check(root.Elements(XName.Get("option", "urn:config")).Select(e => (string)e.Attribute("value")).SequenceEqual(new[] { "new", "added" }), "命名空间定位错误");
            Reject(() => TransformXml("<config xmlns='urn:config'/>", Value("/config", "x")));
            Reject(() => TransformXml("<config/>", Value("/unknown:config", "x")));
        });
        Test("XML 定位错误、零匹配、多匹配与节点类型拒绝", _ =>
        {
            foreach (string xpath in new[] { "/missing", "/config/item", "/config/item[", "count(/config/item)", "/config/item[1]/text()", "/" })
                Reject(() => TransformXml("<config><item>x</item><item>y</item></config>", Value(xpath, "new")));
            Reject(() => TransformXml("<config><item/></config>", Value("/config", "new")));
            Reject(() => TransformXml("<config value='x'/>", Fragment("appendChild", "/config/@value", "<item/>")));
        });
        Test("XML 修改与新增严格区分", _ =>
        {
            Reject(() => TransformXml("<config/>", Value("/config/@missing", "new")));
            Reject(() => TransformXml("<config existing='old'/>", new MediaXmlEdit { Action = "addAttribute", XPath = "/config", Name = "existing", Value = "new" }));
            Reject(() => TransformXml("<config/>", Fragment("appendChild", "/config/missing", "<item/>")));
        });
        Test("XML 禁止删除根节点、插入第二个根及编辑 xmlns", _ =>
        {
            Reject(() => TransformXml("<config/>", new MediaXmlEdit { Action = "remove", XPath = "/config" }));
            foreach (string action in new[] { "insertBefore", "insertAfter" }) Reject(() => TransformXml("<config/>", Fragment(action, "/config", "<other/>")));
            foreach (string name in new[] { "xmlns", "xmlns:p", "p:unknown", "bad:name:parts", "a b" })
                Reject(() => TransformXml("<config/>", new MediaXmlEdit { Action = "addAttribute", XPath = "/config", Name = name, Value = "urn:new" }));
            foreach (string action in new[] { "setValue", "remove" })
                Reject(() => TransformXml("<config xmlns:p='urn:old'/>", new MediaXmlEdit { Action = action, XPath = "/config/namespace::p", Value = action == "setValue" ? "urn:new" : null }));
        });
        Test("XML 片段必须自包含且只有一个根元素", _ =>
        {
            foreach (string fragment in new[] { "", "<one/><two/>", "text", "<p:item/>", "<?xml version='1.0'?><one/>", "<one>", "<!--outside--><one/>" })
                Reject(() => TransformXml("<config/>", Fragment("appendChild", "/config", fragment)));
        });
        Test("XML DTD 与外部实体在文件和片段中均被拒绝", root =>
        {
            Put(root, "external.txt", "sentinel");
            string external = new Uri(Path.Combine(root, "external.txt")).AbsoluteUri;
            foreach (string xml in new[] { "<!DOCTYPE config><config/>", $"<!DOCTYPE config [<!ENTITY ext SYSTEM '{external}'>]><config>&ext;</config>",
                         "<!DOCTYPE config SYSTEM 'https://invalid.example/test.dtd'><config/>" })
            {
                Reject(() => TransformXml(xml, Value("/config", "new")));
                Reject(() => TransformXml("<config/>", Fragment("appendChild", "/config", xml)));
            }
            Equal(root, "external.txt", "sentinel");
        });
        Test("XML 混合内容与 xml:space 不被自动缩进破坏", _ =>
        {
            string xml = TransformXml("<config xml:space='preserve'> a <item/> b </config>", Fragment("insertAfter", "/config/item", "<new/>") );
            Check(xml.Contains(" a <item") && xml.Contains(" b ") && !xml.Contains('\n'), "xml:space 内容改变");
            xml = TransformXml("<config>left<item/>right</config>", Fragment("appendChild", "/config", "<new/>") );
            Check(XDocument.Parse(xml).Root!.Value == "leftright" && !xml.Contains('\n'), "混合内容改变");
        });
        Test("XML 编码声明、BOM、中日文与末尾换行", _ =>
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var cases = new (string Requested, Encoding Encoding, string Declaration)[]
            {
                ("auto", new UTF8Encoding(false, true), "utf-8"), ("auto", new UTF8Encoding(true, true), "utf-8"),
                ("auto", new UnicodeEncoding(false, true, true), "utf-16"), ("auto", new UnicodeEncoding(true, true, true), "utf-16"),
                ("utf-16le", new UnicodeEncoding(false, false, true), "utf-16"), ("utf-16be", new UnicodeEncoding(true, false, true), "utf-16BE"),
                ("auto", Encoding.GetEncoding(936), "gbk"), ("auto", Encoding.GetEncoding(932), "shift_jis"),
                ("gbk", Encoding.GetEncoding(936), null), ("shift-jis", Encoding.GetEncoding(932), null)
            };
            foreach (var item in cases)
            foreach (string ending in new[] { "", "\r\n" })
            {
                string xml = (item.Declaration == null ? "" : $"<?xml version='1.0' encoding='{item.Declaration}' standalone='yes'?>\r\n") + "<config>日本</config>" + ending;
                var op = Edit("contents/config.xml", Value("/config", "中文")); op.Encoding = item.Requested;
                byte[] original = item.Encoding.GetPreamble().Concat(item.Encoding.GetBytes(xml)).ToArray();
                MediaXmlEditor.Validate(op);
                byte[] result = MediaXmlEditor.Apply(original, op);
                int bomLength = item.Encoding.GetPreamble().Length;
                Check(result.AsSpan(0, bomLength).SequenceEqual(item.Encoding.GetPreamble()), "BOM 改变");
                string decoded = item.Encoding.GetString(result, bomLength, result.Length - bomLength);
                var document = XDocument.Parse(decoded);
                Check(document.Root!.Value == "中文", "编码内容损坏");
                Check((document.Declaration != null) == (item.Declaration != null), "XML 声明存在状态改变");
                if (item.Declaration != null) Check(document.Declaration!.Standalone == "yes", "standalone 声明丢失");
                Check(decoded.EndsWith("\r\n", StringComparison.Ordinal) == (ending.Length > 0), "末尾换行改变");
            }
        });
        Test("XML 编码冲突、非法字节与不可编码字符拒绝", _ =>
        {
            var op = Edit("contents/config.xml", Value("/config", "new"));
            Reject(() => MediaXmlEditor.Apply([0xff, 0xff], op));
            Reject(() => MediaXmlEditor.Apply(new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("<?xml version='1.0' encoding='gbk'?><config/>")).ToArray(), op));
            Reject(() => TransformXml("<?xml version='1.0' encoding='iso-8859-1'?><config/>", op));
            op.Encoding = "gbk"; Reject(() => MediaXmlEditor.Apply(Encoding.UTF8.GetBytes("<?xml version='1.0' encoding='utf-8'?><config/>"), op));
            op = Edit("contents/config.xml", Value("/config", "😀")); op.Encoding = "gbk";
            Reject(() => MediaXmlEditor.Apply(Encoding.ASCII.GetBytes("<config/>"), op));
        });
        Test("XML 新操作字段严格校验且拒绝旧行编辑", root =>
        {
            var (game, staging) = Pack(root, Edit("contents/config.xml", Value("/config", "new"))); Put(game, "contents/config.xml", "<config/>");
            string manifest = File.ReadAllText(Path.Combine(staging, "update"));
            foreach (string invalid in new[] { manifest.Replace("editXml", "editText"), manifest.Replace("\"xpath\"", "\"line\""),
                         manifest.Replace("\"setValue\"", "\"unknown\""), manifest.Replace("\"xpath\":", "\"match\": \"old\", \"xpath\":"),
                         manifest.Replace("\"setValue\"", "\"remove\"") })
            { Put(staging, "update", invalid); Seal(staging); Reject(() => Apply(game, staging)); }
            var op = Edit("contents/config.xml", Value("/config", "x")); op.Namespaces = new() { [""] = "urn:bad" }; Reject(() => MediaXmlEditor.Validate(op));
            op.Namespaces = new() { ["xml"] = "urn:bad" }; Reject(() => MediaXmlEditor.Validate(op));
            op = Copy("source/a", "contents/a"); op.Namespaces = new() { ["p"] = "urn:p" }; Manifest(staging, op); Seal(staging); Reject(() => Apply(game, staging));
        });
        Test("XML 文件缺失、非法 XML 与失败预演不修改游戏", root =>
        {
            var (game, staging) = Pack(root, Copy("source/a", "contents/a"), Edit("contents/config.xml", Value("/missing", "new")));
            Put(staging, "source/a", "new");
            Seal(staging); Reject(() => Apply(game, staging)); Check(!File.Exists(Path.Combine(game, "contents/config.xml")), "自动创建了 XML 文件");
            Put(game, "contents/config.xml", "<config/>"); Seal(staging); Reject(() => Apply(game, staging));
            Check(!File.Exists(Path.Combine(game, "contents/a")), "预演失败后仍写入"); Equal(game, "contents/config.xml", "<config/>");
            Put(game, "contents/config.xml", "<config>"); Seal(staging); Reject(() => Apply(game, staging));
            Put(game, "launcher/config.toml", "enabled = false"); Manifest(staging, Edit("launcher/config.toml", Value("/config", "new"))); Seal(staging); Reject(() => Apply(game, staging));
        });
        Test("XML 错误包含文件、编辑序号与 XPath", _ =>
        {
            try { TransformXml("<config/>", Value("/config", "first"), Value("/missing", "new")); }
            catch (IOException ex)
            {
                Check(ex.Message.Contains("contents/config.xml") && ex.Message.Contains("编辑 2") && ex.Message.Contains("/missing"), "XML 错误缺少定位信息");
                return;
            }
            throw new Exception("无效 XPath 未失败");
        });
    }

    private static MediaXmlEdit Value(string xpath, string value) => new() { Action = "setValue", XPath = xpath, Value = value };
    private static MediaXmlEdit Fragment(string action, string xpath, string xml) => new() { Action = action, XPath = xpath, Xml = xml };
    private static string TransformXml(string xml, params MediaXmlEdit[] edits) => TransformXml(xml, Edit("contents/config.xml", edits));
    private static string TransformXml(string xml, MediaUpdateOperation operation)
    {
        MediaXmlEditor.Validate(operation);
        return Encoding.UTF8.GetString(MediaXmlEditor.Apply(Encoding.UTF8.GetBytes(xml), operation));
    }
}
