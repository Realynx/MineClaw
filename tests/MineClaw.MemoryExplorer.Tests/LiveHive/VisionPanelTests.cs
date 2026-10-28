using MineClaw.MemoryExplorer.LiveHive;
using MineClaw.Render;

namespace MineClaw.MemoryExplorer.Tests.LiveHive;

/// <summary>The vision panel: "what the AI saw" (exact bytes, history, answer) vs compose (debounced renders, the see call).</summary>
public class VisionPanelTests
{
    private static readonly BotDto Bot = new("maid_chan_67", "67", false, 0, 5.5, 65, 9.5, 180, 10, 0, 20, 20, "mining", null, null, null, false, 0);

    private sealed class FakeSource : IVisionSource
    {
        public readonly List<VisionSheetDto> Sheets = [];
        public readonly Dictionary<long, byte[]> Pngs = [];
        public readonly List<string> Renders = [];

        public void Add(long id, string mode, string? answer = null, params string[] views)
        {
            Sheets.Insert(0, new VisionSheetDto(id, DateTimeOffset.UtcNow.AddSeconds(-id), "maid_chan_67", "sheet", "see", mode, [.. views], [5, 65, 7], 6,
                "gen?", $$"""{"views":["{{string.Join("\",\"", views)}}"],"focus":"5,65,7"}""", 20, 10, 1, 4, new string('a', 64), "legend " + id, answer));
            Pngs[id] = [(byte)id, 2, 3, 4];
        }

        public Task<VisionListDto> ListAsync(string body, long since, CancellationToken ct) =>
            Task.FromResult(new VisionListDto(DateTimeOffset.UtcNow, body, 20, null, Sheets.Where(s => s.Id > since).ToList()));

        public Task<byte[]> PngAsync(string body, long id, CancellationToken ct) => Task.FromResult(Pngs[id]);

        public Task<SheetPicture> RenderAsync(string body, string seeJson, CancellationToken ct)
        {
            lock (Renders) Renders.Add(seeJson);
            return Task.FromResult(new SheetPicture([9, 9], 40, 30, "server legend", 50, "server"));
        }
    }

    private sealed class FakeComposer : IComposeRenderer
    {
        public readonly List<ComposeSpec> Specs = [];

        public SheetPicture Render(ComposeSpec spec)
        {
            lock (Specs) Specs.Add(spec);
            Thread.Sleep(15);
            return new SheetPicture([7, 7, 7], 30, 20, "local legend", 15, "local");
        }
    }

    private static VisionPanelViewModel Panel(FakeSource src, FakeComposer comp) =>
        new(src, "67", "maid_chan_67 · 67", () => Bot, comp, post: a => a(), decode: _ => null) { Debounce = TimeSpan.FromMilliseconds(40) };

    [Fact]
    public async Task Saw_mode_shows_the_exact_sheets_newest_first_with_their_answer()
    {
        var src = new FakeSource();
        src.Add(1, "perception", "[vision] mine 5,65,7", "eye", "iso yaw=200 pitch=50", "top");
        src.Add(2, "direct", null, "top");
        var vm = Panel(src, new FakeComposer());
        Assert.True(vm.IsSaw);
        await vm.PollOnceAsync();
        Assert.Equal([2L, 1L], vm.History.Select(r => r.Sheet.Id));
        Assert.Same(vm.History[0], vm.Selected);                       // follows the newest
        Assert.Equal(src.Pngs[2], vm.CurrentPng);                       // the server's bytes, never re-rendered
        Assert.Contains("direct mode", vm.AnswerText);
        vm.Selected = vm.History[1];
        Assert.Equal(src.Pngs[1], vm.CurrentPng);
        Assert.Equal("[vision] mine 5,65,7", vm.AnswerText);
        Assert.Contains("iso yaw=200 pitch=50", vm.RequestText);
        Assert.Contains("~1 image tokens", vm.CostText);
        Assert.StartsWith("see {\"views\":[\"eye\",\"iso yaw=200 pitch=50\",\"top\"]", vm.CopyText());
        // A new sheet while the user looks at an older one: it joins the strip, the selection stays.
        src.Add(3, "direct", null, "orbit");
        await vm.PollOnceAsync();
        Assert.Equal(3, vm.History.Count);
        Assert.Equal(1, vm.Selected!.Sheet.Id);
        Assert.Empty(src.Renders);
        // A perception sheet shows up before its answer: the answer fills in on a later poll.
        src.Add(4, "perception", null, "top");
        await vm.PollOnceAsync();
        vm.Selected = vm.History[0];
        Assert.Contains("waiting for the perception answer", vm.AnswerText);
        src.Sheets[0] = src.Sheets[0] with { Answer = "[vision] gen cell 5,65,7" };
        await vm.PollOnceAsync();
        Assert.Equal("[vision] gen cell 5,65,7", vm.AnswerText);
        Assert.Equal(4, vm.History.Count);
    }

