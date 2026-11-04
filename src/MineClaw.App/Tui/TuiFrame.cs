using System.Text;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Hosting;
using MineClaw.App.Viewport;
using MineClaw.Data;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MineClaw.App.Tui;

public enum InputMode { None, Chat, Command }

/// <summary>Mutable UI state (owned by the TUI loop thread).</summary>
public sealed class TuiState
{
    public int Selected { get; set; }
    public ViewMode View { get; set; } = ViewMode.Top;
    public InputMode Mode { get; set; }
    public StringBuilder Input { get; } = new();
    /// <summary>Brain panel lines scrolled up from the bottom (0 = follow).</summary>
    public int BrainScroll { get; set; }
    /// <summary>Narrow terminals: which panel takes the top slot (0 viewport, 1 tokens, 2 brain).</summary>
    public int NarrowPanel { get; set; }
    public string? Flash { get; set; }
    /// <summary>Isometric view settings (rotation, zoom, cutaway, labels) — shared by all bots.</summary>
    public IsoView Iso { get; } = new();
    /// <summary>Reused render buffers and styles of the isometric viewport.</summary>
    internal IsoRenderer IsoRenderer { get; } = new();
    internal Dictionary<(uint, uint, IsoCellFlags), Style> IsoStyles { get; } = new();
}

/// <summary>Everything one frame needs (snapshots already taken).</summary>
public sealed record FrameInput(TuiState State, IReadOnlyList<BotSnapshot> Bots, BotSnapshot Selected, IReadOnlyList<LogLine> Logs,
    HiveRoster Roster, bool Unicode, DateTimeOffset Now, IsoColours Colours = IsoColours.TrueColor);

/// <summary>
/// Builds one full-screen frame. Wide (≥100 cols): header / [viewport + chat | tokens + brain] / input. Narrow: header /
/// [viewport|tokens|brain (p cycles)] + chat / input. Every line is pre-cut to its panel's inner width, so panels never wrap.
/// </summary>
public static class TuiFrame
{
    public const int MinWidth = 50, MinHeight = 16;

    public static IRenderable Build(FrameInput f, int width, int height)
    {
        if (width < MinWidth || height < MinHeight)
            return new Panel(new Markup($"terminal too small ({width}x{height}); need {MinWidth}x{MinHeight}. q quits."))
            { Expand = true, Height = height, Border = BoxBorder.Square };

        string dash = f.Unicode ? "—" : "-";
        var sel = f.Selected;
        int headerH = 4, inputH = 3, bodyH = height - headerH - inputH;
        var header = Box($"MineClaw {dash} {sel.Name}", HeaderLines(f, width - 2), width, headerH);
        var input = Box($"Input {dash} to {sel.Name}", [InputLine(f, width - 2)], width, inputH);

        Layout body;
        if (width >= 100)
        {
            int rightW = Math.Clamp(width * 42 / 100, 44, 72), leftW = width - rightW;
            int chatH = Math.Clamp(bodyH / 3, 5, 12), viewH = bodyH - chatH;
            int tokH = Math.Min(16, Math.Max(8, bodyH / 2)), brainH = bodyH - tokH;
            body = new Layout("body").SplitColumns(
                new Layout("left").Size(leftW).SplitRows(
                    new Layout("view").Size(viewH).Update(ViewportPanel(f, leftW, viewH, dash)),
                    new Layout("chat").Size(chatH).Update(ChatPanel(f, leftW, chatH, dash))),
                new Layout("right").Size(rightW).SplitRows(
                    new Layout("tokens").Size(tokH).Update(TokensPanel(f, rightW, tokH, dash)),
                    new Layout("brain").Size(brainH).Update(BrainPanel(f, rightW, brainH, dash))));
        }
        else
        {
            int chatH = Math.Clamp(bodyH / 3, 4, 10), topH = bodyH - chatH;
            var top = (f.State.NarrowPanel % 3) switch
            {
                1 => TokensPanel(f, width, topH, dash),
                2 => BrainPanel(f, width, topH, dash),
                _ => ViewportPanel(f, width, topH, dash),
            };
            body = new Layout("body").SplitRows(
                new Layout("top").Size(topH).Update(top),
                new Layout("chat").Size(chatH).Update(ChatPanel(f, width, chatH, dash)));
        }

        return new Layout("root").SplitRows(
            new Layout("header").Size(headerH).Update(header),
            body.Size(bodyH),
            new Layout("input").Size(inputH).Update(input));
    }

