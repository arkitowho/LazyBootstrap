using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using LazyBootstrap.Serialization;

internal static partial class UpdateRegression
{
    private static void RunSpiceDllTests()
    {
        var cases = new (string Original, bool Enabled, string Expected)[]
        {
            (null, true, "near_link.dll"),
            ("", true, "near_link.dll"),
            (" \t ", true, "near_link.dll"),
            ("ifs_hook.dll", true, "ifs_hook.dll near_link.dll"),
            ("ifs_hook.dll test.dll  ", true, "ifs_hook.dll test.dll near_link.dll"),
            ("near_link.dll", true, "near_link.dll"),
            ("  NEAR_LINK.DLL test.dll ", true, "NEAR_LINK.DLL test.dll"),
            ("ifs_hook.dll   test.dll", true, "ifs_hook.dll test.dll near_link.dll"),
            ("  ifs_hook.dll\t  test.dll\r\n final.dll  ", true, "ifs_hook.dll test.dll final.dll near_link.dll"),
            ("ifs_hook.dll   near_link.dll  test.dll", true, "ifs_hook.dll near_link.dll test.dll"),
            ("near_link.dll  NEAR_LINK.DLL", true, "near_link.dll NEAR_LINK.DLL"),
            ("near_link.dll near_link.dll", true, "near_link.dll near_link.dll"),
            (null, false, ""),
            ("", false, ""),
            ("ifs_hook.dll   test.dll", false, "ifs_hook.dll   test.dll"),
            ("near_link.dll", false, ""),
            (" \tnear_link.dll  NEAR_LINK.DLL\t", false, ""),
            ("near_link.dll ifs_hook.dll", false, "ifs_hook.dll"),
            ("ifs_hook.dll near_link.dll", false, "ifs_hook.dll"),
            ("ifs_hook.dll near_link.dll test.dll", false, "ifs_hook.dll test.dll"),
            ("near_link.dll ifs_hook.dll NEAR_LINK.DLL test.dll near_link.dll", false, "ifs_hook.dll test.dll"),
            ("  ifs_hook.dll\ttest.dll  near_link.dll \t final.dll  ", false, "  ifs_hook.dll\ttest.dll final.dll  "),
            ("other_near_link.dll near_link.dll.bak", false, "other_near_link.dll near_link.dll.bak"),
            ("other_near_link.dll", true, "other_near_link.dll near_link.dll"),
            ("near_link.dll.bak NEAR_LINK.DLL other_near_link.dll", false, "near_link.dll.bak other_near_link.dll")
        };
        foreach (var item in cases)
        {
            Test($"DLL 注入列表：{item.Original ?? "缺失字段"}，启用={item.Enabled}", root =>
            {
                string path = WriteSpiceDllConfig(root, item.Original);
                var before = XDocument.Load(path);
                var editor = new SpiceXmlConfigEditor();
                Check(editor.TrySetDllInjectionEnabled(path, "near_link.dll", item.Enabled, out var value, out var error), error);
                Check(value == item.Expected, $"返回列表错误：{value}");
                var after = XDocument.Load(path);
                var options = after.Root.Element("game").Element("options");
                var dllOption = options.Elements("option").SingleOrDefault(x => (string)x.Attribute("name") == "k");
                Check((dllOption?.Attribute("value")?.Value ?? "") == item.Expected, "保存列表错误");
                Check((dllOption != null) == (item.Original != null || item.Enabled), "未正确保留或创建字段");
                Check(SpiceXmlConfigEditor.ContainsInjectedDll(value, "near_link.dll") == item.Enabled, "开关状态与保存列表不一致");
                before.Root.Element("game").Element("options").Elements("option")
                    .Where(x => (string)x.Attribute("name") == "k").Remove();
                dllOption?.Remove();
                Check(XNode.DeepEquals(before, after), "更改了其他配置或游戏条目");
                byte[] saved = File.ReadAllBytes(path);
                Check(editor.TrySetDllInjectionEnabled(path, "near_link.dll", item.Enabled, out _, out error), error);
                Check(File.ReadAllBytes(path).SequenceEqual(saved), "重复操作不应改写文件");
            });
        }

        Test("DLL 注入每次操作使用最新文件内容", root =>
        {
            string path = WriteSpiceDllConfig(root, "ifs_hook.dll");
            var editor = new SpiceXmlConfigEditor();
            Check(editor.TrySetDllInjectionEnabled(path, "near_link.dll", true, out _, out var error), error);
            WriteSpiceDllConfig(root, "external.dll near_link.dll test.dll");
            Check(editor.TrySetDllInjectionEnabled(path, "near_link.dll", false, out var value, out error), error);
            Check(value == "external.dll test.dll", "覆盖了外部修改");
        });

        Test("DLL 注入支持缺失 options 节点且关闭不创建节点", root =>
        {
            string path = Path.Combine(root, "spicetools.xml");
            const string original = "<games><game name=\"Sound Voltex\" /></games>";
            File.WriteAllText(path, original);
            var editor = new SpiceXmlConfigEditor();
            Check(editor.TrySetDllInjectionEnabled(path, "near_link.dll", false, out _, out var error), error);
            Check(File.ReadAllText(path) == original, "关闭时创建了节点");
            Check(editor.TrySetDllInjectionEnabled(path, "near_link.dll", true, out _, out error), error);
            Check(XDocument.Load(path).Root.Element("game").Element("options").Element("option")
                .Attribute("value").Value == "near_link.dll", "未创建注入配置");
        });

        Test("DLL 注入保存失败保留原始配置和已读取的值", root =>
        {
            string path = WriteSpiceDllConfig(root, "ifs_hook.dll");
            byte[] original = File.ReadAllBytes(path);
            var editor = new SpiceXmlConfigEditor();
            using (var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check(!editor.TrySetDllInjectionEnabled(path, "near_link.dll", true, out var value, out var error), "锁定文件仍报告成功");
                Check(value == "ifs_hook.dll" && !string.IsNullOrWhiteSpace(error), "失败后返回了未保存的状态");
            }
            Check(File.ReadAllBytes(path).SequenceEqual(original), "保存失败改变了配置");
        });

        foreach (string contents in new[] { "<broken", "<games><game name=\"Other\" /></games>" })
        Test("DLL 注入拒绝无效配置：" + contents, root =>
        {
            string path = Path.Combine(root, "spicetools.xml");
            File.WriteAllText(path, contents);
            var editor = new SpiceXmlConfigEditor();
            Check(!editor.TrySetDllInjectionEnabled(path, "near_link.dll", true, out _, out var error), "无效配置被接受");
            Check(!string.IsNullOrWhiteSpace(error) && File.ReadAllText(path) == contents, "未报告错误或改变原始内容");
            File.Delete(path);
            Check(!editor.TrySetDllInjectionEnabled(path, "near_link.dll", true, out _, out error), "缺失配置被接受");
            Check(!File.Exists(path), "创建了缺失的配置文件");
        });
    }

    private static string WriteSpiceDllConfig(string root, string value)
    {
        string path = Path.Combine(root, "spicetools.xml");
        new XDocument(new XElement("games",
            new XElement("game", new XAttribute("name", "Sound Voltex"),
                new XComment("保留用户配置"),
                new XElement("options",
                    new XElement("option", new XAttribute("name", "url"), new XAttribute("value", "http://localhost")),
                    new XElement("option", new XAttribute("name", "custom"), new XAttribute("value", "a\tb\r\nc")),
                    value == null ? null : new XElement("option", new XAttribute("name", "k"), new XAttribute("value", value)))),
            new XElement("game", new XAttribute("name", "Other"),
                new XElement("options", new XElement("option", new XAttribute("name", "k"), new XAttribute("value", "other.dll"))))))
            .Save(path);
        return path;
    }
}
