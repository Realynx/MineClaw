using System.Text;
using MineClaw.Agent.Telemetry;
using MineClaw.App.Hosting;
using MineClaw.App.Viewport;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MineClaw.App.Tui;

/// <summary>Placeholder for a hive member that has no running source yet (shown greyed out).</summary>
public sealed class OfflineBotSource(string name) : IBotTelemetrySource
{
    public string Name => name;
    public BotSnapshot Snapshot(bool full) => BotSnapshot.Offline(name);
    public event Action<PromptUsageRecord>? UsageRecorded { add { } remove { } }
    public Task<bool> SendAsync(string line, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>Renders a frame off-screen with a fixed size (TUI presenter, <c>--frame</c> dumps, tests).</summary>
public static class FrameRenderer
{
    public static string Render(IRenderable frame, int width, int height, bool ansi, ColorSystemSupport colours = ColorSystemSupport.TrueColor, bool unicode = true)
    {
        var sw = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = ansi ? AnsiSupport.Yes : AnsiSupport.No,
            ColorSystem = ansi ? colours : ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(sw),
            Interactive = InteractionSupport.No,
        });
        console.Profile.Width = width;
        console.Profile.Height = height;
        console.Profile.Capabilities.Unicode = unicode;
        console.Write(frame);
        return sw.ToString().Replace("\r\n", "\n").TrimEnd('\n');
    }
}

/// <summary>
/// Full-screen terminal UI over a set of <see cref="IBotTelemetrySource"/>s (index 0 = this process's bot). Spectre.Console
/// builds each frame (Layout + Panels); a small presenter repaints the whole alternate screen in one write at ~4 fps, so
/// resizes (SSH included) are picked up on the next frame. Keys are read with Console.ReadKey on the same loop.
/// </summary>
public sealed class TuiApp
{
    private readonly IReadOnlyList<IBotTelemetrySource> _sources;
    private readonly HiveRoster _roster;
    private readonly RingLoggerProvider? _ring;
    private readonly TuiSettings _settings;
    private readonly Action<string> _quit;
    private readonly ILogger? _log;
    private readonly TuiState _state = new();
    private readonly List<string> _history = new();
    private int _historyAt = -1;
    private bool _stop;

    public TuiApp(IReadOnlyList<IBotTelemetrySource> sources, HiveRoster roster, RingLoggerProvider? ring, TuiSettings settings,
        Action<string> quit, ILogger? log = null, int selected = 0, ViewMode view = ViewMode.Top)
    {
        _sources = sources;
        _roster = roster;
        _ring = ring;
        _settings = settings;
        _quit = quit;
        _log = log;
        _state.Selected = Math.Clamp(selected, 0, Math.Max(0, sources.Count - 1));
        _state.View = view;
    }

    public TuiState State => _state;

    /// <summary>Snapshots + frame for the current state (also used by <c>--frame</c>).</summary>
    public IRenderable BuildFrame(int width, int height, bool unicode)
    {
        var bots = _sources.Select((s, i) => i == _state.Selected ? s.Snapshot(full: true) : s.Snapshot(full: false)).ToList();
        var input = new FrameInput(_state, bots, bots[_state.Selected], _ring?.Lines() ?? [], _roster, unicode, DateTimeOffset.Now);
        return TuiFrame.Build(input, width, height);
    }

    public Task RunAsync(CancellationToken ct) => Task.Factory.StartNew(() => Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        var colours = (ColorSystemSupport)(int)AnsiConsole.Profile.Capabilities.ColorSystem;
        bool unicode = AnsiConsole.Profile.Capabilities.Unicode;
        var stdout = Console.OpenStandardOutput();
        var enc = new UTF8Encoding(false);
        void Write(string s) { var b = enc.GetBytes(s); stdout.Write(b, 0, b.Length); stdout.Flush(); }

        bool treatCtrlC = false;
        try { treatCtrlC = Console.TreatControlCAsInput; Console.TreatControlCAsInput = true; } catch (Exception) { }
        Write("\x1b[?1049h\x1b[?25l\x1b[2J");
        try
        {
            int lastW = -1, lastH = -1;
            var period = TimeSpan.FromSeconds(1.0 / Math.Clamp(_settings.Fps, 0.5, 20));
            var next = DateTime.UtcNow;
            while (!ct.IsCancellationRequested && !_stop)
            {
                bool dirty = false;
                try
                {
                    while (Console.KeyAvailable) { HandleKey(Console.ReadKey(intercept: true)); dirty = true; }
                }
                catch (InvalidOperationException) { }
                int w = SafeWidth(), h = SafeHeight();
                if (w != lastW || h != lastH) { Write("\x1b[2J"); lastW = w; lastH = h; dirty = true; }
                if (_stop) break;
                if (dirty || DateTime.UtcNow >= next)
                {
                    string frame;
                    try
                    {
                        // One column short: writing the last cell of a line can trigger auto-wrap/scroll on some terminals.
                        frame = FrameRenderer.Render(BuildFrame(w - 1, h, unicode), w - 1, h, ansi: true, colours, unicode);
                    }
                    catch (Exception ex)
                    {
                        _log?.LogWarning("tui frame failed: {Error}", ex.Message);
                        frame = "TUI render error: " + ex.Message;
                    }
                    Write("\x1b[H" + frame.Replace("\n", "\x1b[K\r\n") + "\x1b[K\x1b[J");
                    next = DateTime.UtcNow + period;
                }
                Thread.Sleep(30);
            }
        }
        finally
        {
            Write("\x1b[0m\x1b[?25h\x1b[?1049l");
            try { Console.TreatControlCAsInput = treatCtrlC; } catch (Exception) { }
        }
    }