    // ------------------------------------------------------------------ panels

    private static Panel Box(string title, IEnumerable<string> markupLines, int w, int h, bool dim = false)
    {
        var lines = markupLines.Take(Math.Max(0, h - 2)).ToList();
        if (lines.Count == 0) lines.Add(" ");
        return new Panel(new Markup(string.Join("\n", lines)))
        {
            Header = new PanelHeader(" " + Markup.Escape(title) + " "),
            Border = BoxBorder.Rounded,
            BorderStyle = dim ? new Style(Color.Grey) : new Style(Color.Grey58),
            Padding = new Padding(0, 0, 0, 0),
            Expand = true,
            Width = w,
            Height = h,
        };
    }

    private static IEnumerable<string> HeaderLines(FrameInput f, int w)
    {
        yield return StatusLine(f, w);
        yield return StripLine(f, w);
    }

    /// <summary>The selected bot: status, name, position, vitals, task, budget, brain and the flash message.</summary>
    private static string StatusLine(FrameInput f, int w)
    {
        var s = f.Selected;
        var l1 = new MLine(w);
        l1.Add(StatusDot(s.Status, f.Unicode) + " ", StatusStyle(s.Status)).Add(s.Status.ToString().ToLowerInvariant() + "  ", StatusStyle(s.Status));
        l1.Add(s.Name, "bold").Add("  ");
        if (s.Pos is { } p) l1.Add("@ " + p + "  ");
        if (s.Status != BotStatus.Offline) l1.Add($"hp {s.Hp}/20 ", s.Hp <= 6 ? "bold red" : null).Add($"food {s.Food}  ", s.Food <= 6 ? "yellow" : null);
        l1.Add("task: ", "grey").Add((s.Task ?? "idle") + "  ");
        l1.Add("budget: ", "grey").Add(s.BudgetMode, ModeStyle(s.BudgetMode));
        if (s.Brain != null) l1.Add("  brain: ", "grey").Add(s.Brain, s.Brain.StartsWith("asleep", StringComparison.Ordinal) ? "aqua" : null);
        if (f.State.Flash != null) l1.Add("  " + f.State.Flash, "italic aqua");
        return l1.ToString();
    }

    /// <summary>Bot strip: as much detail as fits (task → hp/food → short names), so every hive member stays visible; then the clock.</summary>
    private static string StripLine(FrameInput f, int w)
    {
        string clock = DateTimeOffset.Now.ToString("HH:mm:ss");
        List<string> entries = [];
        for (int detail = 3; detail >= 0; detail--)
        {
            entries = f.Bots.Select((b, i) => StripEntry(b, i, detail, f.Unicode)).ToList();
            if (entries.Sum(e => e.Length + 1) + clock.Length <= w) break;
        }
        var l2 = new MLine(w);
        for (int i = 0; i < f.Bots.Count; i++)
        {
            var b = f.Bots[i];
            bool off = b.Status == BotStatus.Offline;
            l2.Add(entries[i], i == f.State.Selected ? "black on aqua" : off ? "grey" : StatusStyle(b.Status)).Add(" ");
        }
        l2.Add(clock, "grey");
        return l2.ToString();
    }

