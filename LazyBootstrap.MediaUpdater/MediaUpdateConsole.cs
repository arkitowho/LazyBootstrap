using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LazyBootstrap.MediaUpdate;

namespace LazyBootstrap.MediaUpdater;

internal sealed class MediaUpdateConsole : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string> _text = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Task _renderer;
    private MediaUpdateProgress _state = new(MediaUpdateStage.Waiting, "正在准备更新");
    private volatile string _game = "—";
    private volatile string _log = "—";
    private volatile bool _interactive;
    private int _scroll;
    private int _maximumScroll;
    private long _finishedTicks = -1;
    private DashboardFrame _previous;
    private int _width, _height, _left, _top;
    private ConsoleColor _foreground;
    private ConsoleColor _background;
    private bool _cursorVisible;
    private bool _savedConsole;

    public MediaUpdateConsole()
    {
        _interactive = !Console.IsOutputRedirected;
        _renderer = Task.Run(RenderLoopAsync);
    }

    public void SetGameDirectory(string game)
    {
        _game = game;
        _log = Path.Combine(game, MediaUpdateProtocol.UpdateStateFolderName, MediaUpdateProtocol.UpdateLogFileName);
    }

    public void ReportText(string message)
    {
        if (!_interactive) _text.Enqueue(MediaUpdateDashboard.Clean(message));
    }

    public void ReportProgress(MediaUpdateProgress state)
    {
        Volatile.Write(ref _state, state);
        if (state.Status != MediaUpdateStatus.Running)
            Interlocked.CompareExchange(ref _finishedTicks, _clock.Elapsed.Ticks, -1);
    }

    public async Task WaitForAcknowledgementAsync()
    {
        if (!Volatile.Read(ref _state).RequiresAcknowledgement || Console.IsInputRedirected) return;
        if (!_interactive) _text.Enqueue("按回车关闭此窗口。");
        try
        {
            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    if (key == ConsoleKey.Enter) return;
                    int delta = key switch
                    {
                        ConsoleKey.UpArrow => -1, ConsoleKey.DownArrow => 1,
                        ConsoleKey.PageUp => -5, ConsoleKey.PageDown => 5, _ => 0
                    };
                    int current = Volatile.Read(ref _scroll);
                    Interlocked.Exchange(ref _scroll, Math.Clamp(current + delta, 0, Volatile.Read(ref _maximumScroll)));
                }
                else await Task.Delay(50);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
    }

    private async Task RenderLoopAsync()
    {
        int tick = 0;
        try
        {
            if (_interactive)
            {
                try
                {
                    _foreground = Console.ForegroundColor;
                    _background = Console.BackgroundColor;
                    _cursorVisible = OperatingSystem.IsWindows() ? Console.CursorVisible : true;
                    _savedConsole = true;
                    Console.CursorVisible = false;
                }
                catch { FallBack(); }
            }
            while (!_stop.IsCancellationRequested)
            {
                DrawOrFlush(tick++);
                try { await Task.Delay(50, _stop.Token); }
                catch (OperationCanceledException) { break; }
            }
            DrawOrFlush(tick);
        }
        finally { RestoreConsole(); }
    }

    private void DrawOrFlush(int tick)
    {
        if (_interactive)
        {
            try { Draw(tick); }
            catch { FallBack(); }
        }
        if (!_interactive)
        {
            while (_text.TryDequeue(out string message))
            {
                try { Console.WriteLine(message); } catch { }
            }
        }
    }

    private void Draw(int tick)
    {
        int width = Math.Max(1, Console.WindowWidth - 1);
        int height = Math.Max(1, Console.WindowHeight - 1);
        int left = Console.WindowLeft, top = Console.WindowTop;
        if (width != _width || height != _height || left != _left || top != _top)
        {
            _previous = null;
            _width = width; _height = height; _left = left; _top = top;
        }
        long finished = Interlocked.Read(ref _finishedTicks);
        var elapsed = finished >= 0 ? TimeSpan.FromTicks(finished) : _clock.Elapsed;
        var frame = MediaUpdateDashboard.Build(Volatile.Read(ref _state), _game, _log, elapsed,
            width, height, tick, Volatile.Read(ref _scroll));
        Interlocked.Exchange(ref _maximumScroll, frame.MaximumDetailOffset);
        for (int y = 0; y < frame.Lines.Length; y++)
        {
            if (_previous != null && frame.Lines[y].SequenceEqual(_previous.Lines[y])) continue;
            Console.SetCursorPosition(left, top + y);
            int written = 0;
            foreach (var span in frame.Lines[y])
            {
                Console.ForegroundColor = span.Color ?? _foreground;
                Console.Write(span.Text);
                written += MediaUpdateDashboard.Width(span.Text);
            }
            Console.ForegroundColor = _foreground;
            Console.Write(new string(' ', Math.Max(0, width - written)));
        }
        _previous = frame;
    }

    private void FallBack()
    {
        _interactive = false;
        RestoreConsole();
        var state = Volatile.Read(ref _state);
        _text.Enqueue(MediaUpdateDashboard.Clean(state.Message));
        if (!string.IsNullOrEmpty(state.Warning)) _text.Enqueue(MediaUpdateDashboard.Clean(state.Warning));
    }

    private void RestoreConsole()
    {
        if (!_savedConsole) return;
        try { Console.ForegroundColor = _foreground; Console.BackgroundColor = _background; } catch { }
        try { Console.CursorVisible = _cursorVisible; } catch { }
        try { Console.SetCursorPosition(Console.WindowLeft, Console.WindowTop + Math.Max(0, Console.WindowHeight - 1)); } catch { }
        _savedConsole = false;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _renderer;
        _stop.Dispose();
    }
}
