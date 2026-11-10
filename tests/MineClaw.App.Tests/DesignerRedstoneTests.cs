using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using MineClaw.App.Supervisor;
using MineClaw.Bot.Blueprints;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>
/// The Design Studio's redstone designs on the bot's side: a circuit is drawn on a build plot (the routes take the kind, the
/// session keeps it, build ↔ redstone keeps every cell), a saved one is an ordinary design tagged by the comment line
/// <c># kind redstone</c> (read on load, reported per revision, kept by every redraw), its editor data is stored per revision
/// behind a DSL marker, and every redstone part of both palettes survives save and reload at every meta the canvas accepts.
/// </summary>
[Collection(GameDataDefaultCollection.Name)]
public class DesignerRedstoneTests
{
    private const string Token = "s3cret-token";
    private const string Fox = "poofimafox";
    private const int Stone = 1, Dust = 55, Lever = 69, RedstoneTorch = 76, Repeater = 93, Comparator = 149;

    /// <summary>Nothing here talks to a model.</summary>
    private sealed class NoModel : ILlmClient
    {
        public Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct) => throw new InvalidOperationException("no model call expected");
    }

    private static BlueprintService Designs(AgentDb db, bool history = true)
    {
        var s = new BlueprintService(new BlueprintOptions(), new BlueprintStore(db), new BlueprintPreview())
        {
            Site = () => throw new InvalidOperationException("no site in tests"), Crew = () => throw new InvalidOperationException("no body in tests"),
            ResolveSite = (_, _) => (null, "n/a"), Trusted = () => [Fox],
        };
        if (history) s.History = new DesignHistory(s, new DesignRevisions(db));
        return s;
    }

    private static SupervisorServer Server(out BlueprintService designs, bool history = true)
    {
        var db = AgentDb.Open(null);
        var s = designs = Designs(db, history);
        var o = new SupervisorOptions { Token = Token, Viewer = new SupervisorViewerOptions { Enabled = true, Designer = new DesignerOptions { Enabled = true } } };
        var routes = new DesignerRoutes(o, () => s, new DesignerStore(db), new NoModel(), new LlmOptions { ApiKey = "sk-test" }, null, () => "maid_chan");
        return new SupervisorServer(o, new FakeSupervisorData()) { Designer = routes };
    }

    private static SupervisorRequest Req(string method, string path, object? body = null) =>
        new(method, path, new Dictionary<string, string>(), Token, body == null ? null : JsonSerializer.Serialize(body));

    private static JsonElement J(SupervisorReply r)
    {
        Assert.True(r.Status == 200, $"{r.Status}: {r.Text}");
        return JsonDocument.Parse(r.Text).RootElement.Clone();
    }

    private static async Task<JsonElement> Post(SupervisorServer server, string path, object body) =>
        J(await server.HandleAsync(Req("POST", "/api/designer/" + path, body)));

    private static async Task<JsonElement> Session(SupervisorServer server, long id) => J(await server.HandleAsync(Req("GET", $"/api/designer/sessions/{id}")));

    private static Task<JsonElement> Open(SupervisorServer server, long design, int? rev = null) =>
        Post(server, "sessions", rev is { } r ? new { design_id = design, rev = r, player = Fox } : (object)new { design_id = design, player = Fox });

    private static int[] Cell(int x, int y, int z, int id, int meta) => [x, y, z, id, meta];

    private static Task<JsonElement> Place(SupervisorServer server, long id, params int[][] cells) =>
        Post(server, $"sessions/{id}/canvas", new { rev = 0, ops = new[] { new { kind = "place", label = "parts", cells } } });

    private static List<(int X, int Y, int Z, int Id, int Meta)> BlocksOf(JsonElement session) =>
        session.GetProperty("blocks").EnumerateArray().Select(b => (b[0].GetInt32(), b[1].GetInt32(), b[2].GetInt32(), b[3].GetInt32(), b[4].GetInt32())).ToList();

    /// <summary>The block states of a session whatever their place (a design opens centred on the plot).</summary>
    private static List<(int Id, int Meta)> StatesOf(JsonElement session) => BlocksOf(session).Select(b => (b.Id, b.Meta)).OrderBy(b => b).ToList();

    private static string? Kind(JsonElement session) => session.GetProperty("kind").GetString();

    private static string? RedstoneAuthoring(JsonElement session) => session.GetProperty("redstone_authoring").GetString();

    /// <summary>A circuit's editor data as the studio writes it (the bot only checks the mark and carries the text).</summary>
    private static string Authoring(string label) => JsonSerializer.Serialize(new
    {
        redstone = 1,
        components = new[] { new { id = "c1", def = "gate.xor", at = new[] { 10, 0, 12 }, turns = 1, mirror = (string?)null, label } },
        customDefs = Array.Empty<object>(), probes = Array.Empty<object>(), breakpoints = Array.Empty<object>(), levels = Array.Empty<object>(),
        sim = new { speed = 10, displayHz = 10 },
    });

    // ------------------------------------------------------------------ the kind on a session

    [Fact]
    public async Task A_redstone_session_is_a_build_plot_and_keeps_its_kind()
    {
        var server = Server(out _);
        var s = await Post(server, "sessions", new { player = Fox, kind = "redstone", version = "1.12.2", seed = "777" });
        long id = s.GetProperty("id").GetInt64();
        Assert.Equal("redstone", Kind(s));
        Assert.Equal("new design", s.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, s.GetProperty("seed").ValueKind); // a seed is a mine's terrain
        Assert.Equal(JsonValueKind.Null, s.GetProperty("redstone_authoring").ValueKind);
        var plot = s.GetProperty("plot");
        Assert.Equal((64, 48, 16), (plot.GetProperty("size").GetInt32(), plot.GetProperty("height").GetInt32(), plot.GetProperty("depth").GetInt32()));
        // Cells go where a build's go: on the grass and dug under it, never at a mine's height.
        var put = await Place(server, id, Cell(5, 0, 5, Dust, 0), Cell(5, -1, 5, 0, 0), Cell(5, 100, 5, Stone, 0));
        Assert.Equal(2, put.GetProperty("applied").GetInt32());
        // Read back from the store: the kind is the session's own, not a default.
        var again = await Session(server, id);
        Assert.Equal("redstone", Kind(again));
        Assert.Equal([(5, 0, 5, Dust, 0)], BlocksOf(again));
        Assert.Equal(1, again.GetProperty("dug").GetArrayLength());
        // Any other word is still refused, and the answer names the three kinds.
        var bad = await server.HandleAsync(Req("POST", "/api/designer/sessions", new { kind = "circuit" }));
        Assert.Equal(400, bad.Status);
        Assert.Contains("use build, mine or redstone", bad.Text);
    }

    [Fact]
    public void A_stored_canvas_keeps_the_kinds_this_code_knows_and_opens_any_other_as_a_build()
    {
        var c = new DesignerCanvas { Kind = DesignerCanvas.KindRedstone };
        c.Apply([new CanvasCell(5, 0, 5, Dust, 0), new CanvasCell(5, -1, 5, 0, 0)]);
        var back = DesignerCanvas.Decode(c.Encode(), c.Rev, DesignerCanvas.KindRedstone);
        Assert.Equal(DesignerCanvas.KindRedstone, back.Kind);
        Assert.False(back.Mine);
        Assert.Equal((-DesignerCanvas.Depth, DesignerCanvas.Height - 1), back.YRange);
        Assert.Equal(c.Blocks.ToDictionary(), back.Blocks.ToDictionary());
        Assert.Equal(c.Dug.ToHashSet(), back.Dug.ToHashSet());
        Assert.Equal(DesignerCanvas.KindBuild, DesignerCanvas.Decode(c.Encode(), c.Rev, "hologram").Kind);
        Assert.Equal(DesignerCanvas.KindBuild, DesignerCanvas.Decode(c.Encode(), c.Rev).Kind);
        Assert.True(DesignerCanvas.SamePlot(DesignerCanvas.KindBuild, DesignerCanvas.KindRedstone));
        Assert.False(DesignerCanvas.SamePlot(DesignerCanvas.KindRedstone, DesignerCanvas.KindMine));
    }

    [Fact]
    public async Task Build_and_redstone_swap_at_once_and_keep_every_cell()
    {
        var server = Server(out _);
        var s = await Post(server, "sessions", new { player = Fox });
        long id = s.GetProperty("id").GetInt64();
        await Post(server, $"sessions/{id}/canvas", new
        {
            rev = 0, ops = new[] { new { kind = "place", label = "wire", cells = new[] { Cell(5, 0, 5, Dust, 0), Cell(5, -1, 5, 0, 0) } } }, anchor = new[] { 5, 0, 5 },
        });
        int rev = (await Session(server, id)).GetProperty("rev").GetInt32();

        // A full canvas, and no question asked: nothing is cleared.
        var there = await Post(server, $"sessions/{id}/version", new { version = "b1.8.1", kind = "redstone" });
        Assert.True(there.GetProperty("changed").GetBoolean());
        Assert.False(there.GetProperty("needs_clear").GetBoolean());
        var circuit = there.GetProperty("session");
        Assert.Equal("redstone", Kind(circuit));
        Assert.Equal([(5, 0, 5, Dust, 0)], BlocksOf(circuit));
        Assert.Equal([5, -1, 5], circuit.GetProperty("dug")[0].EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal([5, 0, 5], circuit.GetProperty("anchor").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal(rev, circuit.GetProperty("rev").GetInt32()); // no cell changed
        Assert.Equal(64, circuit.GetProperty("plot").GetProperty("size").GetInt32());
        Assert.Equal("redstone", Kind(await Session(server, id)));

        var back = await Post(server, $"sessions/{id}/version", new { version = "b1.8.1", kind = "build" });
        Assert.True(back.GetProperty("changed").GetBoolean());
        Assert.Equal("build", Kind(back.GetProperty("session")));
        Assert.Equal([(5, 0, 5, Dust, 0)], BlocksOf(back.GetProperty("session")));
        Assert.Equal(1, back.GetProperty("session").GetProperty("dug").GetArrayLength());
        Assert.False((await Post(server, $"sessions/{id}/version", new { version = "b1.8.1", kind = "build" })).GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task To_or_from_a_mine_a_full_canvas_is_still_asked_about_and_cleared()
    {
        var server = Server(out _);
        var s = await Post(server, "sessions", new { player = Fox, kind = "redstone" });
        long id = s.GetProperty("id").GetInt64();
        await Place(server, id, Cell(5, 0, 5, Dust, 0));
        var ask = await Post(server, $"sessions/{id}/version", new { version = "b1.8.1", kind = "mine" });
        Assert.False(ask.GetProperty("changed").GetBoolean());
        Assert.True(ask.GetProperty("needs_clear").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ask.GetProperty("session").ValueKind);
        var kept = await Session(server, id);
        Assert.Equal("redstone", Kind(kept));
        Assert.Single(BlocksOf(kept));

        var mine = (await Post(server, $"sessions/{id}/version", new { version = "b1.8.1", kind = "mine", seed = "7", apply = true })).GetProperty("session");
        Assert.Equal("mine", Kind(mine));
        Assert.Equal("7", mine.GetProperty("seed").GetString());
        Assert.Empty(BlocksOf(mine));
        Assert.Equal(256, mine.GetProperty("plot").GetProperty("size").GetInt32());

        // Back again: the mine's cells, seed and version tag don't follow it onto a circuit's plot.
        await Post(server, $"sessions/{id}/version", new { version = "b1.8.1", preferred_for = "b1.8.1" });
        await Post(server, $"sessions/{id}/canvas", new { rev = 0, ops = new[] { new { kind = "dig", label = "shaft", cells = new[] { Cell(30, 20, 30, 0, 0) } } } });
        Assert.True((await Post(server, $"sessions/{id}/version", new { version = "b1.8.1", kind = "redstone" })).GetProperty("needs_clear").GetBoolean());
        var circuit = (await Post(server, $"sessions/{id}/version", new { version = "b1.8.1", kind = "redstone", apply = true })).GetProperty("session");
        Assert.Equal("redstone", Kind(circuit));
        Assert.Equal(JsonValueKind.Null, circuit.GetProperty("seed").ValueKind);
        Assert.Equal(JsonValueKind.Null, circuit.GetProperty("preferred_for").ValueKind);
        Assert.Empty(BlocksOf(circuit));
        Assert.Equal(0, circuit.GetProperty("dug").GetArrayLength());
        Assert.Equal(64, circuit.GetProperty("plot").GetProperty("size").GetInt32());
        Assert.Equal("Switched to a redstone design.", circuit.GetProperty("messages").EnumerateArray().Last().GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_kind_switch_that_also_changes_the_version_converts_the_kept_canvas_by_the_usual_rules()
    {
        var server = Server(out _);
        var s = await Post(server, "sessions", new { player = Fox, version = "1.12.2" });
        long id = s.GetProperty("id").GetInt64();
        await Place(server, id, Cell(5, 0, 5, Dust, 0), Cell(6, 0, 5, Comparator, 0));
        // Without apply the version is only checked: nothing changes, the kind included, and the canvas is still whole.
        var check = await Post(server, $"sessions/{id}/version", new { version = "b1.8.1", kind = "redstone" });
        Assert.False(check.GetProperty("changed").GetBoolean());
        Assert.Equal("1.12.2", check.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null, check.GetProperty("session").ValueKind);
        Assert.False(check.TryGetProperty("needs_clear", out var clear) && clear.ValueKind == JsonValueKind.True);
        Assert.Equal("Redstone Comparator", check.GetProperty("report").GetProperty("missing")[0].GetProperty("name").GetString());
        var kept = await Session(server, id);
        Assert.Equal("build", Kind(kept));
        Assert.Equal("1.12.2", kept.GetProperty("version").GetString());
        Assert.Equal(2, BlocksOf(kept).Count);

        var converted = (await Post(server, $"sessions/{id}/version", new { version = "b1.8.1", kind = "redstone", apply = true })).GetProperty("session");
        Assert.Equal("redstone", Kind(converted));
        Assert.Equal("b1.8.1", converted.GetProperty("version").GetString());
        Assert.Equal([(Dust, 0)], StatesOf(converted)); // b1.8.1 has no comparator
    }

    // ------------------------------------------------------------------ the kind of a saved design

    [Fact]
    public async Task A_saved_circuit_is_tagged_by_a_comment_line_and_opens_as_redstone()
    {
        var server = Server(out var designs);
        var s = await Post(server, "sessions", new { player = Fox, kind = "redstone", version = "1.12.2" });
        long id = s.GetProperty("id").GetInt64();
        // A lever saved on, a repeater at delay 3 and a comparator in subtract mode: the states a circuit is configured with.
        int[][] cells = [Cell(30, 0, 30, Stone, 0), Cell(30, 1, 30, Lever, 5 | 8), Cell(31, 0, 30, Dust, 0), Cell(32, 0, 30, Repeater, 1 | 8), Cell(33, 0, 30, Comparator, 1 | 4)];
        await Place(server, id, cells);
        var saved = await Post(server, $"sessions/{id}/save", new { name = "delay line", player = Fox });
        long design = saved.GetProperty("design_id").GetInt64();
        var rec = designs.Store.Get(design)!;
        Assert.StartsWith("# kind redstone\n# drawn in the Design Studio\nblueprint delay line\n", rec.Dsl);
        Assert.Equal("drawn exactly", rec.Eval);
        Assert.Equal(DesignerCanvas.KindRedstone, DesignerCanvas.KindOfDsl(rec.Dsl));
        // To the DSL's parser the line is a comment: the design reads as an ordinary build, as it does to code older than the kind.
        var (bp, issues) = BlueprintParser.Parse(rec.Dsl);
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
        Assert.Equal("build", bp.Kind);

        var open = await Open(server, design);
        Assert.Equal("redstone", Kind(open));
        Assert.Equal("redstone", open.GetProperty("revisions")[0].GetProperty("kind").GetString());
        Assert.Equal("1.12.2", open.GetProperty("version").GetString());
        Assert.Equal(cells.Select(c => (c[3], c[4])).OrderBy(c => c).ToList(), StatesOf(open));

        // The same cells saved from a build canvas carry no tag and open as a build.
        var b = await Post(server, "sessions", new { player = Fox, version = "1.12.2" });
        long bid = b.GetProperty("id").GetInt64();
        await Place(server, bid, cells);
        long plain = (await Post(server, $"sessions/{bid}/save", new { name = "plain", player = Fox })).GetProperty("design_id").GetInt64();
        Assert.StartsWith("# drawn in the Design Studio\n", designs.Store.Get(plain)!.Dsl);
        Assert.Equal(DesignerCanvas.KindBuild, DesignerCanvas.KindOfDsl(designs.Store.Get(plain)!.Dsl));
        var plainOpen = await Open(server, plain);
        Assert.Equal("build", Kind(plainOpen));
        Assert.Equal("build", plainOpen.GetProperty("revisions")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Switching_a_saved_designs_kind_is_a_revision_and_each_revision_keeps_its_own()
    {
        var server = Server(out var designs);
        var s = await Post(server, "sessions", new { player = Fox, version = "1.12.2" });
        long id = s.GetProperty("id").GetInt64();
        await Place(server, id, Cell(30, 0, 30, Stone, 0), Cell(31, 0, 30, Dust, 0));
        var first = await Post(server, $"sessions/{id}/save", new { name = "gate", player = Fox });
        long design = first.GetProperty("design_id").GetInt64();
        int r1 = first.GetProperty("rev").GetInt32();
        // Not a cell changes: only the kind does.
        await Post(server, $"sessions/{id}/version", new { version = "1.12.2", kind = "redstone" });
        var second = await Post(server, $"sessions/{id}/save", new { name = "gate", player = Fox });
        Assert.Equal(r1 + 1, second.GetProperty("rev").GetInt32());
        var revisions = (await Session(server, id)).GetProperty("revisions").EnumerateArray().Select(r => (r.GetProperty("rev").GetInt32(), r.GetProperty("kind").GetString())).ToList();
        Assert.Equal([(r1 + 1, "redstone"), (r1, "build")], revisions);
        Assert.Equal("redstone", Kind(await Open(server, design)));

        // The older revision opens as the build it was; saving it again is the revert, kind included.
        var old = await Open(server, design, r1);
        Assert.Equal("build", Kind(old));
        var reverted = await Post(server, $"sessions/{old.GetProperty("id").GetInt64()}/save", new { name = "gate", player = Fox });
        Assert.Equal(r1 + 2, reverted.GetProperty("rev").GetInt32());
        Assert.False(RedstoneDesign.IsRedstone(designs.Store.Get(design)!.Dsl));
        Assert.Equal("build", Kind(await Open(server, design)));
    }

    private sealed class FakeWorld(Dictionary<BlockPos, (int Id, int Meta)> blocks) : IBlockAccess
    {
        public int GetBlock(int x, int y, int z) => blocks.TryGetValue(new BlockPos(x, y, z), out var s) ? s.Id : 0;
        public int GetMeta(int x, int y, int z) => blocks.TryGetValue(new BlockPos(x, y, z), out var s) ? s.Meta : 0;
        public bool IsLoaded(int x, int z) => true;
        public BlockInfo Info(int id) => Blocks.Get(id);
    }

    private static DesignerSaveResult Saved(BlueprintService svc, string kind, string name, params CanvasCell[] cells)
    {
        var session = new DesignerSessionRecord { Owner = Fox, Canvas = new DesignerCanvas { Kind = kind } };
        session.Canvas.Apply(cells);
        var saved = DesignerSave.Save(svc, session, name, null, Fox, asNew: false);
        Assert.True(saved.Ok, saved.Text);
        return saved;
    }

    [Theory]
    [InlineData(DesignerCanvas.KindRedstone, true)]
    [InlineData(DesignerCanvas.KindBuild, false)]
    public void A_commit_and_a_revert_redraw_the_design_and_it_stays_what_it_was(string kind, bool circuit)
    {
        using var db = AgentDb.Open(null);
        var svc = Designs(db);
        var h = svc.History!;
        var saved = Saved(svc, kind, "latch", new CanvasCell(30, 0, 30, Blocks.Cobblestone, 0), new CanvasCell(31, 0, 30, Dust, 0), new CanvasCell(32, 0, 30, Blocks.Cobblestone, 0));
        var rec = svc.Store.Get(saved.DesignId)!;
        Assert.Equal(circuit, RedstoneDesign.IsRedstone(rec.Dsl));

        // The world shows it with the dust gone and Fox commits that: the bot draws the next revision's DSL itself.
        var world = h.Revisions.Snapshot(rec.Id, saved.Rev)!.Blocks.Where(kv => kv.Value.Id != Dust).ToDictionary(kv => kv.Key, kv => kv.Value);
        h.World = () => new FakeWorld(world);
        var (ok, text) = h.Commit(rec, Fox, "took the dust out");
        Assert.True(ok, text);
        var r2 = h.Revisions.Head(rec.Id)!;
        Assert.Equal(saved.Rev + 1, r2.Rev);
        Assert.StartsWith(circuit ? "# kind redstone\n# captured from the world at " : "# captured from the world at ", r2.Dsl);
        Assert.Equal(kind, DesignerSave.LoadDesign(svc, rec.Id).Canvas!.Kind);

        // "Put it back": r1's cells drawn again as r3.
        rec = svc.Store.Get(rec.Id)!;
        var (_, reverted) = h.Revert(rec, $"r{saved.Rev}", Fox);
        Assert.Contains($"to r{saved.Rev} (now r{r2.Rev + 1})", reverted);
        var r3 = h.Revisions.Head(rec.Id)!;
        Assert.Equal(r2.Rev + 1, r3.Rev);
        Assert.Equal(circuit ? 1 : 0, Regex.Matches(r3.Dsl, "kind redstone").Count); // carried once, never stacked or invented
        var back = DesignerSave.LoadDesign(svc, rec.Id);
        Assert.Equal(kind, back.Canvas!.Kind);
        Assert.Equal(3, back.Canvas.Count);
    }

    /// <summary>A circuit of three cells saved from the studio with editor data <paramref name="data"/>: its session, design and revision.</summary>
    private static async Task<(long Session, long Design, int Rev)> SavedCircuit(SupervisorServer server, string data)
    {
        var s = await Post(server, "sessions", new { player = Fox, kind = "redstone", version = "1.12.2" });
        long id = s.GetProperty("id").GetInt64();
        await Place(server, id, Cell(30, 0, 30, Stone, 0), Cell(31, 0, 30, Dust, 0), Cell(32, 0, 30, Stone, 0));
        var saved = await Post(server, $"sessions/{id}/save", new { name = "latch", player = Fox, redstone_authoring = data });
        return (id, saved.GetProperty("design_id").GetInt64(), saved.GetProperty("rev").GetInt32());
    }

    /// <summary>The world shows revision <paramref name="rev"/> of the design with its dust taken out.</summary>
    private static void WorldWithoutDust(BlueprintService designs, long design, int rev)
    {
        var world = designs.History!.Revisions.Snapshot(design, rev)!.Blocks.Where(kv => kv.Value.Id != Dust).ToDictionary(kv => kv.Key, kv => kv.Value);
        designs.History.World = () => new FakeWorld(world);
    }

    [Fact]
    public async Task A_revision_the_bot_draws_keeps_the_editor_data_it_was_drawn_from()
    {
        var server = Server(out var designs);
        var (id, design, r1) = await SavedCircuit(server, Authoring("sum"));
        var r2 = (await Post(server, $"sessions/{id}/save", new { name = "latch", player = Fox, redstone_authoring = Authoring("carry") })).GetProperty("rev").GetInt32();
        Assert.Equal(r1 + 1, r2);
        var h = designs.History!;

        // A commit of the world's change: the new HEAD keeps HEAD's data (no row of its own is written for it).
        WorldWithoutDust(designs, design, r2);
        var (ok, text) = h.Commit(designs.Store.Get(design)!, Fox, "took the dust out");
        Assert.True(ok, text);
        var r3 = h.Revisions.Head(design)!;
        Assert.Equal(r2 + 1, r3.Rev);
        Assert.StartsWith("# kind redstone\n" + DesignerRedstoneAuthoring.Marker(Authoring("carry")) + "# captured from the world at ", r3.Dsl);
        var opened = await Open(server, design);
        Assert.Equal("redstone", Kind(opened));
        Assert.Equal(r3.Rev, opened.GetProperty("design_revision").GetInt32());
        Assert.Equal(Authoring("carry"), RedstoneAuthoring(opened));

        // A revert to r1 draws r1 again, its data too: the older row the marker names, not the newest one.
        var (_, reverted) = h.Revert(designs.Store.Get(design)!, $"r{r1}", Fox);
        Assert.Contains($"to r{r1} (now r{r3.Rev + 1})", reverted);
        Assert.Contains(DesignerRedstoneAuthoring.Marker(Authoring("sum")), h.Revisions.Head(design)!.Dsl);
        Assert.Equal(Authoring("sum"), RedstoneAuthoring(await Open(server, design)));
        Assert.Equal(Authoring("carry"), RedstoneAuthoring(await Open(server, design, r3.Rev)));

        // Saving the reopened revert unchanged keeps that data: the save writes it as the next revision's own row.
        var again = await Open(server, design);
        var saved = await Post(server, $"sessions/{again.GetProperty("id").GetInt64()}/save", new { name = "latch", player = Fox });
        Assert.Equal(Authoring("sum"), RedstoneAuthoring(await Open(server, design, saved.GetProperty("rev").GetInt32())));
    }

    [Fact]
    public async Task A_revert_to_a_circuit_brings_the_circuit_back_even_when_head_is_a_build()
    {
        var server = Server(out var designs);
        var (id, design, r1) = await SavedCircuit(server, Authoring("sum"));
        await Post(server, $"sessions/{id}/version", new { version = "1.12.2", kind = "build" });
        var r2 = (await Post(server, $"sessions/{id}/save", new { name = "latch", player = Fox })).GetProperty("rev").GetInt32();
        Assert.Equal(r1 + 1, r2);
        Assert.False(RedstoneDesign.IsRedstone(designs.Store.Get(design)!.Dsl));

        WorldWithoutDust(designs, design, r2);
        var (_, reverted) = designs.History!.Revert(designs.Store.Get(design)!, $"r{r1}", Fox);
        Assert.Contains($"to r{r1} (now r{r2 + 1})", reverted);
        var rec = designs.Store.Get(design)!;
        Assert.True(RedstoneDesign.IsRedstone(rec.Dsl));
        // A circuit again, so the classic builder (and a pawn's blueprint order) still never builds half of it.
        Assert.Equal(BlueprintService.RedstoneRefusal(rec), designs.WorldRefusal(rec));
        var opened = await Open(server, design);
        Assert.Equal("redstone", Kind(opened));
        Assert.Equal(Authoring("sum"), RedstoneAuthoring(opened));
    }

    [Fact]
    public void A_revision_without_a_row_takes_the_one_its_marker_names_else_the_newest_older_one()
    {
        using var db = AgentDb.Open(null);
        var store = new DesignerStore(db);
        store.SaveRedstoneAuthoring(5, 1, Authoring("one"));
        store.SaveRedstoneAuthoring(5, 2, Authoring("two"));
        store.SaveRedstoneAuthoring(6, 1, Authoring("other design"));
        Assert.Equal(Authoring("two"), store.RedstoneAuthoringFor(5, 2, null)); // its own row first
        Assert.Equal(Authoring("one"), store.RedstoneAuthoringFor(5, 1, "# kind redstone\n" + DesignerRedstoneAuthoring.Marker(Authoring("two"))));
        Assert.Equal(Authoring("two"), store.RedstoneAuthoringFor(5, 4, null));
        Assert.Equal(Authoring("two"), store.RedstoneAuthoringFor(5, 4, "# kind redstone\nblueprint x\n"));
        Assert.Equal(Authoring("one"), store.RedstoneAuthoringFor(5, 4, "# kind redstone\n" + DesignerRedstoneAuthoring.Marker(Authoring("one")) + "blueprint x\n"));
        // A marker no row matches: the newest older row.
        Assert.Equal(Authoring("two"), store.RedstoneAuthoringFor(5, 4, DesignerRedstoneAuthoring.Marker(Authoring("gone"))));
        // Never a later revision's row, nor another design's.
        Assert.Null(store.RedstoneAuthoringFor(5, 0, null));
        Assert.Null(store.RedstoneAuthoringFor(7, 3, null));
        // The exact read is unchanged.
        Assert.Null(store.RedstoneAuthoring(5, 4));
        var session = new DesignerSessionRecord { DesignId = 5, DesignRevision = 3, Canvas = new DesignerCanvas { Kind = DesignerCanvas.KindRedstone } };
        Assert.Equal(Authoring("two"), store.RedstoneAuthoringOf(session));

        // The marker rides along with the tag when a redraw drops both; a tagged text, or one that was no circuit, is left as it is.
        var marker = DesignerRedstoneAuthoring.Marker(Authoring("one"));
        Assert.Equal("# kind redstone\n" + marker + "blueprint y\n", RedstoneDesign.Carry("# kind redstone\n" + marker + "blueprint x\n", "blueprint y\n"));
        Assert.Equal("# kind redstone\nblueprint y\n", RedstoneDesign.Carry("# kind redstone\nblueprint x\n", "blueprint y\n"));
        Assert.Equal("blueprint y\n", RedstoneDesign.Carry(marker + "blueprint x\n", "blueprint y\n"));
        Assert.Equal("# kind redstone\nblueprint y\n", RedstoneDesign.Carry("# kind redstone\n" + marker, "# kind redstone\nblueprint y\n"));
    }

    // ------------------------------------------------------------------ editor data per revision

    [Fact]
    public async Task Authoring_is_stored_with_each_revision_and_a_revert_restores_the_older_one()
    {
        var server = Server(out var designs);
        var s = await Post(server, "sessions", new { player = Fox, kind = "redstone", version = "1.12.2" });
        long id = s.GetProperty("id").GetInt64();
        await Place(server, id, Cell(5, 0, 5, Dust, 0));
        string first = Authoring("sum"), second = Authoring("carry");
        var saved = await Post(server, $"sessions/{id}/save", new { name = "adder", redstone_authoring = first });
        long design = saved.GetProperty("design_id").GetInt64();
        int r1 = saved.GetProperty("rev").GetInt32();
        Assert.Equal(first, RedstoneAuthoring(await Session(server, id)));
        var dsl = designs.Store.Get(design)!.Dsl;
        Assert.StartsWith("# kind redstone\n" + DesignerRedstoneAuthoring.Marker(first) + "# drawn in the Design Studio\n", dsl);
        Assert.DoesNotContain("gate.xor", dsl); // the DSL carries the data's hash, never the data

        // A label is all that changed: no cell did, and it is a revision all the same.
        var relabelled = await Post(server, $"sessions/{id}/save", new { name = "adder", redstone_authoring = second });
        Assert.Equal(r1 + 1, relabelled.GetProperty("rev").GetInt32());
        Assert.Contains(DesignerRedstoneAuthoring.Marker(second), designs.Store.Get(design)!.Dsl);
        // The same data saved again is no revision.
        Assert.Equal(r1 + 1, (await Post(server, $"sessions/{id}/save", new { name = "adder", redstone_authoring = second })).GetProperty("rev").GetInt32());
        // Nor is a save that sends none: the session still holds what it saved last.
        Assert.Equal(r1 + 1, (await Post(server, $"sessions/{id}/save", new { name = "adder" })).GetProperty("rev").GetInt32());
        var resumed = await Session(server, id);
        Assert.Equal(second, RedstoneAuthoring(resumed));
        Assert.Equal(r1 + 1, resumed.GetProperty("design_revision").GetInt32());

        var newest = await Open(server, design);
        Assert.Equal("redstone", Kind(newest));
        Assert.Equal(second, RedstoneAuthoring(newest));
        Assert.Equal(["redstone", "redstone"], newest.GetProperty("revisions").EnumerateArray().Select(r => r.GetProperty("kind").GetString()));
        var old = await Open(server, design, r1);
        Assert.Equal(first, RedstoneAuthoring(old));
        Assert.Equal(r1, old.GetProperty("design_revision").GetInt32());
        long oldId = old.GetProperty("id").GetInt64();

        // Save as new from the older revision: the copy takes that revision's data, not the newest.
        long copy = (await Post(server, $"sessions/{oldId}/save", new { name = "adder as it was", as_new = true })).GetProperty("design_id").GetInt64();
        Assert.NotEqual(design, copy);
        Assert.Equal(first, RedstoneAuthoring(await Open(server, copy)));
        // Saving the older revision over the design is the revert: the next revision holds the older data again.
        var again = await Open(server, design, r1);
        var reverted = await Post(server, $"sessions/{again.GetProperty("id").GetInt64()}/save", new { name = "adder" });
        Assert.Equal(r1 + 2, reverted.GetProperty("rev").GetInt32());
        Assert.Equal(first, RedstoneAuthoring(await Open(server, design)));
        Assert.Equal(second, RedstoneAuthoring(await Open(server, design, r1 + 1))); // and r2 keeps its own
    }

    [Fact]
    public async Task Authoring_is_a_redstone_designs_only_and_a_bad_envelope_saves_nothing()
    {
        var server = Server(out var designs);
        var s = await Post(server, "sessions", new { player = Fox, kind = "redstone" });
        long id = s.GetProperty("id").GetInt64();
        await Place(server, id, Cell(5, 0, 5, Dust, 0));
        long design = (await Post(server, $"sessions/{id}/save", new { name = "circuit", redstone_authoring = Authoring("a") })).GetProperty("design_id").GetInt64();

        // As a build the session shows none; back as a circuit it is the one it held (nothing was saved in between).
        var asBuild = (await Post(server, $"sessions/{id}/version", new { version = "b1.8.1", kind = "build" })).GetProperty("session");
        Assert.Equal(JsonValueKind.Null, asBuild.GetProperty("redstone_authoring").ValueKind);
        var asCircuit = (await Post(server, $"sessions/{id}/version", new { version = "b1.8.1", kind = "redstone" })).GetProperty("session");
        Assert.Equal(Authoring("a"), RedstoneAuthoring(asCircuit));

        // Saved from a build canvas, the data in the body is dropped: no row, no marker, no tag.
        await Post(server, $"sessions/{id}/version", new { version = "b1.8.1", kind = "build" });
        var plain = await Post(server, $"sessions/{id}/save", new { name = "plain", as_new = true, redstone_authoring = Authoring("b") });
        long plainId = plain.GetProperty("design_id").GetInt64();
        var plainDsl = designs.Store.Get(plainId)!.Dsl;
        Assert.DoesNotContain("# kind redstone", plainDsl);
        Assert.DoesNotContain("# redstone layout", plainDsl);
        var plainOpen = await Open(server, plainId);
        Assert.Equal("build", Kind(plainOpen));
        Assert.Equal(JsonValueKind.Null, plainOpen.GetProperty("redstone_authoring").ValueKind);
        var relabelled = (await Post(server, $"sessions/{plainOpen.GetProperty("id").GetInt64()}/version", new { version = "b1.8.1", kind = "redstone" })).GetProperty("session");
        Assert.Equal(JsonValueKind.Null, relabelled.GetProperty("redstone_authoring").ValueKind);

        // A circuit saved without any opens without any.
        var bare = await Post(server, "sessions", new { player = Fox, kind = "redstone" });
        long bareId = bare.GetProperty("id").GetInt64();
        await Place(server, bareId, Cell(5, 0, 5, Dust, 0));
        long bareDesign = (await Post(server, $"sessions/{bareId}/save", new { name = "bare" })).GetProperty("design_id").GetInt64();
        Assert.Equal(JsonValueKind.Null, (await Open(server, bareDesign)).GetProperty("redstone_authoring").ValueKind);

        // Anything but the marked object is refused, on a circuit and on the build canvas alike, and nothing is saved.
        long circuitSession = (await Open(server, design)).GetProperty("id").GetInt64();
        Assert.Equal("build", Kind(await Session(server, id)));
        string[] bad = ["not json", "", "[]", "{}", "{\"redstone\":2}", "{\"redstone\":\"1\"}", "{\"redstone\":true}", "{\"size\":64,\"modules\":[],\"base\":[],\"overrides\":[]}",
            "{\"redstone\":1,\"pad\":\"" + new string('x', DesignerRedstoneAuthoring.MaxLength) + "\"}"];
        foreach (var text in bad)
        {
            var reply = await server.HandleAsync(Req("POST", $"/api/designer/sessions/{circuitSession}/save", new { name = "bad", redstone_authoring = text }));
            Assert.True(reply.Status == 400, $"{text[..Math.Min(40, text.Length)]}: {reply.Status}");
            Assert.Contains("redstone_authoring", reply.Text);
            Assert.Equal(400, (await server.HandleAsync(Req("POST", $"/api/designer/sessions/{id}/save", new { name = "bad", redstone_authoring = text }))).Status);
        }
        Assert.Single((await Session(server, circuitSession)).GetProperty("revisions").EnumerateArray());
        Assert.Equal("circuit", designs.Store.Get(design)!.Name);
        Assert.Equal("plain", designs.Store.Get(plainId)!.Name);
    }

    [Fact]
    public void The_envelope_check_takes_the_marked_object_and_bounds_its_size_and_depth()
    {
        Assert.True(DesignerRedstoneAuthoring.Valid(null));
        Assert.True(DesignerRedstoneAuthoring.Valid(Authoring("sum")));
        Assert.True(DesignerRedstoneAuthoring.Valid("{\"redstone\":1}"));
        // Exactly at the limit it passes; one character over it doesn't.
        var atLimit = "{\"redstone\":1}".PadRight(DesignerRedstoneAuthoring.MaxLength);
        Assert.True(DesignerRedstoneAuthoring.Valid(atLimit));
        Assert.False(DesignerRedstoneAuthoring.Valid(atLimit + " "));
        Assert.False(DesignerRedstoneAuthoring.Valid("{\"redstone\":1.5}"));
        Assert.False(DesignerRedstoneAuthoring.Valid("{\"Redstone\":1}"));
        Assert.False(DesignerRedstoneAuthoring.Valid("1"));
        Assert.False(DesignerRedstoneAuthoring.Valid("{\"redstone\":1,\"deep\":" + new string('[', 40) + new string(']', 40) + "}"));
        // The marker is the data's hash on one comment line: equal texts give equal markers, any change another.
        var marker = DesignerRedstoneAuthoring.Marker(Authoring("sum"));
        Assert.Matches("^# redstone layout [0-9a-f]{64}\n$", marker);
        Assert.Equal(marker, DesignerRedstoneAuthoring.Marker(Authoring("sum")));
        Assert.NotEqual(marker, DesignerRedstoneAuthoring.Marker(Authoring("carry")));
    }

    [Fact]
    public async Task Without_a_history_the_newest_save_holds_the_data()
    {
        // No revisions are kept (rev 0 every time), so the one row there is must follow the saves.
        var server = Server(out _, history: false);
        var s = await Post(server, "sessions", new { player = Fox, kind = "redstone" });
        long id = s.GetProperty("id").GetInt64();
        await Place(server, id, Cell(5, 0, 5, Dust, 0));
        var saved = await Post(server, $"sessions/{id}/save", new { name = "circuit", redstone_authoring = Authoring("a") });
        Assert.Equal(0, saved.GetProperty("rev").GetInt32());
        await Post(server, $"sessions/{id}/save", new { name = "circuit", redstone_authoring = Authoring("b") });
        Assert.Equal(Authoring("b"), RedstoneAuthoring(await Session(server, id)));
        Assert.Equal(Authoring("b"), RedstoneAuthoring(await Open(server, saved.GetProperty("design_id").GetInt64())));
    }

    [Fact]
    public void A_revisions_row_is_written_once_and_rev_0_follows_the_saves()
    {
        using var db = AgentDb.Open(null);
        var store = new DesignerStore(db);
        store.SaveRedstoneAuthoring(5, 2, "first");
        store.SaveRedstoneAuthoring(5, 2, "second");
        store.SaveRedstoneAuthoring(5, 2, null);
        Assert.Equal("first", store.RedstoneAuthoring(5, 2));
        Assert.Null(store.RedstoneAuthoring(5, 3));
        Assert.Null(store.RedstoneAuthoring(6, 2));
        store.SaveRedstoneAuthoring(5, 0, "first");
        store.SaveRedstoneAuthoring(5, 0, "second");
        Assert.Equal("second", store.RedstoneAuthoring(5, 0));
        store.SaveRedstoneAuthoring(5, 0, null);
        Assert.Null(store.RedstoneAuthoring(5, 0));
        // A session holds its design revision's row, and only while its canvas is a circuit.
        var session = new DesignerSessionRecord { DesignId = 5, DesignRevision = 2, Canvas = new DesignerCanvas { Kind = DesignerCanvas.KindRedstone } };
        Assert.Equal("first", store.RedstoneAuthoringOf(session));
        session.Canvas.Kind = DesignerCanvas.KindBuild;
        Assert.Null(store.RedstoneAuthoringOf(session));
        Assert.Null(store.RedstoneAuthoringOf(new DesignerSessionRecord { Canvas = new DesignerCanvas { Kind = DesignerCanvas.KindRedstone } }));
        // The mine's rows are another table: the same design and revision don't collide.
        store.SaveMineAuthoring(5, 2, "mine");
        Assert.Equal("mine", store.MineAuthoring(5, 2));
        Assert.Equal("first", store.RedstoneAuthoring(5, 2));
    }

    // ------------------------------------------------------------------ every part survives the DSL

    /// <summary>
    /// A revision's block set is what its DSL parses back to (DesignHistory.Sync), so a state the palette can't spell would be
    /// drawn as a stand-in and lost on reload. Every redstone part of the version, at every meta the canvas accepts (the facing,
    /// a repeater's delay, a comparator's mode, a lever saved on, a dust or plate state that slipped in), must come back as itself.
    /// </summary>
    [Theory]
    [InlineData(DesignVersions.Beta, 100)]
    [InlineData(DesignVersions.Release, 330)]
    public void Every_redstone_part_at_every_valid_meta_saves_exactly_and_reloads_equal(string version, int atLeast)
    {
        using var db = AgentDb.Open(null);
        var svc = Designs(db);
        var palette = DesignPalette.For(version);
        // The palette's Redstone category, and the rails a circuit switches or reads (they sit under Transport). A second half
        // (a door's upper) comes with its first: it is never placed on its own.
        int[] rails = [27, 28, 157];
        var states = (from id in palette.Entries.Where(e => e.Category == DesignPalette.Redstone).Select(e => e.Id).Concat(rails.Where(palette.Has)).Distinct()
                      from meta in Enumerable.Range(0, 16)
                      where palette.Refusal(id, meta) == null && !DesignFacing.SecondHalf(id, meta)
                      select (Id: id, Meta: meta)).ToList();
        Assert.True(states.Count >= atLeast, $"{version}: only {states.Count} states listed");
        foreach (var configured in new[] { (Repeater, 3 | 12), (Lever, 5 | 8), (Dust, 15), (RedstoneTorch, 2), (27, 1 | 8) }) Assert.Contains(configured, states);
        if (version == DesignVersions.Release) Assert.Contains((Comparator, 2 | 4), states);

        // A design holds fewer distinct states than there are here (one palette symbol each), so they go in batches.
        int batchNo = 0;
        foreach (var batch in states.Chunk(96))
        {
            var canvas = new DesignerCanvas { Version = version, Kind = DesignerCanvas.KindRedstone };
            var cells = new List<CanvasCell>();
            for (int i = 0; i < batch.Length; i++)
            {
                // Three apart: no part touches another (chests would pair), and a wall torch gets the block it hangs on.
                int x = 2 + i % 10 * 3, z = 2 + i / 10 * 3;
                if (DesignerCanvas.WallOf(batch[i].Id, batch[i].Meta) is { } wall) cells.Add(new CanvasCell(x + wall.Dx, 0, z + wall.Dz, Stone, 0));
                cells.Add(new CanvasCell(x, 0, z, batch[i].Id, batch[i].Meta));
            }
            using (DesignVersions.Use(version))
            {
                canvas.Apply(cells, out int rejected);
                Assert.Equal(0, rejected);
                Assert.Empty(canvas.LastFixed);
            }
            foreach (var c in cells) Assert.Equal((c.Id, c.Meta), canvas.Get(c.X, c.Y, c.Z));

            var saved = DesignerSave.Save(svc, new DesignerSessionRecord { Owner = Fox, Canvas = canvas }, $"parts {++batchNo}", null, Fox, asNew: true);
            Assert.True(saved.Ok, saved.Text);
            Assert.DoesNotContain("stand-ins", saved.Text);
            var rec = svc.Store.Get(saved.DesignId)!;
            Assert.Equal("drawn exactly", rec.Eval);
            Assert.True(RedstoneDesign.IsRedstone(rec.Dsl));

            var loaded = DesignerSave.LoadDesign(svc, saved.DesignId);
            Assert.Null(loaded.Why);
            Assert.Equal(DesignerCanvas.KindRedstone, loaded.Canvas!.Kind);
            Assert.Equal(version, loaded.Canvas.Version);
            var (was, now) = (Shape(canvas), Shape(loaded.Canvas));
            foreach (var (at, state) in was)
                Assert.True(now.TryGetValue(at, out var got) && got == state, $"{version} {palette.Spec(state.Id, state.Meta)} ({state.Id}:{state.Meta}) came back as {got}");
            Assert.Equal(was.Count, now.Count);
            // Opening it settles attachments (the routes do): nothing here hangs on air, so nothing is turned.
            using (DesignVersions.Use(version)) Assert.Empty(loaded.Canvas.FixAllAttachments());
        }
    }

    /// <summary>A canvas's blocks relative to their box's corner (a design opens centred on the plot, wherever it was drawn).</summary>
    private static Dictionary<(int X, int Y, int Z), (int Id, int Meta)> Shape(DesignerCanvas canvas)
    {
        var min = canvas.Bounds!.Value.Min;
        return canvas.Blocks.ToDictionary(kv => (kv.Key.X - min.X, kv.Key.Y - min.Y, kv.Key.Z - min.Z), kv => kv.Value);
    }
}