    private static string StripEntry(BotSnapshot b, int i, int detail, bool unicode)
    {
        bool off = b.Status == BotStatus.Offline;
        string name = detail >= 1 ? b.Name : ShortName(b.Name);
        string dot = StatusDot(b.Status, unicode);
        if (off) return detail >= 2 ? $" {i + 1} {name} {dot} offline " : $" {i + 1} {name} {dot} ";
        return detail switch
        {
            3 => $" {i + 1} {name} {dot} {b.Hp}/{b.Food} {Trunc(b.Task ?? "idle", 14)} ",
            2 => $" {i + 1} {name} {dot} {b.Hp}/{b.Food} ",
            _ => $" {i + 1} {name} {dot} ",
        };
    }

    /// <summary>"maid_chan_420" → "_420"; names without a numeric suffix stay (cut to 10).</summary>
    private static string ShortName(string name)
    {
        int u = name.LastIndexOf('_');
        return u > 0 && u < name.Length - 1 && name[(u + 1)..].All(char.IsDigit) ? name[u..] : Trunc(name, 10);
    }

    private static Panel ViewportPanel(FrameInput f, int w, int h, string dash)
    {
        var s = f.Selected;
        var st = f.State;
        bool nether = s.Dimension == -1;
        bool iso = st.View == ViewMode.Iso && s.Voxels != null && s.Viewport != null;
        var view = st.View == ViewMode.Iso && !iso ? ViewMode.Top : st.View;
        string title = ViewportTitle(f, view, nether, dash);
        int iw = w - 2, ih = h - 2;
        if (s.Viewport is not { } grid)
            return Box(title, [Markup.Escape(s.Status == BotStatus.Offline ? "offline / not spawned" : "no world data yet")], w, h, dim: true);
        bool legend = ih >= 12;
        int mapH = ih - (legend ? 1 : 0);
        if (iso) return IsoPanel(f, s, grid, title, w, h, iw, mapH, legend, nether);
        var lines = MapLines(f, grid, view, iw, mapH);
        if (legend) lines.Add(TopLegend(f, iw));
        return Box(title, lines, w, h);
    }

    /// <summary>"Viewport — name (@ x,y,z) · mode · nether".</summary>
    private static string ViewportTitle(FrameInput f, ViewMode view, bool nether, string dash)
    {
        var s = f.Selected;
        string sep = f.Unicode ? " · " : " - ";
        return $"Viewport {dash} {s.Name}" + (s.Pos is { } p ? $" (@ {p})" : "") + sep + ViewModeLabel(f, view) + (nether ? sep + "nether" : "");
    }

    /// <summary>The viewport title's mode: iso (compass, cutaway, zoom), side, or top (saying so when iso has no 3D data yet).</summary>
    private static string ViewModeLabel(FrameInput f, ViewMode view) => view switch
    {
        ViewMode.Iso => IsoModeLabel(f),
        ViewMode.Side => "side, looking north",
        _ => f.State.View == ViewMode.Iso ? "top (no 3D data yet)" : "top",
    };

    private static string IsoModeLabel(FrameInput f)
    {
        var iso = f.State.Iso;
        bool u = f.Unicode;
        return "iso " + (u ? "N" + IsoProjection.Arrows[iso.Rotation & 3] : "N:" + IsoProjection.AsciiArrows[iso.Rotation & 3])
            + (iso.Cutaway && f.Selected.Voxels is { } vx ? (u ? " · cut y≤" : " - cut y<=") + (vx.Cy + 1) : "")
            + (iso.Zoom != 1 ? (u ? " · zoom " : " - zoom ") + iso.Zoom : "");
    }

    /// <summary>The top/side map, centred in the panel and padded to <paramref name="mapH"/> lines.</summary>
    private static List<string> MapLines(FrameInput f, ViewportGrid grid, ViewMode view, int iw, int mapH)
    {
        var lines = new List<string>();
        var cells = ViewportRenderer.Render(grid, view, iw, mapH, f.Roster.Glyph);
        int padTop = Math.Max(0, (mapH - cells.Length) / 2);
        int padLeft = cells.Length > 0 ? Math.Max(0, (iw - cells[0].Length) / 2) : 0;
        for (int i = 0; i < padTop; i++) lines.Add("");
        foreach (var row in cells) lines.Add(new string(' ', padLeft) + CellsMarkup(row));
        while (lines.Count < mapH) lines.Add("");
        return lines;
    }

