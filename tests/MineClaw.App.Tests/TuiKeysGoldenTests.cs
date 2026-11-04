using System.Text;
using MineClaw.App.Hosting;
using MineClaw.App.Sources;
using MineClaw.App.Tui;
using MineClaw.App.Viewport;

namespace MineClaw.App.Tests;

/// <summary>
/// Pins <see cref="TuiApp.HandleKey"/> (WP07): a long key sequence through every hotkey (both cases), typing mode (chat and
/// command, backspace, escape, history, the 256-char cap, control characters), with the state after each key.
/// </summary>
public class TuiKeysGoldenTests
{
    private static ConsoleKeyInfo Key(char c) => new(c, ConsoleKey.NoName, false, false, false);
    private static ConsoleKeyInfo Key(ConsoleKey k, bool shift = false, bool ctrl = false, char c = '\0') => new(c, k, shift, false, ctrl);

    private static IEnumerable<(string Label, ConsoleKeyInfo Key)> Keys()
    {
        foreach (var c in "123459][[]]vVvVvV,<.>.>+=+=+-_-_-cCyYpPpPtT")
        {
            yield return (c.ToString(), Key(c));
            if (c is 't' or 'T') yield return ("esc", Key(ConsoleKey.Escape));
        }
        yield return ("tab", Key(ConsoleKey.Tab));
        yield return ("shift+tab", Key(ConsoleKey.Tab, shift: true));
        yield return ("shift+tab", Key(ConsoleKey.Tab, shift: true));
        yield return ("pgup", Key(ConsoleKey.PageUp));
        yield return ("pgup", Key(ConsoleKey.PageUp));
        yield return ("pgdn", Key(ConsoleKey.PageDown));
        yield return ("pgdn", Key(ConsoleKey.PageDown));
        yield return ("pgdn", Key(ConsoleKey.PageDown));
        yield return ("pgup", Key(ConsoleKey.PageUp));
        yield return ("end", Key(ConsoleKey.End));
        yield return ("esc", Key(ConsoleKey.Escape));
        yield return ("x", Key('x'));
        yield return ("ctrl+a", Key(ConsoleKey.A, ctrl: true, c: '\u0001'));
        // Chat typing.
        yield return ("enter", Key(ConsoleKey.Enter));
        foreach (var c in "hi q/[]1") yield return (c.ToString(), Key(c));
        yield return ("ctrl char", Key(ConsoleKey.NoName, c: '\u0007'));
        yield return ("bs", Key(ConsoleKey.Backspace));
        yield return ("enter", Key(ConsoleKey.Enter));
        // Command typing: '/' then text; backspace past the start leaves command mode.
        yield return ("/", Key('/'));
        foreach (var c in "say x") yield return (c.ToString(), Key(c));
        yield return ("enter", Key(ConsoleKey.Enter));
        yield return ("/", Key('/'));
        yield return ("a", Key('a'));
        yield return ("bs", Key(ConsoleKey.Backspace));
        yield return ("bs", Key(ConsoleKey.Backspace));
        yield return ("t", Key('t'));
        yield return ("enter (empty)", Key(ConsoleKey.Enter));
        // History: up walks back, stops at the oldest; esc leaves.
        yield return ("t", Key('t'));
        yield return ("up", Key(ConsoleKey.UpArrow));
        yield return ("up", Key(ConsoleKey.UpArrow));
        yield return ("up", Key(ConsoleKey.UpArrow));
        yield return ("down", Key(ConsoleKey.DownArrow, c: '\0'));
        yield return ("esc", Key(ConsoleKey.Escape));
        yield return ("/", Key('/'));
        yield return ("/", Key('/'));
        yield return ("enter", Key(ConsoleKey.Enter));
        yield return ("t", Key('t'));
        for (int i = 0; i < 260; i++) yield return ("w", Key('w'));
        yield return ("esc", Key(ConsoleKey.Escape));
        yield return ("q", Key('q'));
        yield return ("Q", Key('Q'));
        yield return ("ctrl+c", Key(ConsoleKey.C, ctrl: true, c: '\u0003'));
        yield return ("t", Key('t'));
        yield return ("ctrl+c typing", Key(ConsoleKey.C, ctrl: true, c: '\u0003'));
    }

    internal static string Dump()
    {
        var names = new[] { "maid_chan", "maid_chan_67", "maid_chan_69", "maid_chan_420" };
        var roster = new HiveRoster(names);
        var demo = new DemoHive(roster.Names, 3);
        for (int i = 0; i < 5; i++) demo.Tick();
        var quit = new List<string>();
        var app = new TuiApp(demo.Sources, roster, new RingLoggerProvider(), new TuiSettings(), quit.Add);
        var sb = new StringBuilder();
        string? last = null;
        foreach (var (label, key) in Keys())
        {
            app.HandleKey(key);
            var s = app.State;
            // The 260 w's print only near the 256-char cap; a long input prints as its length.
            if (label == "w" && s.Input.Length is > 3 and < 254) continue;
            var input = s.Input.Length > 20 ? $"{s.Input.Length} chars" : s.Input.ToString();
            var line = $"sel={s.Selected} view={s.View} mode={s.Mode} input=[{input}] scroll={s.BrainScroll} panel={s.NarrowPanel} flash={s.Flash ?? "-"} " +
                $"iso={s.Iso.Rotation}/{s.Iso.Zoom}/{s.Iso.Cutaway}/{s.Iso.Labels} quit=[{string.Join(",", quit)}]";
            // A key that changes nothing prints nothing.
            if (line == last) continue;
            sb.Append(label).Append(" -> ").Append(line).Append('\n');
            last = line;
        }
        return sb.ToString();
    }

    [Fact]
    public void Keys_match_the_golden_dump() => RouteGolden.Check("tui-keys", TuiKeysGoldenData.Expected, Dump());
}