    [Fact]
    public async Task Compose_renders_debounced_and_gives_the_see_call_then_switches_back()
    {
        var src = new FakeSource();
        src.Add(1, "direct", null, "top");
        var comp = new FakeComposer();
        var vm = Panel(src, comp);
        await vm.PollOnceAsync();
        vm.ShowComposeCommand.Execute(null);
        Assert.True(vm.IsCompose);
        await vm.WhenComposedAsync();
        Assert.Single(comp.Specs);
        Assert.Equal([7, 7, 7], vm.CurrentPng);
        Assert.Contains("local", vm.ComposeStats);
        // A burst of slider moves renders once, with the final values.
        int before = comp.Specs.Count;
        for (int i = 0; i < 8; i++) vm.IsoYaw = 90 + i * 10;
        vm.IsoPitch = 60;
        vm.TopZoom = 2;
        await vm.WhenComposedAsync();
        Assert.InRange(comp.Specs.Count - before, 1, 2);
        var last = comp.Specs[^1];
        Assert.Contains(last.Views, v => v.Kind == "iso" && v.Params == new CamParams(Yaw: 160, Pitch: 60));
        Assert.Contains(last.Views, v => v.Kind == "top" && v.Params == new CamParams(Zoom: 2));
        Assert.Equal((5, 65, 9), last.Focus);                          // centred on the bot by default
        Assert.Equal("""see {"views":["eye","iso yaw=160 pitch=60","orbit","top zoom=2"],"focus":"5,65,9","radius":6}""", vm.SeeCallText);
        Assert.Equal(vm.SeeCallText, vm.CopyText());
        // A block centre; a bad one renders nothing and says why.
        vm.Target = vm.Targets[1];
        vm.TargetText = "nope";
        Assert.Contains("x,y,z", vm.ComposeStats);
        vm.TargetText = "4,64,6";
        await vm.WhenComposedAsync();
        Assert.Equal((4, 64, 6), comp.Specs[^1].Focus);
        Assert.Equal(new Interest(0, 4, 6, 64, 1500), vm.ComposeInterest());
        // Server render: the same call goes to the server.
        vm.ServerRender = true;
        await vm.WhenComposedAsync();
        Assert.Contains("\"focus\":\"4,64,6\"", src.Renders.Last());
        // Back to what the AI saw: its sheet again, no compose interest.
        vm.ShowSawCommand.Execute(null);
        Assert.True(vm.IsSaw);
        Assert.Equal(src.Pngs[1], vm.CurrentPng);
        Assert.Null(vm.ComposeInterest());
        // Compose from a captured sheet takes its centre, radius, panels and angles.
        src.Add(2, "direct", null, "iso yaw=45 pitch=70", "side axis=E zoom=2");
        await vm.PollOnceAsync();
        vm.ServerRender = false;
        vm.ComposeFromSheetCommand.Execute(null);
        Assert.True(vm.IsCompose);
        Assert.Equal((45.0, 70.0), (vm.IsoYaw, vm.IsoPitch));
        Assert.Equal(("E", 2.0), (vm.SideAxis, vm.SideZoom));
        Assert.False(vm.UseEye);
        Assert.Equal("""see {"views":["iso yaw=45 pitch=70","side zoom=2 axis=E"],"focus":"5,65,7","radius":6}""", vm.SeeCallText);
        vm.Dispose();
    }

    [Fact]
    public void Tile_mode_switches_between_live_feed_server_views_and_the_ai_sheet()
    {
        var tile = new BotTileViewModel(Bot, new HiveWorldCache(), TileMode.All[0]);
        Assert.True(tile.IsEye);
        Assert.False(tile.IsVision);
        tile.Mode = TileMode.All.First(m => m.Key == "iso");
        Assert.True(tile.IsMap);
        tile.Mode = TileMode.All.First(m => m.Key == "vision");
        Assert.True(tile.IsVision);
        Assert.False(tile.IsEye || tile.IsMap);
        Assert.True(tile.VisionEmpty);
        tile.SetVision(new VisionSheetDto(5, DateTimeOffset.UtcNow, "maid_chan_67", "sheet", "see", "perception", ["eye", "top"], null, 6, null, null, 20, 10, 310, 4, "", ""), null);
        Assert.False(tile.VisionEmpty);
        Assert.Contains("sheet → perception · eye, top · ~310 tok", tile.VisionCaption);
        tile.RefreshStale(true, DateTime.UtcNow);
        Assert.False(tile.IsStale);                                     // a sheet is history, never "stale"
        Assert.EndsWith("s ago", tile.VisionAge);
    }
}