    private static string TopLegend(FrameInput f, int iw)
    {
        var hive = string.Join("", f.Roster.Names.Select(n => f.Roster.Glyph(n)).OfType<char>());
        return new MLine(iw).Add("# stone . dirt ~ water ! lava T log * leaves C chest F furnace W bench + solid  @ me " + hive + " hive m mob i item  v: view", "grey").ToString();
    }

    /// <summary>The isometric viewport: an <see cref="IsoRenderer"/> canvas plus a one-line key/legend.</summary>
    private static Panel IsoPanel(FrameInput f, BotSnapshot s, ViewportGrid grid, string title, int w, int h, int iw, int mapH, bool legend, bool nether)
    {
        var st = f.State;
        var input = new IsoInput(s.Voxels!, grid.Markers, st.Iso, f.Colours, f.Unicode, nether, f.Roster.Glyph, f.Roster.ColourOf, f.Roster.ColourOf(s.Name));
        var frame = st.IsoRenderer.Render(input, iw, mapH);
        IRenderable content = new CanvasRenderable(frame, st.IsoStyles);
        if (legend)
        {
            var hive = string.Join("", f.Roster.Names.Where(n => !string.Equals(n, s.Name, StringComparison.OrdinalIgnoreCase))
                .Select(n => f.Roster.Glyph(n)).OfType<char>());
            var l = new MLine(iw).Add(", .", "bold").Add(" rotate ", "grey").Add("+ -", "bold").Add(" zoom ", "grey").Add("c", "bold")
                .Add(st.Iso.Cutaway ? " cut:on " : " cut ", "grey").Add("y", "bold").Add(" labels ", "grey").Add("v", "bold").Add(" view  ", "grey")
                .Add("@", "bold yellow").Add(" me ", "grey");
            if (hive.Length > 0) l.Add(hive, "bold fuchsia").Add(" hive ", "grey");
            l.Add("z s c S", "bold red").Add(" mobs ", "grey").Add("i", "bold yellow").Add(" item", "grey");
            content = new Rows(content, new Markup(l.ToString()));
        }
        return new Panel(content)
        {
            Header = new PanelHeader(" " + Markup.Escape(title) + " "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(nether ? Color.IndianRed : Color.Grey58),
            Padding = new Padding(0, 0, 0, 0),
            Expand = true,
            Width = w,
            Height = h,
        };
    }

    private static Panel ChatPanel(FrameInput f, int w, int h, string dash)
    {
        int iw = w - 2, ih = h - 2;
        var entries = f.Selected.Chat.Select(c => (Key: c.Length >= 8 ? c[..8] : "", Markup: new MLine(iw).Add(c).ToString()))
            .Concat(f.Logs.Select(l => (Key: l.At.ToString("HH:mm:ss"), Markup: LogMarkup(l, iw))))
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => e.Markup)
            .ToList();
        var lines = entries.Skip(Math.Max(0, entries.Count - ih)).ToList();
        return Box($"Chat & log {dash} {f.Selected.Name}", lines, w, h);
    }

    private static string LogMarkup(LogLine l, int iw)
    {
        string style = l.Level switch { >= LogLevel.Error => "red", LogLevel.Warning => "yellow", _ => "grey" };
        string lvl = l.Level switch { >= LogLevel.Error => "ERR", LogLevel.Warning => "WRN", _ => "INF" };
        return new MLine(iw).Add(l.At.ToString("HH:mm:ss") + " " + lvl + " ", "grey").Add(l.Message, style).ToString();
    }

