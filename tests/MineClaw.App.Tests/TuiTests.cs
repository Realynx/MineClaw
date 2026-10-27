using MineClaw.App.Hosting;
using MineClaw.App.Sources;
using MineClaw.App.Tui;
using MineClaw.App.Viewport;

namespace MineClaw.App.Tests;

public class TuiTests
{
    private static readonly string[] Names = ["maid_chan", "maid_chan_67", "maid_chan_69", "maid_chan_420", "maid_chan_1337"];

    private static (TuiApp App, DemoHive Demo, List<string> Quit) Demo(int online = 3)
    {
        var roster = new HiveRoster(Names);
        var demo = new DemoHive(roster.Names, online);
        for (int i = 0; i < 40; i++) demo.Tick();
        var quit = new List<string>();
        var app = new TuiApp(demo.Sources, roster, new RingLoggerProvider(), new TuiSettings(), quit.Add);
        return (app, demo, quit);
    }

    private static string[] Frame(TuiApp app, int w, int h) =>
        FrameRenderer.Render(app.BuildFrame(w, h, unicode: true), w, h, ansi: false).Split('\n');

    private static ConsoleKeyInfo Key(char c) => new(c, ConsoleKey.NoName, false, false, false);
    private static ConsoleKeyInfo Key(ConsoleKey k, bool shift = false, bool ctrl = false) => new('\0', k, shift, false, ctrl);

    [Theory]
    [InlineData(140, 45)]
    [InlineData(120, 40)]
    [InlineData(80, 24)]   // narrow layout
    [InlineData(50, 16)]   // minimum
    public void Frame_FillsTheTerminalExactly(int w, int h)
    {
        var (app, demo, _) = Demo();
        using var _d = demo;
        var lines = Frame(app, w, h);
        Assert.Equal(h, lines.Length);
        Assert.All(lines, l => Assert.True(l.Length <= w, $"line longer than {w}: {l.Length}"));
        var text = string.Join("\n", lines);
        Assert.Contains("MineClaw — maid_chan", text);
        Assert.Contains("Viewport — maid_chan (@ ", text);
    }

    [Fact]
    public void Frame_TooSmall_SaysSo()
    {
        var (app, demo, _) = Demo();
        using var _d = demo;
        Assert.Contains("terminal too small", string.Join("\n", Frame(app, 40, 12)));
    }

    [Fact]
    public void Wide_ShowsAllPanelsWithBotTitles_HiveStripAndTotals()
    {
        var (app, demo, _) = Demo();
        using var _d = demo;
        var text = string.Join("\n", Frame(app, 140, 45));
        foreach (var title in new[] { "Viewport — maid_chan (@", "Tokens — maid_chan", "Brain — maid_chan", "Chat & log — maid_chan", "Input — to maid_chan" })
            Assert.Contains(title, text);
        Assert.Contains("2 maid_chan_67 ●", text);
        Assert.Contains("4 maid_chan_420 ○ offline", text); // not spawned in a 3-bot demo
        Assert.Contains("hive 3/5 up", text);
        Assert.Contains("calls/h", text);
        Assert.Matches(@"tools \d+%", text); // last call's section legend
    }

    [Fact]
    public void Keys_SwitchBots_ToggleView_AndTypingNeverTriggersHotkeys()
    {
        var (app, demo, quit) = Demo();
        using var _d = demo;
        app.HandleKey(Key('3'));
        Assert.Equal(2, app.State.Selected);
        var text = string.Join("\n", Frame(app, 140, 45));
        Assert.Contains("Viewport — maid_chan_69 (@ ", text);
        Assert.Contains("Tokens — maid_chan_69", text);
        Assert.Contains("Brain — maid_chan_69", text);

        app.HandleKey(Key(']'));
        Assert.Equal(3, app.State.Selected);
        Assert.Contains("offline / not spawned", string.Join("\n", Frame(app, 140, 45)));
        app.HandleKey(Key(ConsoleKey.Tab));
        app.HandleKey(Key(ConsoleKey.Tab));
        Assert.Equal(0, app.State.Selected); // wraps
        app.HandleKey(Key('['));
        Assert.Equal(4, app.State.Selected);
        app.HandleKey(Key(ConsoleKey.Tab, shift: true));
        Assert.Equal(3, app.State.Selected);
        app.HandleKey(Key('9')); // no 9th bot: ignored
        Assert.Equal(3, app.State.Selected);

        app.HandleKey(Key('v'));
        Assert.Equal(ViewMode.Side, app.State.View);
        app.HandleKey(Key('v'));
        Assert.Equal(ViewMode.Iso, app.State.View); // v cycles top → side → iso
        app.HandleKey(Key('v'));
        Assert.Equal(ViewMode.Top, app.State.View);

        app.HandleKey(Key('1'));
        app.HandleKey(Key('t'));
        Assert.Equal(InputMode.Chat, app.State.Mode);
        foreach (var c in "q v 2") app.HandleKey(Key(c));
        Assert.Equal("q v 2", app.State.Input.ToString());
        Assert.Empty(quit);
        Assert.Equal(0, app.State.Selected);
        app.HandleKey(Key(ConsoleKey.Enter));
        Assert.Equal(InputMode.None, app.State.Mode);
        Assert.Equal(0, app.State.Input.Length);

        app.HandleKey(Key('q'));
        Assert.Equal(["q"], quit);
    }

    [Fact]
    public void CtrlC_QuitsEvenWhileTyping()
    {
        var (app, demo, quit) = Demo();
        using var _d = demo;
        app.HandleKey(Key('/'));
        Assert.Equal(InputMode.Command, app.State.Mode);
        app.HandleKey(new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, true));
        Assert.Equal(["ctrl+c"], quit);
    }

    [Fact]
    public void Viewport_OverlaysOtherHiveBotsAsDigits()
    {
        var (app, demo, _) = Demo();
        using var _d = demo;
        // From maid_chan's point of view, the clones 67 and 69 are players in the world.
        var snap = demo.Sources[0].Snapshot(full: true);
        Assert.NotNull(snap.Viewport);
        var roster = new HiveRoster(Names);
        var rows = ViewportRenderer.RenderPlain(snap.Viewport!, ViewMode.Top, 25, 25, roster.Glyph);
        var all = string.Concat(rows);
        Assert.Contains('@', all);
        Assert.Contains('6', all);
        Assert.Contains('9', all);
        Assert.DoesNotContain('M', all); // the main bot is '@' in its own view
        var fromClone = ViewportRenderer.RenderPlain(demo.Sources[1].Snapshot(full: true).Viewport!, ViewMode.Top, 25, 25, roster.Glyph);
        Assert.Contains('M', string.Concat(fromClone));
    }
}
