using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LazyBootstrap.MediaUpdate;

namespace LazyBootstrap.MediaUpdater;

internal sealed record DashboardSpan(string Text, ConsoleColor? Color = null);
internal sealed record DashboardFrame(DashboardSpan[][] Lines, int DetailOffset, int MaximumDetailOffset);

// Pure layout: no terminal access and no dependency on installation or wall-clock timing.
internal static class MediaUpdateDashboard
{
    private const ConsoleColor Accent = ConsoleColor.Cyan;
    private const ConsoleColor Muted = ConsoleColor.DarkGray;

    public static DashboardFrame Build(MediaUpdateProgress state, string game, string log, TimeSpan elapsed,
        int width, int height, int tick, int detailOffset)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        bool bordered = width >= 60 && height >= 18;
        bool exceptional = state.Status is MediaUpdateStatus.Failed or MediaUpdateStatus.Cancelled
            || !string.IsNullOrEmpty(state.Warning);
        var card = new Card(width, height, bordered, exceptional ? 22 : 15);
        if (exceptional) return BuildException(card, state, game, log, elapsed, detailOffset);
        if (state.Status == MediaUpdateStatus.Succeeded) BuildSuccess(card, state, elapsed);
        else BuildRunning(card, state, elapsed, tick);
        return new(card.Lines, 0, 0);
    }

    private static void BuildRunning(Card card, MediaUpdateProgress state, TimeSpan elapsed, int tick)
    {
        string title = Title(state);
        string help = state.Stage == MediaUpdateStage.Installing
            ? "Ctrl+C 取消 · 已完成的修改将保留" : "Ctrl+C 取消";
        bool installing = state.Stage is MediaUpdateStage.Installing or MediaUpdateStage.Cleanup;
        string operation = state.Operation switch
        {
            MediaUpdateAction.Remove => "删除",
            MediaUpdateAction.Directory => "创建目录",
            MediaUpdateAction.Write => "写入文件",
            _ => ""
        };
        string bar = installing ? ProgressBar(state, card.InnerWidth)
            : $"处理中 {new string('.', tick % 5 + 1)}";
        if (card.Height < 8)
        {
            card.Text(0, title, Accent);
            if (card.Height >= 3) card.Text(1, bar, Accent);
            if (card.Height >= 4) card.Text(2, Ellipsize(state.Path ?? Elapsed(elapsed), card.InnerWidth));
            if (card.Height > 1) card.Text(card.Height - 1, help, Muted);
            return;
        }

        bool roomy = card.Height >= 13;
        card.Text(card.Bordered ? 1 : 0, "Media Updater", Accent);
        if (roomy) card.Row(card.Bordered ? 3 : 2, Navigation(state));
        int headline = roomy ? 5 : 2;
        card.Split(headline, title, installing ? $"{Percent(state)}%" : "", Accent, Accent);
        card.Text(headline + 1, bar, Accent);
        card.Split(headline + 2, installing ? Count(state) : "", Elapsed(elapsed));
        int operationRow = roomy ? 9 : 5;
        // Compact layouts can have only eight rows; keep the file above the fixed footer.
        if (operationRow + 1 < card.Height - (card.Bordered ? 2 : 1))
        {
            card.Text(operationRow, operation, Muted);
            card.Text(operationRow + 1, Ellipsize(state.Path ?? "", card.InnerWidth));
        }
        card.Text(card.Height - (card.Bordered ? 2 : 1), help, Muted);
    }

    private static void BuildSuccess(Card card, MediaUpdateProgress state, TimeSpan elapsed)
    {
        string help = SuccessHelp(state);
        if (card.Height < 7)
        {
            card.Center(0, MediaUpdateRunner.SuccessMessage, ConsoleColor.Green);
            if (card.Height >= 3) card.Center(1, Count(state));
            if (card.Height >= 4) card.Center(2, Elapsed(elapsed), Muted);
            if (card.Height > 1) card.Center(card.Height - 1, help, ConsoleColor.Green);
            return;
        }
        card.Text(card.Bordered ? 1 : 0, "Media Updater", Accent);
        int titleRow = card.Height / 2 - 2;
        card.Center(titleRow, MediaUpdateRunner.SuccessMessage, ConsoleColor.Green);
        card.Center(titleRow + 2, Count(state));
        card.Center(titleRow + 3, Elapsed(elapsed), Muted);
        card.Center(card.Height - (card.Bordered ? 4 : 2), help, ConsoleColor.Green);
    }

    private static DashboardFrame BuildException(Card card, MediaUpdateProgress state, string game, string log,
        TimeSpan elapsed, int detailOffset)
    {
        var color = state.Status switch
        {
            MediaUpdateStatus.Succeeded => ConsoleColor.Green,
            MediaUpdateStatus.Failed => ConsoleColor.Red,
            MediaUpdateStatus.Cancelled => ConsoleColor.Yellow,
            _ => Accent
        };
        string help = state.RequiresAcknowledgement ? "↑↓ / PageUp PageDown 查看详情 · 回车关闭"
            : state.Status == MediaUpdateStatus.Succeeded ? SuccessHelp(state) : "Ctrl+C 取消";
        int detailStart, detailEnd;
        if (card.Height >= 10)
        {
            card.Text(card.Bordered ? 1 : 0, "Media Updater", Accent);
            card.Text(card.Bordered ? 3 : 2, Title(state), color);
            card.Split(card.Bordered ? 4 : 3,
                state.Stage is MediaUpdateStage.Waiting or MediaUpdateStage.Preflight ? "" : Count(state), Elapsed(elapsed));
            detailStart = card.Bordered ? 6 : 5;
            int gameRow = card.Height - (card.Bordered ? 4 : 3);
            detailEnd = gameRow - 2;
            card.Text(gameRow, "游戏：" + Ellipsize(game, card.InnerWidth - 6), Muted);
            card.Text(gameRow + 1, "日志：" + Ellipsize(log, card.InnerWidth - 6), Muted);
        }
        else
        {
            card.Text(0, Title(state), color);
            detailStart = 1;
            detailEnd = card.Height - 2;
        }
        if (card.Height > 1) card.Text(card.Height - (card.Bordered ? 2 : 1),
            card.InnerWidth >= Width(help) ? help : state.RequiresAcknowledgement ? "↑↓ 详情 · 回车关闭" : help,
            state.RequiresAcknowledgement ? ConsoleColor.Yellow : color);

        var details = new List<DashboardSpan>();
        void Detail(string text, ConsoleColor? foreground)
        {
            if (!string.IsNullOrWhiteSpace(text))
                details.AddRange(Wrap(text, card.InnerWidth).Select(line => new DashboardSpan(line, foreground)));
        }
        if (!string.IsNullOrEmpty(state.Path)) Detail("目标：" + state.Path, color);
        if (!string.IsNullOrEmpty(state.Warning)) Detail(state.Warning, ConsoleColor.Yellow);
        if (state.Status != MediaUpdateStatus.Succeeded) Detail(state.Detail, color);
        // Tiny layouts have no fixed path footer; include paths in their scrollable details only.
        if (card.Height < 10)
        {
            Detail("游戏目录：" + game, Muted);
            Detail("日志位置：" + log, Muted);
        }
        int capacity = Math.Max(0, detailEnd - detailStart + 1);
        int maximum = Math.Max(0, details.Count - capacity);
        int offset = Math.Clamp(detailOffset, 0, maximum);
        for (int i = 0; i < capacity && i + offset < details.Count; i++) card.Row(detailStart + i, details[i + offset]);
        return new(card.Lines, offset, maximum);
    }

    private static string Title(MediaUpdateProgress state) => state.Status switch
    {
        MediaUpdateStatus.Succeeded => MediaUpdateRunner.SuccessMessage,
        MediaUpdateStatus.Cancelled when state.Stage == MediaUpdateStage.Installing => "安装未完成，部分文件可能已更新",
        MediaUpdateStatus.Cancelled => "更新已取消，未进行安装",
        MediaUpdateStatus.Failed when state.Stage == MediaUpdateStage.Installing => "安装未完成，部分文件可能已更新",
        MediaUpdateStatus.Failed when state.Stage == MediaUpdateStage.Preflight => "预演失败，未进行安装",
        MediaUpdateStatus.Failed => "更新失败，未进行安装",
        _ => state.Stage switch
        {
            MediaUpdateStage.Waiting => "正在等待启动器退出",
            MediaUpdateStage.Preflight => "正在预演检查",
            MediaUpdateStage.Installing => state.Total == 0 ? "无需修改" : "正在安装更新",
            MediaUpdateStage.Cleanup => "正在完成更新",
            _ => "更新完成"
        }
    };

    private static string Count(MediaUpdateProgress state) => state.Total == 0 ? "无需修改" : $"已完成 {state.Completed} / {state.Total} 项";
    private static string Elapsed(TimeSpan elapsed) => $"已用时 {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}";
    private static string SuccessHelp(MediaUpdateProgress state) => state.RemainingSeconds > 0
        ? $"将在 {state.RemainingSeconds} 秒后重新启动启动器" : "正在重新启动启动器";
    private static int Percent(MediaUpdateProgress state) => state.Total == 0 ? 100
        : (int)Math.Clamp((long)state.Completed * 100 / state.Total, 0, 100);

    private static string ProgressBar(MediaUpdateProgress state, int width)
    {
        int filled = Percent(state) * width / 100;
        return new string('█', filled) + new string('░', width - filled);
    }

    private static DashboardSpan[] Navigation(MediaUpdateProgress state)
    {
        int active = state.Stage switch
        {
            MediaUpdateStage.Waiting => 0, MediaUpdateStage.Preflight => 1,
            MediaUpdateStage.Installing or MediaUpdateStage.Cleanup => 2, _ => 3
        };
        var spans = new List<DashboardSpan>();
        string[] stages = ["等待启动器", "预演检查", "安装更新", "完成"];
        for (int i = 0; i < stages.Length; i++)
        {
            if (i > 0) spans.Add(new(" / ", Muted));
            spans.Add(new(stages[i], i < active ? ConsoleColor.Gray : i == active ? Accent : Muted));
        }
        return spans.ToArray();
    }

    // Every frame includes blank margins, so transitions erase the previous card completely.
    private sealed class Card
    {
        public DashboardSpan[][] Lines { get; }
        public bool Bordered { get; }
        public int Height { get; }
        public int InnerWidth { get; }
        private readonly int _left;
        private readonly int _top;

        public Card(int width, int height, bool bordered, int preferredHeight)
        {
            Bordered = bordered;
            int cardWidth = Math.Min(72, bordered ? width - 4 : width);
            Height = Math.Min(preferredHeight, bordered ? height - 2 : height);
            InnerWidth = cardWidth - (bordered ? 6 : 0);
            _left = (width - cardWidth) / 2;
            _top = (height - Height) / 2;
            Lines = Enumerable.Range(0, height).Select(_ => Array.Empty<DashboardSpan>()).ToArray();
            if (bordered)
            {
                Lines[_top] = [new(new string(' ', _left) + "┌" + new string('─', cardWidth - 2) + "┐", Muted)];
                Lines[_top + Height - 1] = [new(new string(' ', _left) + "└" + new string('─', cardWidth - 2) + "┘", Muted)];
                for (int y = 1; y < Height - 1; y++) Row(y);
            }
        }

        public void Row(int y, params DashboardSpan[] spans)
        {
            if (y < (Bordered ? 1 : 0) || y >= Height - (Bordered ? 1 : 0)) return;
            var fitted = Fit(spans, InnerWidth);
            var row = new List<DashboardSpan> { new(new string(' ', _left) + (Bordered ? "│  " : ""), Muted) };
            row.AddRange(fitted);
            if (Bordered) row.Add(new(new string(' ', InnerWidth - fitted.Sum(s => Width(s.Text))) + "  │", Muted));
            Lines[_top + y] = row.ToArray();
        }

        public void Text(int y, string text, ConsoleColor? color = null) => Row(y, new DashboardSpan(text, color));
        public void Center(int y, string text, ConsoleColor? color = null)
        {
            string clipped = Take(text, InnerWidth);
            Row(y, new DashboardSpan(new string(' ', (InnerWidth - Width(clipped)) / 2)), new DashboardSpan(clipped, color));
        }
        public void Split(int y, string left, string right, ConsoleColor? leftColor = null, ConsoleColor? rightColor = null)
        {
            right = Take(right, InnerWidth);
            left = Take(left, Math.Max(0, InnerWidth - Width(right) - 1));
            Row(y, new DashboardSpan(left, leftColor), new DashboardSpan(new string(' ', InnerWidth - Width(left) - Width(right))),
                new DashboardSpan(right, rightColor));
        }
    }

    // Strip terminal controls, bidi controls and zero-width format characters, but retain combining marks.
    internal static string Clean(string text)
    {
        var result = new StringBuilder();
        foreach (var rune in (text ?? "").EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                result.Append(' ');
            else result.Append(rune.ToString());
        }
        return result.ToString();
    }

    private static int RuneWidth(Rune rune)
    {
        if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark) return 0;
        int n = rune.Value;
        return n >= 0x1100 && (n <= 0x115f || n is 0x2329 or 0x232a ||
            n >= 0x2e80 && n <= 0xa4cf && n != 0x303f || n >= 0xac00 && n <= 0xd7a3 ||
            n >= 0xf900 && n <= 0xfaff || n >= 0xfe10 && n <= 0xfe19 || n >= 0xfe30 && n <= 0xfe6f ||
            n >= 0xff00 && n <= 0xff60 || n >= 0xffe0 && n <= 0xffe6 || n >= 0x1f300 && n <= 0x1faff ||
            n >= 0x20000 && n <= 0x3fffd) ? 2 : 1;
    }

    internal static int Width(string text) => Clean(text).EnumerateRunes().Sum(RuneWidth);

    private static string Take(string text, int width, bool tail = false)
    {
        var runes = Clean(text).EnumerateRunes().ToArray();
        var taken = new List<Rune>();
        int used = 0;
        foreach (var rune in tail ? runes.Reverse() : runes)
        {
            int size = RuneWidth(rune);
            if (used + size > width) break;
            taken.Add(rune); used += size;
        }
        if (tail) taken.Reverse();
        // Avoid orphaned combining marks after clipping.
        while (taken.Count > 0 && RuneWidth(taken[0]) == 0) taken.RemoveAt(0);
        return string.Concat(taken.Select(r => r.ToString()));
    }

    internal static string Ellipsize(string text, int width)
    {
        width = Math.Max(0, width);
        text = Clean(text);
        if (Width(text) <= width) return text;
        if (width <= 3) return new string('.', width);
        int start = (width - 3) / 3;
        return Take(text, start) + "..." + Take(text, width - 3 - start, true);
    }

    private static DashboardSpan[] Fit(IEnumerable<DashboardSpan> spans, int width)
    {
        var result = new List<DashboardSpan>();
        foreach (var span in spans)
        {
            string text = Take(span.Text, Math.Max(0, width));
            result.Add(span with { Text = text });
            width -= Width(text);
            if (width <= 0) break;
        }
        return result.ToArray();
    }

    internal static List<string> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        var lines = new List<string>();
        foreach (string paragraph in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = new StringBuilder();
            int used = 0;
            foreach (var rune in Clean(paragraph).EnumerateRunes())
            {
                int size = RuneWidth(rune);
                if (used + size > width)
                {
                    if (line.Length > 0) lines.Add(line.ToString());
                    line.Clear(); used = 0;
                }
                if (size > width) { line.Append('?'); used++; }
                else if (size > 0 || line.Length > 0) { line.Append(rune.ToString()); used += size; }
            }
            lines.Add(line.ToString());
        }
        return lines;
    }
}