    private static Panel TokensPanel(FrameInput f, int w, int h, string dash)
    {
        int iw = w - 2;
        var u = f.Selected.Usage;
        var lines = new List<string>();
        if (u == null) lines.Add(Markup.Escape("no usage data"));
        else AddUsageLines(lines, u, iw, f.Unicode);
        lines.Add(HiveLine(f, iw));
        return Box($"Tokens {dash} {f.Selected.Name}", lines, w, h);
    }

    /// <summary>The caps, a row per tier, the cache, the last call (and its prompt sections), calls per hour and the latency medians.</summary>
    private static void AddUsageLines(List<string> lines, UsageSnapshot u, int iw, bool unicode)
    {
        int barW = Math.Clamp(iw - 34, 6, 30);
        lines.Add(CapLine("today", u.TodayUsd, u.DailyCapUsd, barW, iw, unicode).Add("  " + u.Mode, ModeStyle(u.Mode)).ToString());
        lines.Add(CapLine("month", u.MonthUsd, u.MonthlyCapUsd, barW, iw, unicode).ToString());
        lines.Add(new MLine(iw).Add($"{"tier",-7}{"today",9}{"calls",7}{"cache",7}{"month",10}", "grey").ToString());
        foreach (var tier in UsageTiers(u))
        {
            var t = u.Today.FirstOrDefault(x => x.Tier == tier);
            var m = u.Month.FirstOrDefault(x => x.Tier == tier);
            if (t != null || m != null) lines.Add(TierLine(tier, t, m, iw));
        }
        var last = u.Last;
        lines.Add(new MLine(iw).Add("cache ", "grey").Add($"today {Percent.Of(u.TodayCacheHit)}").Add(" · last ", "grey")
            .Add(last != null ? Percent.Of(last.CacheHit) : "-").Add(" · ", "grey").Add($"{u.CallsLastHour} calls/h").ToString());
        if (last != null)
        {
            lines.Add(new MLine(iw).Add("last ", "grey").Add(last.Tier + " ", TierStyle(last.Tier)).Add($"{last.Purpose} r{last.Round} ")
                .Add($"{K(last.PromptTokens)}→{last.OutputTokens} ${last.CostUsd:0.0000} {last.LatencyMs / 1000.0:0.0}s ctx {Percent.Of(last.ContextPct)}", "grey").ToString());
            lines.Add(SectionBar(last, iw, unicode));
            lines.Add(SectionLegend(last, iw));
        }
        lines.Add(new MLine(iw).Add("calls/h ", "grey").Add(Sparkline(u.CallsPerHour, unicode), "aqua").Add(" 24h", "grey").ToString());
        // Chat latency p50 (zero-token telemetry): first visible reaction, first game action, first move, model rounds before it.
        if (u.Latency is { Turns: > 0 } lat) lines.Add(new MLine(iw).Add("p50 ", "grey").Add(lat.Line()).ToString());
    }

    private static string TierLine(string tier, TierUsage? t, TierUsage? m, int iw) =>
        new MLine(iw).Add($"{tier,-7}", TierStyle(tier))
            .Add($"{"$" + (t?.Usd ?? 0).ToString("0.000"),9}{t?.Calls ?? 0,7}{Percent.Of(t?.CacheHit ?? 0),7}{"$" + (m?.Usd ?? 0).ToString("0.00"),10}").ToString();

    /// <summary>The whole hive: bodies up, and the spend and calls of those with usage.</summary>
    private static string HiveLine(FrameInput f, int iw)
    {
        var online = f.Bots.Where(b => b.Usage != null).ToList();
        decimal hd = online.Sum(b => b.Usage!.TodayUsd), hm = online.Sum(b => b.Usage!.MonthUsd);
        int hc = online.Sum(b => b.Usage!.CallsLastHour);
        int up = f.Bots.Count(b => b.Status != BotStatus.Offline);
        return new MLine(iw).Add("hive ", "bold").Add($"{up}/{f.Bots.Count} up · today ${hd:0.000} · month ${hm:0.00} · {hc} calls/h").ToString();
    }

