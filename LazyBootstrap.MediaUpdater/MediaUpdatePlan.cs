using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.MediaUpdate;

internal sealed record MediaUpdateNode(bool IsDirectory, string Source, byte[] Content)
{
    public static MediaUpdateNode Read(string path) => new(Directory.Exists(path), path, null);
    public static MediaUpdateNode DirectoryNode => new(true, null, null);
}

internal sealed class MediaUpdatePlan
{
    public Dictionary<string, MediaUpdateNode> Before { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MediaUpdateNode> After { get; private set; }
    private readonly HashSet<string> _loadedTrees = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _game;
    private readonly string _package;
    private readonly CancellationToken _cancel;

    public MediaUpdatePlan(string game, string package, CancellationToken cancel)
    {
        _game = game;
        _cancel = cancel;

        var manifest = MediaUpdateManifest.Parse(File.ReadAllBytes(Path.Combine(package, MediaUpdateProtocol.ManifestFileName)));
        _package = package;

        foreach (var op in manifest.Operations)
        {
            string target = MediaUpdateSecurity.ValidateRelativePath(op.Target);
            string path = MediaUpdateSecurity.ResolveDestination(target, game);
            if (MediaUpdateSecurity.Same(target, MediaUpdateSecurity.UpdaterPath) && op.Type is "delete" or "editXml")
                throw new IOException("不能删除或编辑更新器。");
            Load(target, path, true);
            for (string parent = Parent(target); parent != null; parent = Parent(parent)) Load(parent, Destination(parent), false);
        }
        // The pending replacement is an internal target, never a package-controlled path.
        Load(MediaUpdateSecurity.PendingPath, Destination(MediaUpdateSecurity.PendingPath), false);
        After = new Dictionary<string, MediaUpdateNode>(Before, StringComparer.OrdinalIgnoreCase);
        foreach (var op in manifest.Operations)
        {
            _cancel.ThrowIfCancellationRequested();
            string target = MediaUpdateSecurity.ValidateRelativePath(op.Target);
            if (op.Type == "delete") Remove(target);
            else if (op.Type == "editXml")
            {
                if (!After.TryGetValue(target, out var node) || node.IsDirectory) throw new IOException("未找到待修改 XML 文件：" + target);
                byte[] originalText = node.Content ?? File.ReadAllBytes(node.Source);
                After[target] = node with { Content = MediaXmlEditor.Apply(originalText, op) };
            }
            else
            {
                string source = Path.GetFullPath(Path.Combine(_package, op.Source));
                if (!DirectorySafety.IsWithin(source, Path.Combine(_package, "source"))) throw new IOException("更新源路径越界。");

                if (!File.Exists(source) && !Directory.Exists(source)) throw new IOException("未找到更新源：" + op.Source);
                if (op.Type == "mirror")
                {
                    if (!Directory.Exists(source)) throw new IOException("镜像源必须是目录。");
                    Remove(target);
                }
                Copy(source, target);
            }
        }
    }

    private void Load(string relative, string path, bool recursive)
    {
        _cancel.ThrowIfCancellationRequested();

        if (!Directory.Exists(path) && !File.Exists(path)) return;
        Before.TryAdd(relative, MediaUpdateNode.Read(path));
        if (!recursive || !Directory.Exists(path) || !_loadedTrees.Add(relative)) return;
        foreach (string child in Directory.EnumerateFileSystemEntries(path))
            Load(relative + "/" + Path.GetFileName(child), Destination(relative + "/" + Path.GetFileName(child)), true);
    }

    private void Remove(string target)
    {
        foreach (string key in After.Keys.Where(p => Within(p, target)).OrderByDescending(p => p.Length).ToArray())
        {
            if (Protected(key)) continue;
            if (After[key].IsDirectory && After.Keys.Any(p => !MediaUpdateSecurity.Same(p, key) && Within(p, key))) continue;
            After.Remove(key);
        }
    }

    private static bool Protected(string path) => MediaUpdateSecurity.Same(path, MediaUpdateSecurity.UpdaterPath)
        || MediaUpdateSecurity.Same(path, MediaUpdateSecurity.PendingPath);

    private void Copy(string source, string target)
    {
        _cancel.ThrowIfCancellationRequested();
        MediaUpdateSecurity.ResolveDestination(target, _game);

        bool isDirectory = Directory.Exists(source);
        if (MediaUpdateSecurity.Same(target, MediaUpdateSecurity.UpdaterPath))
        {
            if (isDirectory) throw new IOException("更新器载荷必须是文件。");
            target = MediaUpdateSecurity.PendingPath;
        }
        if (After.TryGetValue(target, out var current) && current.IsDirectory != isDirectory)
            throw new IOException("更新文件与目录类型冲突，或镜像转换会破坏受保护文件：" + target);
        EnsureParents(target);
        if (isDirectory)
        {
            if (!After.ContainsKey(target)) After[target] = MediaUpdateNode.DirectoryNode;
            foreach (string child in Directory.EnumerateFileSystemEntries(source).OrderBy(p => p, StringComparer.Ordinal))
                Copy(child, target + "/" + Path.GetFileName(child));
        }
        else
        {
            After[target] = MediaUpdateNode.Read(source);
        }
    }

    private void EnsureParents(string path)
    {
        string parent = Parent(path);
        if (parent == null) return;
        if (After.TryGetValue(parent, out var node))
        {
            if (!node.IsDirectory) throw new IOException("更新目标的父路径是文件：" + parent);
            return;
        }
        EnsureParents(parent);
        After[parent] = MediaUpdateNode.DirectoryNode;
    }

    private string Destination(string path) => MediaUpdateSecurity.ResolveDestination(path, _game, true);
    private static string Parent(string path) => path.LastIndexOf('/') is var i && i >= 0 ? path[..i] : null;
    private static bool Within(string path, string root) => MediaUpdateSecurity.Same(path, root) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
}
