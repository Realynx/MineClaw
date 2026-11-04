using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Hosting;
using MineClaw.App.Sources;
using MineClaw.App.Tui;
using MineClaw.App.Viewport;
using Microsoft.Extensions.Logging;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="TuiFrame.Build"/> (WP07): whole frames (plain text, plus a hash of the ANSI-styled one) over the wide and narrow
/// layouts, each view mode (top, side, iso with rotation/zoom/cutaway, iso without voxels), unicode and ascii, an offline bot, a bot
/// with no world data, the nether, a rich brain panel (every section, tool marks, wrapping, scrolling), empty usage, flash, chat and
/// command input. The demo's wall-clock parts are pinned or masked: each bot's task is fixed and every HH:mm:ss reads ##:##:##. The test
/// keeps the dump's line count and SHA-256 (WP07_GOLDEN_OUT writes the dump for a diff).
/// </summary>
public class TuiFrameGoldenTests
{
    private static readonly string[] Names = ["maid_chan", "maid_chan_67", "maid_chan_69", "maid_chan_420"];
    private static readonly Regex Clock = new(@"\d\d:\d\d:\d\d", RegexOptions.Compiled);

    private static string Mask(string s) => Clock.Replace(s, "##:##:##");

    private static ThoughtSnapshot RichThought() => new()
    {
        TurnAt = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero),
        Header = "pos 1,64,2 hp 20 food 18",
        Events = "[since last turn] kiwi placed 4 torches near the bridge and\nsaid hi; a very_long_unbroken_word_that_wraps_on_its_own_because_it_is_too_long_for_any_line",
        Digest = "players asked for cobble",
        Memory = "#12 chest by the tree: cobblestone x210",
        Thinking = "should top up the chest first, then the saplings",
        Output = "on it!",
        Tools =
        [
            new ThoughtToolCall(default, "delegate", "task=mine 16 cobble"),
            new ThoughtToolCall(default, "goto", "x=1 z=2", "ok: there", true),
            new ThoughtToolCall(default, "dig", "x=3", "no: out of reach", false),
        ],
        Task = "mine cobble",
        Delegate = "#4 31s mining",
        Journal = "topped up the chest",
    };

    private static BotSnapshot Fixed(BotSnapshot b, int i) => b with { Task = b.Status == BotStatus.Offline ? null : i % 2 == 0 ? "delegate #4 31s" : null };

    private static List<BotSnapshot> Snapshots(DemoHive demo, int selected) =>
        demo.Sources.Select((s, i) => Fixed(s.Snapshot(full: i == selected), i)).ToList();

    private static void Frame(StringBuilder sb, string label, FrameInput f, int w, int h)
    {
        var frame = TuiFrame.Build(f, w, h);
        var plain = Mask(FrameRenderer.Render(frame, w, h, ansi: false, unicode: f.Unicode));
        var ansi = Mask(FrameRenderer.Render(TuiFrame.Build(f, w, h), w, h, ansi: true, unicode: f.Unicode));
        sb.Append("== ").Append(label).Append(' ').Append(w).Append('x').Append(h)
            .Append(" ansi ").Append(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ansi)))[..16]).Append('\n');
        sb.Append(plain).Append('\n');
    }

    internal static string Dump()
    {
        var roster = new HiveRoster(Names);
        using var demo = new DemoHive(roster.Names, 3);
        for (int i = 0; i < 40; i++) demo.Tick();
        var sb = new StringBuilder();
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        var logs = new List<LogLine>
        {
            new(now, LogLevel.Information, "x", "hive started"),
            new(now.AddSeconds(1), LogLevel.Warning, "x", "slow tick [1200 ms]"),
            new(now.AddSeconds(2), LogLevel.Error, "x", "lost connection"),
        };

        FrameInput Input(TuiState st, IReadOnlyList<BotSnapshot> bots, bool unicode = true, IReadOnlyList<LogLine>? l = null) =>
            new(st, bots, bots[st.Selected], l ?? [], roster, unicode, now);

        foreach (var unicode in new[] { true, false })
        {
            foreach (var view in new[] { ViewMode.Top, ViewMode.Side, ViewMode.Iso })
            {
                var st = new TuiState { View = view };
                Frame(sb, $"demo {view} unicode={unicode}", Input(st, Snapshots(demo, 0), unicode), 140, 45);
            }
            var narrow = new TuiState();
            for (int p = 0; p < 3; p++)
            {
                narrow.NarrowPanel = p;
                Frame(sb, $"narrow panel {p} unicode={unicode}", Input(narrow, Snapshots(demo, 0), unicode), 80, 24);
            }
            var iso = new TuiState { View = ViewMode.Iso };
            iso.Iso.Rotation = 1; iso.Iso.Zoom = 2; iso.Iso.Cutaway = true; iso.Iso.Labels = true;
            Frame(sb, $"iso rot1 zoom2 cut unicode={unicode}", Input(iso, Snapshots(demo, 1), unicode), 120, 40);
            iso.Iso.Rotation = 3; iso.Iso.Zoom = 0; iso.Iso.Cutaway = false;
            Frame(sb, $"iso rot3 zoom0 unicode={unicode}", Input(iso, Snapshots(demo, 1), unicode), 100, 30);
        }

        // An offline bot selected; then iso asked for with no voxels (falls back to top).
        var off = new TuiState { Selected = 3 };
        Frame(sb, "offline selected", Input(off, Snapshots(demo, 3)), 140, 45);

        var bots = Snapshots(demo, 0);
        var noVox = new TuiState { View = ViewMode.Iso };
        bots[0] = bots[0] with { Voxels = null };
        Frame(sb, "iso without voxels", Input(noVox, bots), 140, 45);

        // A rich brain, the nether, flash, asleep brain, low hp/food, logs, chat input, scrolling.
        var rich = Snapshots(demo, 0);
        rich[0] = rich[0] with
        {
            Thought = RichThought(), Dimension = -1, Hp = 5, Food = 4, Brain = "asleep (script cobble_loop 34%)", BudgetMode = "Economy",
            Chat = ["12:00:00 <kiwi> hi", "12:00:03 <poofimafox> [bold]not markup[/]"],
        };
        rich[1] = rich[1] with { Status = BotStatus.Dead, Brain = "awake", BudgetMode = "CheapOnly" };
        rich[2] = rich[2] with { Status = BotStatus.Paused, Usage = null, BudgetMode = "Paused" };
        foreach (var scroll in new[] { 0, 3, 999 })
        {
            var st = new TuiState { BrainScroll = scroll, Flash = "saved [x]", Mode = InputMode.Chat };
            st.Input.Append("hello there [markup] ").Append('w', 200);
            Frame(sb, $"rich scroll={scroll}", Input(st, rich, true, logs), 140, 45);
            sb.Append("scroll after: ").Append(st.BrainScroll).Append('\n');
        }
        foreach (var scroll in new[] { 0, 3, 999 })
        {
            var cmd = new TuiState { Mode = InputMode.Command, NarrowPanel = 2, BrainScroll = scroll };
            cmd.Input.Append("say x");
            Frame(sb, $"rich narrow brain, command, scroll={scroll}", Input(cmd, rich, false, logs), 60, 20);
            sb.Append("scroll after: ").Append(cmd.BrainScroll).Append('\n');
        }

        // No turn yet (empty thought, null thought), no viewport, no usage, connecting.
        var bare = Snapshots(demo, 0);
        bare[0] = bare[0] with { Thought = ThoughtSnapshot.Empty, Viewport = null, Usage = null, Status = BotStatus.Connecting, Pos = null };
        bare[1] = bare[1] with { Thought = null };
        Frame(sb, "bare", Input(new TuiState(), bare), 140, 45);
        Frame(sb, "bare null thought", Input(new TuiState { Selected = 1 }, bare), 100, 24);
        var turnOnly = Snapshots(demo, 0);
        turnOnly[0] = turnOnly[0] with { Thought = new ThoughtSnapshot { Delegate = "d" } };
        Frame(sb, "delegate only", Input(new TuiState { NarrowPanel = 2 }, turnOnly), 50, 16);
        Frame(sb, "too small", Input(new TuiState(), turnOnly), 49, 16);
        return sb.ToString();
    }

    [Fact]
    public void Frames_match_the_golden_hash()
    {
        var dump = Dump().ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP07_GOLDEN_OUT") is { Length: > 0 } dir) File.WriteAllText(Path.Combine(dir, "tui-frames.txt"), dump);
        var lines = dump.Count(c => c == '\n');
        Assert.Equal(TuiFrameGoldenData.Expected, $"{lines} {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dump)))}");
    }
}