    private static IEnumerable<string> UsageTiers(UsageSnapshot u) =>
        new[] { "opus", "worker", "memory" }.Concat(u.Today.Select(t => t.Tier)).Concat(u.Month.Select(t => t.Tier)).Distinct();

    private static MLine CapLine(string label, decimal spent, decimal cap, int barW, int iw, bool unicode)
    {
        double frac = cap > 0 ? (double)(spent / cap) : 0;
        return new MLine(iw).Add($"{label,-6}", "grey").Add($"${spent:0.000}/{cap:0} ".PadRight(14))
            .Add(Bar(frac, barW, unicode), frac >= 0.8 ? "red" : frac >= 0.6 ? "yellow" : "green").Add($" {Percent.Of(frac)}");
    }

    private static Panel BrainPanel(FrameInput f, int w, int h, string dash)
    {
        int iw = w - 2, ih = h - 2;
        var lines = new List<string>();
        foreach (var (text, style, prefix) in BrainEntries(f.Selected.Thought, f.Unicode)) AddWrapped(lines, text, style, prefix, iw);
        int scroll = Math.Clamp(f.State.BrainScroll, 0, Math.Max(0, lines.Count - ih));
        f.State.BrainScroll = scroll;
        var view = lines.Skip(Math.Max(0, lines.Count - ih - scroll)).Take(ih).ToList();
        return Box($"Brain {dash} {f.Selected.Name}" + (scroll > 0 ? $" (scrolled {scroll})" : ""), view, w, h);
    }

    /// <summary>The brain panel's entries (text, style, label) in order; "no turn yet" before the first turn.</summary>
    private static List<(string Text, string? Style, string Prefix)> BrainEntries(ThoughtSnapshot? t, bool unicode)
    {
        var raw = new List<(string Text, string? Style, string Prefix)>();
        if (NoTurnYet(t))
        {
            raw.Add(("no turn yet", "grey", ""));
            return raw;
        }
        if (t.TurnAt is { } at) raw.Add(($"turn {at.ToLocalTime():HH:mm:ss}  {t.Header}", "grey", ""));
        AddIfAny(raw, t.Events, null, "events ");
        AddIfAny(raw, t.Digest, "aqua", "digest ");
        AddIfAny(raw, t.Memory, "green", "memory ");
        AddIfAny(raw, t.Thinking, "italic grey", "think  ");
        AddIfAny(raw, t.Output, "bold", "said   ");
        foreach (var c in t.Tools) raw.Add(ToolEntry(c, unicode));
        raw.Add((t.Task ?? "idle", null, "task   "));
        if (t.Delegate != null) raw.Add((t.Delegate, "teal", "worker "));
        if (t.Journal != null) raw.Add((t.Journal, "grey", "journal"));
        return raw;
    }

    private static bool NoTurnYet([System.Diagnostics.CodeAnalysis.NotNullWhen(false)] ThoughtSnapshot? t) =>
        t == null || t == ThoughtSnapshot.Empty || (t.TurnAt == null && t.Output.Length == 0 && t.Delegate == null);

    private static void AddIfAny(List<(string Text, string? Style, string Prefix)> raw, string text, string? style, string prefix)
    {
        if (text.Length > 0) raw.Add((text, style, prefix));
    }

    /// <summary>One tool call: a mark (running, ok, failed), its name and args, and its result once in.</summary>
    private static (string Text, string? Style, string Prefix) ToolEntry(ThoughtToolCall c, bool unicode)
    {
        string mark = c.Result == null ? (unicode ? "…" : "~") : c.Ok ? (unicode ? "✓" : "+") : (unicode ? "✗" : "x");
        return ($"{mark} {c.Name}({c.Args})" + (c.Result != null ? " → " + c.Result : ""), c.Result == null ? "yellow" : c.Ok ? null : "red", "tool   ");
    }

