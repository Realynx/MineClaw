using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using MineClaw.MemoryExplorer.Controls;
using MineClaw.MemoryExplorer.Tests.LiveHive;
using Xunit.Abstractions;

namespace MineClaw.MemoryExplorer.Tests;

/// <summary>
/// Pins what the RAPTOR graph control draws (WP08 complexity refactor): seeded trees in each layout (icicle, radial, node-link)
/// and colouring, driven through a scripted session of selection, search hits, hover (tooltips), keyboard navigation, wheel
/// zoom, drag pan, double-click focus and the minimap, each frame rendered headless (Skia) and hashed.
/// The pixels depend on the machine's default font and time zone (tooltips show local times), so the hashes are compared
/// only where the canary (a line of text in the default font + the time zone) matches the one they were recorded with; elsewhere
/// the test prints the hashes and passes. Set EXPLORER_GOLDEN_OUT to a directory to write the per-step hashes out.
/// </summary>
[Collection("avalonia")]
public sealed class GraphDrawGoldenTests(ITestOutputHelper output)
{
    private static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b))[..20];

    private static GraphModel Model(int seed)
    {
        string[] kinds = ["episode", "gamedata", "recipe", "skill", "routine", "player", "chat", "claim", "structure", "world", "selfnote", "journal", null!];
        var r = new Random(seed * 31 + 7);
        var specs = new List<NodeSpec>();
        int leaves = 12 + r.Next(0, 50), layers = 2 + r.Next(0, 2);
        var below = new List<int>();
        for (int i = 0; i < leaves; i++)
        {
            specs.Add(new NodeSpec(i, 0, [], $"leaf {i} about {kinds[i % 5] ?? "x"} and stone {i * 7}\nline two {new string('x', r.Next(0, 120))}", r.Next(5, 900),
                r.Next(6) == 0, kinds[r.Next(kinds.Length)], null, "activity", r.Next(3) == 0 ? null : $"title {i} mining cobblestone near the gen",
                DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + r.Next(0, 400_000))));
            below.Add(i);
        }
        int next = 1000;
        for (int layer = 1; layer <= layers && below.Count > 1; layer++)
        {
            var above = new List<int>();
            int per = 2 + r.Next(0, 3);
            for (int c = 0; c < below.Count; c += per)
            {
                var kids = below.Skip(c).Take(per).ToList();
                if (r.Next(5) == 0) kids.Add(below[r.Next(below.Count)]);
                specs.Add(new NodeSpec(next, layer, kids, $"summary {next} of the cobblestone generator and the farm", r.Next(50, 400), r.Next(5) == 0,
                    Updated: DateTimeOffset.FromUnixTimeSeconds(1_800_050_000)));
                above.Add(next++);
            }
            below = above;
        }
        var m = GraphModel.FromSpecs(specs, layers);
        TopicLabeler.Label(m);
        return m;
    }

    private static readonly FieldInfo AnimStart = typeof(LayeredGraph).GetField("_animStart", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo To = typeof(LayeredGraph).GetField("_to", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Lets queued input land (a frame is rendered and the dispatcher drained), then ends any view animation, so the
    /// next frame shows its target, as it would 200 ms later.</summary>
    private static void Settle(LayeredGraph g, Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
        AnimStart.SetValue(g, -1.0);
        // The frame rendered above may be mid-animation: its text layouts are cached under a rounded max width, so a
        // time-dependent frame would leak into the next one. Drop them and redraw at the target.
        ((System.Collections.IDictionary)TextCache.GetValue(g)!).Clear();
        g.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
    }

    private static readonly FieldInfo TextCache = typeof(LayeredGraph).GetField("_text", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static string Private(LayeredGraph g)
    {
        var sb = new StringBuilder();
        foreach (var name in new[] { "_hover", "_cursor", "_keyboardTip", "_pinTip", "_pointer", "_press", "_moved", "_dragMinimap", "_minimapShown" })
            sb.Append(' ').Append(name).Append('=').Append(FormattableString.Invariant($"{typeof(LayeredGraph).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(g)}"));
        sb.Append(" focused=").Append(g.IsFocused);
        return sb.ToString();
    }

    private static string Frame(Window w, string? dumpName = null)
    {
        var bmp = w.CaptureRenderedFrame()!;
        using var fb = bmp.Lock();
        var bytes = new byte[fb.RowBytes * fb.Size.Height];
        Marshal.Copy(fb.Address, bytes, 0, bytes.Length);
        var outDir = Environment.GetEnvironmentVariable("EXPLORER_GOLDEN_OUT");
        if (dumpName != null && outDir is { Length: > 0 } && Environment.GetEnvironmentVariable("EXPLORER_GOLDEN_RAW") is { Length: > 0 } pick && dumpName.Contains(pick))
            File.WriteAllBytes(Path.Combine(outDir, dumpName.Replace('/', '_').Replace(' ', '_').Replace(',', '_') + $".{fb.RowBytes}.raw"), bytes);
        return Hash(bytes);
    }

    private static string Canary() => HeadlessUi.Run(() =>
    {
        var w = new Window { Width = 320, Height = 60, Content = new TextBlock { Text = "Canary Ag 0123 · leaves L2 #1001 …", FontSize = 11.5 } };
        w.Show();
        Dispatcher.UIThread.RunJobs();
        var h = Frame(w);
        w.Close();
        return h + " " + TimeZoneInfo.Local.BaseUtcOffset.TotalMinutes;
    }).GetAwaiter().GetResult();

    private static List<(string Name, string Hash)> Session(int seed, GraphLayoutKind kind, GraphColorBy colorBy) => HeadlessUi.Run(() =>
    {
        var rows = new List<(string, string)>();
        var m = Model(seed);
        var g = new LayeredGraph { LayoutKind = kind, ColorBy = colorBy };
        var w = new Window { Width = 900, Height = 560, Content = g };
        w.Show();
        g.Model = m;
        void Shot(string step)
        {
            Settle(g, w);
            var v = (ViewState)To.GetValue(g)!;
            rows.Add(($"{seed}/{kind}/{colorBy}/{step}", Frame(w, $"{seed}/{kind}/{colorBy}/{step}") + FormattableString.Invariant(
                $" sel={g.SelectedIndex} focus={g.FocusIndex} view={v.X0:R},{v.Span:R},{v.Scale:R},{v.PanX:R},{v.PanY:R}") + Private(g)));
        }
        void Press(Key k)
        {
            w.KeyPress(k, RawInputModifiers.None, PhysicalKey.None, null);
            w.KeyRelease(k, RawInputModifiers.None, PhysicalKey.None, null);
        }
        Shot("init");
        var leaf = m.Nodes.First(n => n.IsLeaf && n.Index % 3 == 1).Index;
        g.SelectedIndex = leaf;
        Shot("select-leaf");
        var inner = m.Nodes.First(n => !n.IsLeaf).Index;
        g.SelectedIndex = inner;
        Shot("select-inner");
        g.SearchHits = m.Nodes.Where(n => n.Index % 4 == 0).Select(n => n.Index).ToList();
        Shot("hits");
        foreach (var (x, y) in new[] { (300.0, 60.0), (500.0, 200.0), (450.0, 280.0), (860.0, 470.0), (120.0, 300.0), (700.0, 120.0) })
        {
            w.MouseMove(new Point(x, y), RawInputModifiers.None);
            Shot($"hover {x},{y}");
        }
        g.Focus();
        foreach (var k in new[] { Avalonia.Input.Key.Down, Avalonia.Input.Key.Right, Avalonia.Input.Key.Right, Avalonia.Input.Key.Left, Avalonia.Input.Key.Down,
                     Avalonia.Input.Key.Up, Avalonia.Input.Key.Enter, Avalonia.Input.Key.F, Avalonia.Input.Key.OemPlus, Avalonia.Input.Key.Add,
                     Avalonia.Input.Key.OemMinus, Avalonia.Input.Key.Escape, Avalonia.Input.Key.Escape, Avalonia.Input.Key.Home, Avalonia.Input.Key.Subtract })
        {
            Press(k);
            Shot($"key {k}");
        }
        w.MouseWheel(new Point(500, 220), new Vector(0, 2), RawInputModifiers.None);
        Shot("wheel in");
        w.MouseDown(new Point(500, 220), MouseButton.Left, RawInputModifiers.None);
        w.MouseMove(new Point(440, 250), RawInputModifiers.LeftMouseButton);
        Shot("dragging");
        w.MouseUp(new Point(440, 250), MouseButton.Left, RawInputModifiers.None);
        Shot("dragged");
        w.MouseDown(new Point(830, 548), MouseButton.Left, RawInputModifiers.None); // the minimap (bottom right) when zoomed
        w.MouseMove(new Point(860, 548), RawInputModifiers.LeftMouseButton);
        w.MouseUp(new Point(860, 548), MouseButton.Left, RawInputModifiers.None);
        Shot("minimap");
        w.MouseDown(new Point(450, 120), MouseButton.Left, RawInputModifiers.None);
        w.MouseUp(new Point(450, 120), MouseButton.Left, RawInputModifiers.None);
        Shot("click");
        w.MouseDown(new Point(450, 120), MouseButton.Left, RawInputModifiers.None);
        w.MouseUp(new Point(450, 120), MouseButton.Left, RawInputModifiers.None);
        Shot("double-click");
        w.MouseWheel(new Point(300, 300), new Vector(0, -3), RawInputModifiers.None);
        Shot("wheel out");
        g.PinTooltip(m.Nodes[^1].Index);
        Shot("pin last");
        g.PinTooltip(leaf);
        Shot("pin leaf");
        g.FocusIndex = inner;
        Shot("focus inner");
        g.Reset();
        Shot("reset");
        w.Close();
        return rows;
    }).GetAwaiter().GetResult();

    [Fact]
    public void Graph_frames_match_the_golden_hashes()
    {
        var rows = new List<(string Name, string Hash)> { ("canary", Canary()) };
        foreach (int seed in new[] { 3, 11 })
            foreach (var kind in Enum.GetValues<GraphLayoutKind>())
                foreach (var colorBy in Enum.GetValues<GraphColorBy>())
                    rows.AddRange(Session(seed, kind, colorBy));
        var sb = new StringBuilder();
        foreach (var (n, h) in rows) sb.Append(n).Append(' ').Append(h).Append('\n');
        var outDir = Environment.GetEnvironmentVariable("EXPLORER_GOLDEN_OUT");
        if (outDir is { Length: > 0 }) File.WriteAllText(Path.Combine(outDir, "graph-frames.txt"), sb.ToString());
        string all = Hash(Encoding.UTF8.GetBytes(sb.ToString()));
        output.WriteLine($"canary {rows[0].Hash}\nframes {all} ({rows.Count - 1} frames)");
        if (rows[0].Hash != Canary_) { output.WriteLine("different default font / time zone: frame hashes not compared"); return; }
        Assert.Equal(Golden, all);
    }

    private const string Canary_ = "A9DAF6CC63AC80CEC185 -300";
    private const string Golden = "1282E579C57EDBC6C338";
}