    private static int SafeWidth() { try { return Math.Max(20, Console.WindowWidth); } catch (Exception) { return 120; } }
    private static int SafeHeight() { try { return Math.Max(10, Console.WindowHeight); } catch (Exception) { return 40; } }

    /// <summary>Key handling (public for tests). Typing mode only when t, Enter or / was pressed, so hotkeys never eat chat.</summary>
    public void HandleKey(ConsoleKeyInfo k)
    {
        var st = _state;
        if (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control)) { Quit("ctrl+c"); return; }

        if (st.Mode != InputMode.None)
        {
            switch (k.Key)
            {
                case ConsoleKey.Enter:
                    var text = st.Input.ToString().Trim();
                    var mode = st.Mode;
                    st.Mode = InputMode.None;
                    st.Input.Clear();
                    _historyAt = -1;
                    if (text.Length == 0) return;
                    var line = mode == InputMode.Command ? "/" + text.TrimStart('/') : text;
                    _history.Add(line);
                    if (_history.Count > 30) _history.RemoveAt(0);
                    Send(line);
                    return;
                case ConsoleKey.Escape:
                    st.Mode = InputMode.None;
                    st.Input.Clear();
                    return;
                case ConsoleKey.Backspace:
                    if (st.Input.Length > 0) st.Input.Length--;
                    else if (st.Mode == InputMode.Command) st.Mode = InputMode.None;
                    return;
                case ConsoleKey.UpArrow when _history.Count > 0:
                    _historyAt = _historyAt < 0 ? _history.Count - 1 : Math.Max(0, _historyAt - 1);
                    var h = _history[_historyAt];
                    st.Mode = h.StartsWith('/') ? InputMode.Command : InputMode.Chat;
                    st.Input.Clear().Append(h.StartsWith('/') ? h[1..] : h);
                    return;
                default:
                    if (k.KeyChar != '\0' && !char.IsControl(k.KeyChar) && st.Input.Length < 256) st.Input.Append(k.KeyChar);
                    return;
            }
        }

        switch (k.Key)
        {
            case ConsoleKey.Tab when k.Modifiers.HasFlag(ConsoleModifiers.Shift): Select(st.Selected - 1); return;
            case ConsoleKey.Tab: Select(st.Selected + 1); return;
            case ConsoleKey.Enter: st.Mode = InputMode.Chat; return;
            case ConsoleKey.PageUp: st.BrainScroll += 5; return;
            case ConsoleKey.PageDown: st.BrainScroll = Math.Max(0, st.BrainScroll - 5); return;
            case ConsoleKey.End: st.BrainScroll = 0; return;
            case ConsoleKey.Escape: st.Flash = null; return;
        }
        switch (k.KeyChar)
        {
            case 'q' or 'Q': Quit("q"); return;
            case 't' or 'T': st.Mode = InputMode.Chat; return;
            case '/': st.Mode = InputMode.Command; return;
            case ']': Select(st.Selected + 1); return;
            case '[': Select(st.Selected - 1); return;
            case 'v' or 'V': st.View = st.View == ViewMode.Top ? ViewMode.Side : ViewMode.Top; return;
            case 'p' or 'P': st.NarrowPanel = (st.NarrowPanel + 1) % 3; return;
            case >= '1' and <= '9':
                int i = k.KeyChar - '1';
                if (i < _sources.Count) Select(i);
                return;
        }
    }

    private void Select(int i)
    {
        if (_sources.Count == 0) return;
        _state.Selected = ((i % _sources.Count) + _sources.Count) % _sources.Count;
        _state.BrainScroll = 0;
    }

    private void Quit(string why)
    {
        _stop = true;
        _quit(why);
    }

    private void Send(string line)
    {
        var src = _sources[_state.Selected];
        _ = Task.Run(async () =>
        {
            try
            {
                bool ok = await src.SendAsync(line, CancellationToken.None).ConfigureAwait(false);
                if (!ok) _state.Flash = $"{src.Name} is not reachable";
            }
            catch (Exception ex) { _state.Flash = "send failed: " + ex.Message; }
        });
    }
}