    /// <summary>An entry wrapped to the panel, its label on the first line and blank under it on the rest.</summary>
    private static void AddWrapped(List<string> lines, string text, string? style, string prefix, int iw)
    {
        var wrapped = Wrap(text.Replace('\n', ' '), Math.Max(10, iw - prefix.Length - (prefix.Length > 0 ? 1 : 0)));
        for (int i = 0; i < wrapped.Count; i++)
        {
            var ml = new MLine(iw);
            if (prefix.Length > 0) ml.Add((i == 0 ? prefix : new string(' ', prefix.Length)) + " ", "grey");
            lines.Add(ml.Add(wrapped[i], style).ToString());
        }
    }

    private static string InputLine(FrameInput f, int iw)
    {
        var st = f.State;
        var l = new MLine(iw);
        if (st.Mode == InputMode.None)
            return l.Add("t", "bold").Add(" chat  ").Add("/", "bold").Add(" command  ").Add("tab [ ]", "bold").Add(" bots  ").Add("1-5", "bold")
                .Add(" jump  ").Add("v", "bold").Add(st.View == ViewMode.Iso ? " view  , . rotate  c cut  " : " view  ").Add("p", "bold").Add(" panel  ").Add("PgUp/PgDn", "bold").Add(" brain  ")
                .Add("q", "bold").Add(" quit", null).ToString();
        string prompt = st.Mode == InputMode.Command ? "/" : "say> ";
        var text = st.Input.ToString();
        int room = iw - prompt.Length - 1;
        if (text.Length > room) text = text[^room..];
        return l.Add(prompt, "bold aqua").Add(text).Add("_", "bold aqua").ToString();
    }

    // ------------------------------------------------------------------ helpers