/// <summary>The vision panel and the Live Hive page (its tile template with the "AI vision" button) load headless.</summary>
[Collection("avalonia")]
public class VisionPanelViewTests
{
    [Fact]
    public Task Vision_panel_and_live_hive_views_load() => HeadlessUi.Run(() =>
    {
        var bot = new BotDto("maid_chan_67", "67", false, 0, 5.5, 65, 9.5, 180, 10, 0, 20, 20, "mining", null, null, null, false, 0);
        var vm = new VisionPanelViewModel(new NoSource(), "67", "maid_chan_67 · 67", () => bot, post: a => a());
        var view = new VisionPanelView { DataContext = vm };
        var window = new Avalonia.Controls.Window { Width = 1100, Height = 760, Content = view };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        vm.ShowComposeCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsCompose);
        window.Close();
        using var hive = new LiveHiveViewModel();
        var page = new LiveHiveView { DataContext = hive };
        var w2 = new Avalonia.Controls.Window { Width = 1200, Height = 800, Content = page };
        w2.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        w2.Close();
    });

    private sealed class NoSource : IVisionSource
    {
        public Task<VisionListDto> ListAsync(string body, long since, CancellationToken ct) => throw new InvalidOperationException("not polled here");
        public Task<byte[]> PngAsync(string body, long id, CancellationToken ct) => throw new InvalidOperationException();
        public Task<SheetPicture> RenderAsync(string body, string seeJson, CancellationToken ct) => throw new InvalidOperationException();
    }
}

/// <summary>Compose mode's local renderer: the bot's ContactSheet over the synced world (no server round trip per slider move).</summary>
public class LocalComposerTests
{
    [Fact]
    public void Local_compose_renders_the_sheet_and_the_angles_change_it()
    {
        var cache = new HiveWorldCache();
        var floor = Enumerable.Range(0, 16).SelectMany(x => Enumerable.Range(0, 16).Select(z => (x, 63, z, (byte)4))).ToList();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entries = new List<MineClaw.World.ColumnPayloadEntry>();
        for (int cx = -1; cx <= 1; cx++)
            for (int cz = -1; cz <= 1; cz++)
            {
                var blocks = floor.ToList();
                if (cx == 0 && cz == 0) blocks.Add((8, 64, 4, 41)); // a gold block
                entries.Add(LiveHiveDecoderTests.Entry(cx, cz, LiveHiveDecoderTests.Snap([.. blocks]), now));
            }
        cache.Apply(MineClaw.World.ColumnPayload.Encode(0, entries));
        var bot = new BotDto("maid_chan", "main", true, 0, 4.5, 64, 4.5, 180, 10, 0, 20, 20, null, null, null, null, true, 0);
        var composer = new LocalSheetComposer(cache, () => null, () => [bot], () => []);
        SheetPicture Render(params SeeView[] views) => composer.Render(new ComposeSpec(bot, (4, 64, 4), 6, views));
        var a = Render(new SeeView("iso", new CamParams(Yaw: 180, Pitch: 35)));
        var b = Render(new SeeView("iso", new CamParams(Yaw: 0, Pitch: 60)));
        var d = Render(new SeeView("iso"));
        Assert.Equal("local", a.Where);
        Assert.True(a.Width > 300 && a.Height > 200, $"{a.Width}x{a.Height}");
        Assert.Equal(MineClaw.Render.Png.Decode(a.Png).Width, a.Width);
        Assert.NotEqual(Convert.ToHexString(a.Png), Convert.ToHexString(b.Png));
        Assert.NotEqual(Convert.ToHexString(a.Png), Convert.ToHexString(d.Png));
        Assert.Contains("ISO from S looking N (yaw 180), 35 deg down", a.Legend);
        Assert.Contains("ISO from SW looking NE", d.Legend);
        // Single-panel interactivity: report the time (≥ 10 fps is the target; the bound here is loose against load flakes).
        var times = Enumerable.Range(0, 3).Select(i => Render(new SeeView("iso", new CamParams(Yaw: 180 + i * 7))).Ms).ToList();
        Assert.True(times.Min() < 1000, $"single panel {times.Min():0} ms");
        var six = Render(new SeeView("eye"), new SeeView("iso"), new SeeView("orbit"), new SeeView("high"), new SeeView("top"), new SeeView("side"));
        Assert.Contains("[6]", six.Legend);
    }
}
