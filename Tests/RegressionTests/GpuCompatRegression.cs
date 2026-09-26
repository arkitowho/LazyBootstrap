using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using LazyBootstrap.FileSystem;
using LazyBootstrap.Platform;
using LazyBootstrap.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

internal static partial class UpdateRegression
{
    private const string ShaderDll = "sdvx_shader_fix_64bit.dll";
    private static readonly string[] GpuBaseFiles = { "nvcuda.dll", "nvcuvid.dll", "nvEncodeAPI64.dll" };

    private static void RunGpuCompatTests()
    {
        Test("兼容层默认模式及旧配置迁移", root =>
        {
            var (_, paths, _) = CreateGpuFixture(root);
            var config = new AppConfigStore(paths.ConfigFilePath, null);
            Check(config.ReadString("Setting", "cl-rendermode") == "shaderfix", "新配置默认模式错误");
            Check(!config.ReadBool("Setting", "compatlayer", false), "新配置意外启用兼容层");
            foreach (string mode in new[] { null, "", "unknown", "dx9on12_external", "SHADERFIX" })
                Check(GpuCompatLayerConfigurator.NormalizeRenderMode(mode) == "shaderfix", "未迁移旧模式或默认值");
            Check(GpuCompatLayerConfigurator.NormalizeRenderMode("DX9ON12") == "dx9on12", "未保留内置模式");
            Check(GpuCompatLayerConfigurator.NormalizeRenderMode("DXVK") == "dxvk", "未保留 dxvk");
        });

        var injections = new (string Original, string Expected)[]
        {
            (null, ShaderDll),
            ("", ShaderDll),
            (" \t ", ShaderDll),
            ("  near_link.dll\t ifs_hook.dll\r\n test.dll  ", "near_link.dll ifs_hook.dll test.dll " + ShaderDll),
            ("  SDVX_SHADER_FIX_64BIT.DLL   near_link.dll  ", "SDVX_SHADER_FIX_64BIT.DLL near_link.dll"),
            ("other_sdvx_shader_fix_64bit.dll sdvx_shader_fix_64bit.dll.bak", "other_sdvx_shader_fix_64bit.dll sdvx_shader_fix_64bit.dll.bak " + ShaderDll),
            (ShaderDll + "  " + ShaderDll, ShaderDll + " " + ShaderDll)
        };
        foreach (var item in injections)
        {
            Test($"ShaderFix 启用并规范完整注入列表：{item.Original ?? "缺失"}", root =>
            {
                var (gpu, paths, xml) = CreateGpuFixture(root, item.Original);
                var before = XDocument.Load(xml);
                Check(gpu.TryToggleGpuCompatLayer(true, "shaderfix", xml, out var error), error);
                Check(ReadGpuOption(xml, "k") == item.Expected, "注入列表错误");
                Check(ReadGpuOption(xml, "sp2x-dx9on12") == "0", "未禁用内置 dx9on12");
                foreach (string file in GpuBaseFiles)
                    Equal(paths.GetContentsDirectoryPath(), "modules/" + file, "bundled-" + file);
                Check(!File.Exists(Path.Combine(paths.GetContentsDirectoryPath(), "modules/d3d9.dll")), "ShaderFix 遗留 d3d9");
                Check(!File.Exists(Path.Combine(paths.GetContentsDirectoryPath(), "modules", ShaderDll)), "不应复制 ShaderFix DLL");
                var state = GpuCompatLayerConfigurator.DetectRuntimeState(paths.GetContentsDirectoryPath(), paths.GetBundledLibsDirectoryPath(), ReadGpuOption(xml, "k"));
                Check(state.IsFullyApplied && state.DetectedRenderMode == "shaderfix", "重启检测误判 ShaderFix");
                Check(GpuCompatLayerConfigurator.DetectRuntimeState(paths.GetContentsDirectoryPath(), paths.GetBundledLibsDirectoryPath()).IsFullyApplied,
                    "环境扫描无法识别 NVIDIA 兼容文件");
                var after = XDocument.Load(xml);
                foreach (var doc in new[] { before, after })
                    doc.Root.Element("game").Element("options").Elements("option")
                        .Where(x => (string)x.Attribute("name") is "k" or "sp2x-dx9on12").Remove();
                Check(XNode.DeepEquals(before, after), "改动了无关选项或其他游戏");
                byte[] saved = File.ReadAllBytes(xml);
                Check(gpu.TryToggleGpuCompatLayer(true, "shaderfix", xml, out error), error);
                Check(File.ReadAllBytes(xml).SequenceEqual(saved), "重复启用更改了注入内容");
            });
        }

        foreach (string mode in new[] { "dx9on12", "dxvk" })
        {
            Test($"ShaderFix 与 {mode} 切换并关闭", root =>
            {
                var (gpu, paths, xml) = CreateGpuFixture(root, "near_link.dll other.dll");
                Check(gpu.TryToggleGpuCompatLayer(true, "shaderfix", xml, out var error), error);
                Check(gpu.TryPersistGpuCompatLayerRenderMode(mode, true, xml, out error), error);
                Check(ReadGpuOption(xml, "k") == "near_link.dll other.dll", "切换未清理 ShaderFix 或误删其他注入");
                Check(ReadGpuOption(xml, "sp2x-dx9on12") == (mode == "dx9on12" ? "1" : "0"), "转译选项错误");
                string d3d9 = Path.Combine(paths.GetContentsDirectoryPath(), "modules/d3d9.dll");
                Check(File.Exists(d3d9) == (mode == "dxvk"), "转译文件错误");
                var state = GpuCompatLayerConfigurator.DetectRuntimeState(paths.GetContentsDirectoryPath(), paths.GetBundledLibsDirectoryPath(), ReadGpuOption(xml, "k"));
                Check(state.IsFullyApplied && state.DetectedRenderMode == mode, "已有有效模式被覆盖");
                Check(gpu.TryPersistGpuCompatLayerRenderMode("shaderfix", true, xml, out error), error);
                Check(ReadGpuOption(xml, "k") == "near_link.dll other.dll " + ShaderDll && !File.Exists(d3d9), "切回 ShaderFix 错误");
                Check(gpu.TryToggleGpuCompatLayer(false, "shaderfix", xml, out error), error);
                Check(ReadGpuOption(xml, "k") == "near_link.dll other.dll", "关闭未清理注入");
                Check(ReadGpuOption(xml, "sp2x-dx9on12") == "", "关闭未清理转译选项");
                Check(!Directory.EnumerateFiles(Path.Combine(paths.GetContentsDirectoryPath(), "modules")).Any(), "关闭后遗留兼容文件");
            });
        }

        Test("旧外置文件只读检测及首次启用迁移", root =>
        {
            var (gpu, paths, xml) = CreateGpuFixture(root, "near_link.dll");
            foreach (string file in GpuBaseFiles) Put(paths.GetContentsDirectoryPath(), "modules/" + file, "old-" + file);
            Put(paths.GetContentsDirectoryPath(), "modules/d3d9.dll", "old-external");
            string config = File.ReadAllText(paths.ConfigFilePath);
            string originalXml = File.ReadAllText(xml);
            var state = GpuCompatLayerConfigurator.DetectRuntimeState(paths.GetContentsDirectoryPath(), paths.GetBundledLibsDirectoryPath(), "near_link.dll");
            Check(!state.IsFullyApplied && state.HasInconsistentFiles && state.DetectedRenderMode == "", "外置模式仍被视为完整应用");
            Check(File.ReadAllText(paths.ConfigFilePath) == config && File.ReadAllText(xml) == originalXml, "检测写入了配置");
            Equal(paths.GetContentsDirectoryPath(), "modules/d3d9.dll", "old-external");
            Check(gpu.TryToggleGpuCompatLayer(true, "dx9on12_external", xml, out var error), error);
            Check(ReadGpuOption(xml, "k") == "near_link.dll " + ShaderDll, "旧模式未应用 ShaderFix");
            Check(new AppConfigStore(paths.ConfigFilePath, null).ReadString("Setting", "cl-rendermode") == "shaderfix", "迁移模式未持久化");
        });

        Test("切换配置后按当前注入检测模式，缺少文件不视为启用", root =>
        {
            var (gpu, paths, xml) = CreateGpuFixture(root);
            Check(gpu.TryToggleGpuCompatLayer(true, "shaderfix", xml, out var error), error);
            foreach (var injection in new[] { ShaderDll, "near_link.dll", ShaderDll.ToUpperInvariant() })
            {
                var state = GpuCompatLayerConfigurator.DetectRuntimeState(paths.GetContentsDirectoryPath(), paths.GetBundledLibsDirectoryPath(), injection);
                Check(state.DetectedRenderMode == (injection == "near_link.dll" ? "dx9on12" : "shaderfix"), "检测缓存了旧注入配置");
            }
            File.Delete(Path.Combine(paths.GetContentsDirectoryPath(), "modules/nvcuda.dll"));
            var partial = GpuCompatLayerConfigurator.DetectRuntimeState(paths.GetContentsDirectoryPath(), paths.GetBundledLibsDirectoryPath(), ShaderDll);
            Check(!partial.IsFullyApplied && partial.HasInconsistentFiles, "仅有注入配置不应视为完整启用");
        });

        Test("关闭时选择模式仅保存偏好", root =>
        {
            var (gpu, paths, xml) = CreateGpuFixture(root, "near_link.dll");
            byte[] original = File.ReadAllBytes(xml);
            Check(gpu.TryPersistGpuCompatLayerRenderMode("dxvk", false, xml, out var error), error);
            Check(File.ReadAllBytes(xml).SequenceEqual(original), "关闭时修改了 XML");
            Check(!Directory.EnumerateFiles(Path.Combine(paths.GetContentsDirectoryPath(), "modules")).Any(), "关闭时复制了文件");
            Check(new AppConfigStore(paths.ConfigFilePath, null).ReadString("Setting", "cl-rendermode") == "dxvk", "未保存偏好");
        });

        foreach (string operation in new[] { "enable", "switch", "disable" })
        {
            Test($"XML 写入失败完整回滚兼容层：{operation}", root =>
            {
                var (gpu, paths, xml) = CreateGpuFixture(root, "near_link.dll");
                if (operation != "enable")
                    Check(gpu.TryToggleGpuCompatLayer(true, "shaderfix", xml, out var setupError), setupError);
                else
                    Put(paths.GetContentsDirectoryPath(), "modules/d3d9.dll", "old-external");
                string modules = Path.Combine(paths.GetContentsDirectoryPath(), "modules");
                var files = Directory.GetFiles(modules).ToDictionary(Path.GetFileName, File.ReadAllBytes);
                byte[] config = File.ReadAllBytes(paths.ConfigFilePath);
                byte[] spice = File.ReadAllBytes(xml);
                using (File.Open(xml, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bool success = operation == "switch"
                        ? gpu.TryPersistGpuCompatLayerRenderMode("dxvk", true, xml, out _)
                        : gpu.TryToggleGpuCompatLayer(operation == "enable", "shaderfix", xml, out _);
                    Check(!success, "锁定 XML 仍报告成功");
                }
                Check(File.ReadAllBytes(paths.ConfigFilePath).SequenceEqual(config), "未还原启动器配置");
                Check(File.ReadAllBytes(xml).SequenceEqual(spice), "未保留原 XML");
                Check(Directory.GetFiles(modules).Length == files.Count, "回滚遗留或丢失文件");
                foreach (var file in files)
                    Check(File.ReadAllBytes(Path.Combine(modules, file.Key)).SequenceEqual(file.Value), "未还原文件：" + file.Key);
            });
        }
    }

    private static (GpuCompatLayerConfigurator Gpu, LauncherPaths Paths, string Xml) CreateGpuFixture(string root, string injection = null)
    {
        var paths = new LauncherPaths(root, Path.Combine(root, "launcher"), Path.Combine(root, "config.toml"));
        Put(root, "config.toml", AppConfigDefaults.CreateDefaultConfigText());
        Directory.CreateDirectory(Path.Combine(paths.GetContentsDirectoryPath(), "modules"));
        foreach (string file in GpuBaseFiles) Put(paths.GetBundledLibsDirectoryPath(), file, "bundled-" + file);
        Put(paths.GetBundledLibsDirectoryPath(), "d3d9.dll.dxvk", "bundled-dxvk");
        var editor = new SpiceXmlConfigEditor();
        var gpu = new GpuCompatLayerConfigurator(new AppConfigStore(paths.ConfigFilePath, null), paths, editor,
            NullLogger<GpuCompatLayerConfigurator>.Instance);
        return (gpu, paths, WriteSpiceDllConfig(root, injection));
    }

    private static string ReadGpuOption(string xml, string name) =>
        XDocument.Load(xml).Root.Element("game").Element("options").Elements("option")
            .SingleOrDefault(x => (string)x.Attribute("name") == name)?.Attribute("value")?.Value ?? "";
}