    private static string CellsMarkup(ViewCell[] row)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < row.Length)
        {
            var style = CellStyle(row[i]);
            int j = i;
            var run = new StringBuilder();
            while (j < row.Length && CellStyle(row[j]) == style) run.Append(row[j++].Glyph);
            var text = Markup.Escape(run.ToString());
            sb.Append(style == null ? text : $"[{style}]{text}[/]");
            i = j;
        }
        return sb.ToString();
    }

    private static readonly string[] PlayerColours = ["aqua", "lime", "fuchsia", "orange1", "deepskyblue1", "hotpink"];

    /// <summary>Each cell kind's style, by <see cref="CellKind"/> value (players take theirs by name: <see cref="CellStyle"/>).</summary>
    private static readonly string?[] KindStyles =
    [
        null, "grey", "olive", "blue", "bold red", "darkorange3", "green", "bold yellow", "silver", "orange3", "white", "bold black on yellow",
        "bold black on fuchsia", null, "bold red", "yellow",
    ];

    internal static string? CellStyle(ViewCell c)
    {
        if (c.Kind == CellKind.Player) return "bold " + PlayerColours[StableHash(c.Name ?? "") % PlayerColours.Length];
        return (uint)c.Kind < (uint)KindStyles.Length ? KindStyles[(int)c.Kind] : null;
    }

    private static readonly (string Key, string Short, string Colour)[] Sections =
    [
        (PromptSection.Tools, "tools", "blue"), (PromptSection.System, "sys", "grey"), (PromptSection.Knowledge, "know", "teal"),
        (PromptSection.Identity, "id", "purple"), (PromptSection.History, "hist", "olive"), (PromptSection.Memory, "mem", "green"),
        (PromptSection.Digest, "dig", "aqua"), (PromptSection.Events, "ev", "yellow"), (PromptSection.TurnTools, "turn", "red"),
    ];

    private static string SectionBar(PromptUsageRecord r, int iw, bool unicode)
    {
        long total = Math.Max(1, r.Sections.Values.Sum());
        var sb = new StringBuilder();
        int used = 0;
        var widths = Sections.Select(s => (s, Width: (int)Math.Round((double)r.Sections.GetValueOrDefault(s.Key) / total * iw))).ToList();
        foreach (var (s, width) in widths)
        {
            int wdt = Math.Min(width, iw - used);
            if (wdt <= 0) continue;
            sb.Append($"[{s.Colour}]").Append(new string(unicode ? '█' : s.Short[0], wdt)).Append("[/]");
            used += wdt;
        }
        return sb.ToString();
    }

    private static string SectionLegend(PromptUsageRecord r, int iw)
    {
        long total = Math.Max(1, r.Sections.Values.Sum());
        var l = new MLine(iw);
        foreach (var s in Sections)
        {
            long v = r.Sections.GetValueOrDefault(s.Key);
            if (v <= 0) continue;
            l.Add(s.Short, s.Colour).Add($" {100.0 * v / total:0}% ");
        }
        return l.ToString();
    }

    public static string Bar(double frac, int width, bool unicode)
    {
        int full = (int)Math.Round(Math.Clamp(frac, 0, 1) * width);
        return new string(unicode ? '█' : '#', full) + new string(unicode ? '░' : '-', width - full);
    }

    public static string Sparkline(IReadOnlyList<int> values, bool unicode)
    {
        if (values.Count == 0) return "";
        string ramp = unicode ? "▁▂▃▄▅▆▇█" : "_.:-=+*#";
        int max = Math.Max(1, values.Max());
        return new string(values.Select(v => v <= 0 ? (unicode ? ' ' : ' ') : ramp[Math.Min(ramp.Length - 1, (int)Math.Ceiling((double)v / max * ramp.Length) - 1)]).ToArray());
    }

    private static string K(long n) => n >= 10_000 ? $"{n / 1000.0:0.#}k" : n.ToString();

    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cur = new StringBuilder();
        foreach (var word in words)
        {
            var wd = word;
            while (wd.Length > width) { if (cur.Length > 0) { lines.Add(cur.ToString()); cur.Clear(); } lines.Add(wd[..width]); wd = wd[width..]; }
            if (cur.Length > 0 && cur.Length + 1 + wd.Length > width) { lines.Add(cur.ToString()); cur.Clear(); }
            if (cur.Length > 0) cur.Append(' ');
            cur.Append(wd);
        }
        if (cur.Length > 0 || lines.Count == 0) lines.Add(cur.ToString());
        return lines;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + ".";

    private static string StatusDot(BotStatus s, bool unicode) => s == BotStatus.Offline ? (unicode ? "○" : "o") : (unicode ? "●" : "*");

    private static string StatusStyle(BotStatus s) => s switch
    {
        BotStatus.Online => "green",
        BotStatus.Connecting => "yellow",
        BotStatus.Dead => "red",
        BotStatus.Paused => "blue",
        _ => "grey",
    };

    private static string? ModeStyle(string mode) => mode switch { "Normal" => "green", "Economy" => "yellow", "CheapOnly" => "darkorange", "Paused" => "red", _ => null };

    private static string? TierStyle(string tier) => tier switch { "opus" => "fuchsia", "worker" => "aqua", "memory" => "green", _ => null };

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (char ch in s.ToLowerInvariant()) h = h * 31 + ch;
            return h & 0x7fffffff;
        }
    }

    /// <summary>Markup line builder that cuts plain text at a fixed width (so markup tags never count).</summary>
    private sealed class MLine(int max)
    {
        private readonly StringBuilder _sb = new();
        private int _len;

        public MLine Add(string text, string? style = null)
        {
            int room = max - _len;
            if (room <= 0 || text.Length == 0) return this;
            if (text.Length > room) text = text[..room];
            _len += text.Length;
            var esc = Markup.Escape(text);
            _sb.Append(style == null ? esc : $"[{style}]{esc}[/]");
            return this;
        }

        public override string ToString() => _sb.ToString();
    }
}
