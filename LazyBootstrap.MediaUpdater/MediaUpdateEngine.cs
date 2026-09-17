using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.MediaUpdate;

internal enum MediaUpdateAction { Remove, Directory, Write }
internal sealed record MediaUpdateChange(string Path, MediaUpdateAction Action, MediaUpdateNode Node);

internal sealed class MediaUpdateEngine : IDisposable
{
    private readonly string _game;
    private readonly Action<string> _report;
    private readonly MediaUpdateLog _log;
    private readonly List<MediaUpdateChange> _changes;
    private bool _applied;

    private MediaUpdateEngine(string game, Action<string> report, MediaUpdatePlan plan)
    {
        _game = game;
        _report = report;
        _changes = BuildChanges(plan);
        _log = new MediaUpdateLog(game, true);
    }

    public static MediaUpdateEngine Prepare(string game, string package, Action<string> report = null, CancellationToken cancel = default)
    {
        game = DirectorySafety.Normalize(game);
        package = DirectorySafety.Normalize(package);
        cancel.ThrowIfCancellationRequested();
        try { report?.Invoke("正在预演更新..."); } catch { }
        try
        {
            var plan = new MediaUpdatePlan(game, package, cancel);
            var engine = new MediaUpdateEngine(game, report, plan);
            try
            {
                engine.CheckAccess(plan, cancel);
                engine.Report("预演通过，所有文件均可安装。");
                return engine;
            }
            catch { engine.Dispose(); throw; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new IOException("预演失败，未进行安装。" + ex.Message, ex); }
    }

    private static List<MediaUpdateChange> BuildChanges(MediaUpdatePlan plan)
    {
        var changes = new List<MediaUpdateChange>();
        foreach (var pair in plan.Before.Where(p => !plan.After.TryGetValue(p.Key, out var node) || node.IsDirectory != p.Value.IsDirectory)
                     .OrderByDescending(p => p.Key.Count(c => c == '/')).ThenBy(p => p.Value.IsDirectory))
            changes.Add(new(pair.Key, MediaUpdateAction.Remove, pair.Value));
        foreach (var pair in plan.After.Where(p => p.Value.IsDirectory && (!plan.Before.TryGetValue(p.Key, out var node) || !node.IsDirectory))
                     .OrderBy(p => p.Key.Count(c => c == '/')))
            changes.Add(new(pair.Key, MediaUpdateAction.Directory, pair.Value));
        foreach (var pair in plan.After.Where(p => !p.Value.IsDirectory && (!plan.Before.TryGetValue(p.Key, out var node) || !ReferenceEquals(node, p.Value)))
                     .OrderBy(p => MediaUpdateSecurity.Same(p.Key, MediaUpdateSecurity.PendingPath)))
            changes.Add(new(pair.Key, MediaUpdateAction.Write, pair.Value));
        return changes;
    }

    private void CheckAccess(MediaUpdatePlan plan, CancellationToken cancel)
    {
        var readSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in _changes)
        {
            cancel.ThrowIfCancellationRequested();
            string target = Destination(change.Path);
            try
            {
                if (change.Action == MediaUpdateAction.Remove)
                    MediaUpdateAccess.Remove(target, change.Node.IsDirectory);
                else
                {
                    if (change.Action == MediaUpdateAction.Write)
                    {
                        if (change.Node.Content == null && readSources.Add(change.Node.Source)) MediaUpdateAccess.Read(change.Node.Source);
                        if (plan.Before.TryGetValue(change.Path, out var old) && !old.IsDirectory)
                        {
                            MediaUpdateAccess.Overwrite(target);
                            continue;
                        }
                    }
                    // A planned file-to-directory conversion is not an existing parent directory.
                    string parent = Path.GetDirectoryName(target)!;
                    bool newParents = false;
                    while (!Directory.Exists(parent))
                    {
                        newParents = true;
                        parent = Path.GetDirectoryName(parent) ?? throw new IOException("未找到可创建目标的父目录。");
                    }
                    MediaUpdateAccess.CreateIn(parent, change.Action == MediaUpdateAction.Directory, newParents);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new IOException($"无法安装 {change.Path}：{ex.Message}", ex); }
        }
    }

    public void Apply(CancellationToken cancel = default)
    {
        if (_applied) throw new InvalidOperationException("安装计划已经执行，请重新预演。");
        cancel.ThrowIfCancellationRequested();
        _applied = true;
        string current = "尚未写入文件";
        try
        {
            for (int i = 0; i < _changes.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var change = _changes[i];
                current = change.Path;
                string target = Destination(current);
                _log.Write($"Installing {i + 1}/{_changes.Count}: {change.Action} {current}");
                switch (change.Action)
                {
                    case MediaUpdateAction.Remove:
                        if (change.Node.IsDirectory) Directory.Delete(target, false);
                        else File.Delete(target);
                        break;
                    case MediaUpdateAction.Directory:
                        Directory.CreateDirectory(target);
                        break;
                    case MediaUpdateAction.Write:
                        if (change.Node.Content != null) File.WriteAllBytes(target, change.Node.Content);
                        else File.Copy(change.Node.Source, target, true);
                        break;
                }
                Report($"正在安装 {i + 1}/{_changes.Count}：{current}");
                cancel.ThrowIfCancellationRequested();
            }
            Report("更新安装完成。");
        }
        catch (Exception ex)
        {
            string message = $"安装未完成，部分文件可能已更新。{(ex is OperationCanceledException ? "用户已取消。" : "")}文件：{current}；{ex.Message}";
            _log.Write(message);
            throw new IOException(message, ex);
        }
    }

    private string Destination(string relative) => Path.Combine(_game, relative.Replace('/', Path.DirectorySeparatorChar));
    private void Report(string message)
    {
        _log.Write(message);
        try { _report?.Invoke(message); } catch { }
    }
    public void Dispose() => _log.Dispose();
}
