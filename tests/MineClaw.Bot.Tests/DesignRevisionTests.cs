using MineClaw.Agent;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Bot.Memory;
using MineClaw.Client;
using MineClaw.Client.Tests;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Protocol.Packets;
using MineClaw.World;
using MineClaw.World.Designs;
using BlockPos = MineClaw.World.BlockPos;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Fox, 10-30: "Just because a build is saved doesn't mean it should be protected. I keep running into this. I want the AI to
/// keep working on the build, but it saves it, and then it can't edit it. Maybe this should be similar to Git style, so that we
/// can detect changes and do revisions and regressions." Live: main captured his spiral staircase ("memorize these stairs"),
/// then refused every next column ("part of the spiral_staircase build"); its own "stair_shaft_seal_y16" mark blocked the
/// doorway the room dig needed. These pin the editing context (requested, explicit digs on an open design; autonomous picks,
/// never-break marks, containers and other players' builds unchanged), the bot's own marks, revisions, diffs, drift
/// classification and revert through the construction executor, on b1.8.1 and 1.12.2.
/// </summary>
public class DesignRevisionTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 30, 14, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class BoxSource(params ProtectedBox[] boxes) : IProtectedRegionSource, IReservedItemSource
    {
        public IReadOnlyList<(BlockPos Min, BlockPos Max)> ProtectedRegions() => boxes.Select(b => (b.Min, b.Max)).ToList();
        public IReadOnlyList<ProtectedBox> Boxes() => boxes;
        public IReadOnlyCollection<string> ReservedItemNames() => [];
    }

    private static readonly BlockPos Step = new(154, 40, 276);
    private const string Fox = "PoofImaFox";

    private static (Protection P, EditContext E, Clock T) Staircase(params ProtectedBox[] more)
    {
        var t = new Clock();
        var stairs = new ProtectedBox(new BlockPos(150, 36, 272), new BlockPos(158, 48, 280), "spiral_staircase", Fox, Design: 12, Structure: true);
        var src = new BoxSource([stairs, .. more]);
        var e = new EditContext(t) { Trusted = () => [Fox, "kiwi"], IsBot = n => n == "maid_chan" };
        var p = new Protection(new BotOptions(), src, src, t) { Edits = e, Marks = new ProtectionMarks(null, t) };
        return (p, e, t);
    }

    private static IDisposable Turn(string? speaker)
    {
        var prev = TurnContext.Current;
        TurnContext.Current = new TurnInfo(speaker, speaker == null ? [] : [speaker], false);
        return new Undo(() => TurnContext.Current = prev);
    }

    private sealed class Undo(Action a) : IDisposable { public void Dispose() => a(); }

    // ------------------------------------------------------------------ the editing context

    [Fact]
    public void A_saved_design_yields_to_a_requested_explicit_dig_while_it_is_open_and_to_nothing_else()
    {
        var robins = new ProtectedBox(new BlockPos(157, 36, 272), new BlockPos(162, 48, 280), "robin's tower", "kiwi", Structure: true);
        var (p, e, t) = Staircase(robins);
        // Saved = protected against the bot's own picks, always (mine, materials, path digs never ask EditYields).
        Assert.True(p.IsProtected(Step, Blocks.Stone));
        Assert.Equal("nobody asked (my own choice)", p.EditCheck(Step, Blocks.Stone));
        Assert.Contains("design #12; editing it needs a trusted player's ask", p.Check(Step, Blocks.Stone).Why);
        using (Turn(Fox))
        {
            Assert.Equal("design #12 isn't open for editing", p.EditCheck(Step, Blocks.Stone));
            e.Touch(12, $"{Fox} captured it"); // "memorize these stairs"
            Assert.True(p.EditYields(Step, Blocks.Stone));             // the next column he asked for
            Assert.True(p.IsProtected(Step, Blocks.Stone));            // still never a material
            Assert.False(p.EditYields(Step, Blocks.Chest));            // containers: exact permits only
            Assert.False(p.EditYields(new BlockPos(158, 40, 276), Blocks.Stone)); // Robin's tower overlaps there: nobody asked her
            p.Marks!.Add(MarkKind.Never, 0, [(new BlockPos(154, 41, 276), new BlockPos(154, 41, 276))], "the landing", "kiwi", null, exact: true);
            Assert.Equal("a never-break mark", p.EditCheck(new BlockPos(154, 41, 276), Blocks.Stone)); // hard: never yields
            t.Now += TimeSpan.FromMinutes(31);
            Assert.Equal("design #12 isn't open for editing", p.EditCheck(Step, Blocks.Stone)); // the lease ran out
        }
        e.Touch(12, "Fox named it");
        using (Turn("griefer")) Assert.Equal("nobody asked (my own choice)", p.EditCheck(Step, Blocks.Stone)); // untrusted asks unlock nothing
        // A trusted line a few minutes ago still counts (the [task] wake after his ask has no speaker), until AskMinutes pass.
        e.NoteAsk(Fox);
        Assert.True(p.EditYields(Step, Blocks.Stone));
        t.Now += TimeSpan.FromMinutes(11);
        Assert.False(p.EditYields(Step, Blocks.Stone));
        // Its own build/revert job edits it, whoever is (not) talking.
        using (EditContext.JobFor(12)) Assert.True(p.EditYields(Step, Blocks.Stone));
        Assert.Null(EditContext.Job);
    }

    [Fact]
    public void The_bots_own_mark_never_blocks_its_requested_dig_but_still_guards_its_wandering()
    {
        // mark_structure without taught_by: main's own "stair_shaft_seal_y16" over the wall the room's doorway goes through.
        var seal = new ProtectedBox(new BlockPos(150, 15, 272), new BlockPos(158, 17, 280), "stair_shaft_seal_y16", null, Structure: true);
        var players = new ProtectedBox(new BlockPos(140, 15, 272), new BlockPos(149, 17, 280), "Fox's cellar", Fox, Structure: true);
        var t = new Clock();
        var src = new BoxSource(seal, players);
        var e = new EditContext(t) { Trusted = () => [Fox], IsBot = n => n == "maid_chan" };
        var p = new Protection(new BotOptions(), src, src, t) { Edits = e };
        var wall = new BlockPos(154, 16, 272);
        Assert.True(p.IsProtected(wall, Blocks.Stone));
        Assert.Equal("part of the stair_shaft_seal_y16 build (my own mark; a trusted player's ask digs through it)", p.Check(wall, Blocks.Stone).Why);
        Assert.False(p.EditYields(wall, Blocks.Stone)); // wandering: the mark holds
        using (Turn(Fox))
        {
            Assert.True(p.EditYields(wall, Blocks.Stone)); // "dig the room through there"
            Assert.Equal("the Fox's cellar build is PoofImaFox's", p.EditCheck(new BlockPos(145, 16, 272), Blocks.Stone)); // a player's mark isn't softer
        }
        // A bot-made mark by a hive body's name is the bot's own too.
        var clones = new BoxSource(seal with { Owner = "maid_chan" });
        var p2 = new Protection(new BotOptions(), clones, clones, t) { Edits = e };
        using (Turn(Fox)) Assert.True(p2.EditYields(wall, Blocks.Stone));
    }

    [Fact]
    public void A_hive_authored_design_is_open_for_its_own_requested_work_without_a_lease()
    {
        var (p, e, _) = Staircase();
        e.HiveDesign = id => id == 12;
        Assert.False(p.EditYields(Step, Blocks.Stone)); // still only requested work
        using (Turn(Fox)) Assert.True(p.EditYields(Step, Blocks.Stone));
    }

    // ------------------------------------------------------------------ over the fake server: explicit digs vs picks

    private static async Task<(MinecraftClient C, FakeServer S)> StartAsync(Action<FakeServer> setup, bool release1122)
    {
        var server = new FakeServer(release1122);
        setup(server);
        var o = new ClientOptions
        {
            Username = "maid_chan", TickInterval = TimeSpan.FromMilliseconds(5), ChatInterval = TimeSpan.Zero,
            RespawnDelay = TimeSpan.FromMilliseconds(10), HostileAlertRadius = 0,
            ConnectionFactory = release1122 ? server.CreateConnection : server.CreateClientConnection,
        };
        if (release1122) o = o with { Profile = MineClaw.Client.Versioning.GameVersionProfiles.Java1122 };
        var client = new MinecraftClient(o);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync(cts.Token);
        await Until(() => client.World.Player.OnGround, 3000);
        return (client, server);
    }

    private static async Task<bool> Until(Func<bool> cond, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond()) return true;
            await Task.Delay(5);
        }
        return cond();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_explicit_requested_dig_edits_the_captured_build_while_mine_never_picks_it(bool release1122)
    {
        using var mode = WorldMode.Override(WorldMode.Normal); // cobblestone is a resource for mine on a normal world
        var (c, s) = await StartAsync(srv =>
        {
            foreach (var (x, z) in new[] { (7, 9), (9, 9), (9, 7) }) srv.Blocks[(x, 64, z)] = Blocks.Cobblestone; // Fox's steps
        }, release1122);
        await using var _ = c;
        using var __ = s;
        var t = new Clock();
        var box = new ProtectedBox(new BlockPos(6, 63, 6), new BlockPos(10, 66, 10), "spiral_staircase", Fox, Design: 1, Structure: true);
        var src = new BoxSource(box);
        var edits = new EditContext(t) { Trusted = () => [Fox] };
        var p = new Protection(new BotOptions(), src, src, t) { Edits = edits, Marks = new ProtectionMarks(null, t), Dimension = () => c.World.Dimension };
        c.IsProtected = p.IsProtected;
        c.ExplainProtection = p.Explain;
        c.SoftOverride = p.OverridesSoft;
        c.EditYields = p.EditYields;

        var refused = await c.DigBlockAsync(7, 64, 9);
        Assert.False(refused.Ok);
        Assert.Contains("is protected (part of the spiral_staircase build (design #1; editing it needs a trusted player's ask)", refused.Message);
        using (Turn(Fox))
        {
            edits.Touch(1, $"{Fox} captured it");
            var dug = await c.DigBlockAsync(7, 64, 9);
            Assert.True(dug.Ok, dug.Message);
            Assert.True(await Until(() => c.World.GetBlock(7, 64, 9) == 0, 2000));
            // The same open design is never a material: mine picks skip it (the editing context is for named cells only).
            var mine = await c.MineAsync("cobblestone", 1, 8);
            Assert.False(mine.Ok, mine.Message);
            Assert.Equal(Blocks.Cobblestone, c.World.GetBlock(9, 64, 9));
            Assert.Equal(Blocks.Cobblestone, c.World.GetBlock(9, 64, 7));
            Assert.DoesNotContain(s.Dug, d => d.X == 9 && d.Y == 64);
            // A never-break mark holds against the requested dig too.
            p.Marks!.Add(MarkKind.Never, 0, [(new BlockPos(9, 64, 9), new BlockPos(9, 64, 9))], "the top step", "kiwi", null, exact: true);
            var never = await c.DigBlockAsync(9, 64, 9);
            Assert.False(never.Ok);
            Assert.Contains("never-break", never.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_explosion_takes_its_blocks_and_is_remembered_as_the_cause_on_both_versions(bool release1122)
    {
        var (c, s) = await StartAsync(srv => srv.Blocks[(8, 64, 7)] = Blocks.Planks, release1122);
        await using var _ = c;
        using var __ = s;
        Assert.True(await Until(() => c.World.GetBlock(8, 64, 7) == Blocks.Planks, 2000));
        s.Blocks.Remove((8, 64, 7));
        s.Send(new ExplosionPacket { X = 8.5, Y = 64.5, Z = 6.5, Radius = 3, Records = [new ExplosionRecord(0, 0, 1)] });
        Assert.True(await Until(() => c.World.GetBlock(8, 64, 7) == 0, 2000)); // 1.12.2: translated from 0x1C (was dropped)
        Assert.True(c.World.Blasts.Near(new BlockPos(8, 64, 7)));
        Assert.False(c.World.Blasts.Near(new BlockPos(40, 64, 40)));
    }

    // ------------------------------------------------------------------ revisions, diff, drift, commit, revert (sim world)

    private sealed record Fixture(BlueprintService S, WorldState W, SimBuildBody Body, MemoryStore Memory, Protection P, EditContext E, List<long> Started);

    /// <summary>A little staircase on the grass: a 3×3 planks landing (y 64), a cobble post (1,65..66,1), oak stairs at (0,65,1).</summary>
    private static Fixture Setup(AgentDb db)
    {
        var w = StructureCaptureTests.Terrain();
        for (int x = 0; x <= 2; x++) for (int z = 0; z <= 2; z++) w.SetBlock(x, 64, z, Blocks.Planks, 0);
        w.SetBlock(1, 65, 1, Blocks.Cobblestone, 0);
        w.SetBlock(1, 66, 1, Blocks.Cobblestone, 0);
        w.SetBlock(0, 65, 1, Blocks.WoodenStairs, 0);
        w.Player.ServerTeleport(new Vec3d(4.5, 64, 4.5), 65.62, 0, 0, true);
        var store = new BlueprintStore(db);
        var body = new SimBuildBody(w, new() { [(Blocks.Cobblestone, 0)] = 16, [(Blocks.Planks, 0)] = 16, [(Blocks.WoodenStairs, 0)] = 4 });
        var started = new List<long>();
        var s = new BlueprintService(new BlueprintOptions(), store, new BlueprintPreview())
        {
            Site = () => BlueprintTests.Site(w), Crew = () => new SingleBodyCrew(body),
            ResolveSite = (_, _) => (new SitePick(new BlockPos(-20, 63, 0), "over there"), null),
            Trusted = () => [Fox], Executor = new ConstructionExecutor(new BlueprintOptions(), store),
        };
        s.StartBuild = (id, _) => { started.Add(id); return null; };
        var memory = new MemoryStore(db, null);
        var e = new EditContext { Trusted = () => [Fox], IsBot = n => n == "maid_chan" };
        s.Edits = e;
        s.History = new DesignHistory(s, new DesignRevisions(db)) { World = () => w.PlanningView, Edits = e, Memory = memory, BotName = () => "maid_chan" };
        var adapter = new MemoryStoreAdapter(memory);
        var p = new Protection(new BotOptions { MemoryRefreshSeconds = 1 }, adapter, adapter) { Edits = e };
        // The build body the executor gets: the host's predicate minus the editing context's yield (GameBuildBody's rule).
        body.Protect = (q, id) => p.IsProtected(q, id) && !p.EditYields(q, id);
        return new Fixture(s, w, body, memory, p, e, started);
    }

    private static BlueprintRecord Capture(Fixture f)
    {
        var cap = StructureCapture.Capture(new CaptureRequest { Name = "stairs", World = f.W.PlanningView, Corners = (new BlockPos(0, 64, 0), new BlockPos(2, 66, 2)) });
        Assert.True(cap.Ok, cap.Refusal);
        var (rec, text) = DesignCapture.Save(f.S, cap, Fox, (r, c) => DesignCapture.Link(f.Memory, f.W, r, c));
        Assert.NotNull(rec);
        f.P.Invalidate();
        return rec!;
    }

    [Fact]
    public async Task Revisions_diff_drift_commit_and_revert_through_the_executor()
    {
        using var db = AgentDb.Open(null);
        var f = Setup(db);
        var h = f.S.History!;
        var rec = Capture(f);
        // r1: the capture, Fox's; the world matches it; the box protects it from my own picks.
        Assert.StartsWith("#1 stairs: r1 PoofImaFox just now 'captured by PoofImaFox'", h.Log(rec));
        Assert.Contains("world matches r1", h.Status(rec));
        Assert.True(f.P.IsProtected(new BlockPos(1, 66, 1), Blocks.Cobblestone));
        Assert.Equal("nobody asked (my own choice)", f.P.EditCheck(new BlockPos(1, 66, 1), Blocks.Cobblestone)); // open (Fox captured it), not asked

        // Fox digs the post's top, puts a block on the landing and turns the stairs; a creeper takes a corner of the landing.
        var drift = new DesignDrift(f.S, h, f.W, timer: false) { Self = () => "maid_chan", SettleSeconds = 0 };
        drift.Refresh();
        f.W.Entities.SpawnPlayer(7, Fox, new Vec3d(1.5, 64, 4.5), 0, 0);
        f.W.SetBlock(1, 66, 1, 0, 0);
        f.W.SetBlock(2, 65, 2, Blocks.Cobblestone, 0);
        f.W.SetBlock(0, 65, 1, Blocks.WoodenStairs, 2);
        f.W.Blasts.Note(new Vec3d(0.5, 64.5, -0.5), 1);
        f.W.SetBlock(0, 64, 0, 0, 0);
        var line = drift.Tick();
        Assert.NotNull(line);
        Assert.StartsWith("[design] #1 stairs drifted from r1: +1 cobblestone, −1 cobblestone, −1 planks, ~1 ", line);
        Assert.Contains(" facing (PoofImaFox 3, an explosion 1)", line);
        Assert.EndsWith("; revert{id=1} restores, commit{id=1} keeps", line);
        Assert.Null(drift.Tick()); // one line per burst, never again for the same drift
        Assert.StartsWith("r1→world: +1 cobblestone, −1 cobblestone, −1 planks", h.Diff(rec, null));
        Assert.Contains("; world: +1 cobblestone, −1 cobblestone, −1 planks, ~1 ", h.Status(rec));
        Assert.Contains(" facing (PoofImaFox 3, an explosion 1); commit{id=1} keeps it, revert{id=1} restores r1", h.Status(rec));
        Assert.NotNull(f.S.History!.Revisions.World(rec.Id)); // the Explorer's world view

        // A griefer can't commit; Fox commits his edit (the explosion's hole too, here) as r2, tagged good.
        Assert.Contains("needs a trusted player's word", h.Commit(rec, "griefer", null).Text);
        var (cok, ctext) = h.Commit(rec, Fox, "#good a step on the landing");
        Assert.True(cok, ctext);
        Assert.StartsWith("committed #1 stairs as r2: +1 cobblestone, −1 cobblestone, −1 planks", ctext);
        rec = f.S.Store.Get(rec.Id)!;
        Assert.Contains("world matches r2", h.Status(rec));
        Assert.StartsWith("r1→r2: +1 cobblestone, −1 cobblestone, −1 planks", h.Diff(rec, "r1 r2"));
        Assert.Equal(2, h.Revisions.Resolve(rec.Id, "good")!.Rev);
        // r2's DSL draws the world as it stands (parses back cell for cell).
        var plan = f.S.PlanOf(rec, out var err)!;
        Assert.Null(err);
        Assert.Equal(Blocks.Cobblestone, plan.Blocks[new BlockPos(2, 65, 2)].Id);
        Assert.False(plan.Blocks.ContainsKey(new BlockPos(0, 64, 0)));

        // "Put it back the way it was": revert to r1 (a new revision r3), built in place by the executor as its own job.
        Assert.Contains("needs a trusted player's word", h.Revert(rec, "r1", "griefer").Text);
        var (rok, rtext) = h.Revert(rec, "r1", Fox);
        Assert.True(rok, rtext);
        Assert.StartsWith("reverting #1 stairs to r1 (now r3): +1 cobblestone, +1 planks, −1 cobblestone, ~1 ", rtext);
        Assert.Equal([rec.Id], f.Started);
        f.P.Invalidate();
        Assert.False(f.P.EditYields(new BlockPos(2, 65, 2), Blocks.Cobblestone)); // outside its job nobody asked
        var (ok, built) = await f.S.BuildAsync(rec.Id.ToString(), null, null, null, CancellationToken.None);
        Assert.True(ok, built);
        Assert.Equal(0, f.W.GetBlock(2, 65, 2));                  // Fox's block dug (the job edits its own design)
        Assert.Equal(Blocks.Cobblestone, f.W.GetBlock(1, 66, 1)); // the post's top back
        Assert.Equal(Blocks.Planks, f.W.GetBlock(0, 64, 0));      // the blast's hole filled
        Assert.Equal(0, f.W.GetMeta(0, 65, 1));                   // the stairs facing back
        rec = f.S.Store.Get(rec.Id)!;
        Assert.Equal(DesignStatus.Built, rec.Status);
        Assert.Contains("world matches r3", h.Status(rec));
        var log = h.Log(rec);
        Assert.StartsWith("#1 stairs: r3 PoofImaFox just now 'revert to r1' · r2 PoofImaFox just now 'a step on the landing' [good] · r1 ", log);
        Assert.Equal(3, h.Revisions.List(rec.Id).Count); // finishing the build added no revision: its blocks are r3's
        // The linked structure still guards it from my wandering, as one box (no "stairs #1" twin).
        Assert.Single(f.Memory.Read(d => d.Structures.Where(st => DesignLinks.Of(st.Note) == rec.Id).ToList()));
    }

    [Fact]
    public void A_design_saved_before_revisions_becomes_r1_on_first_load_and_an_unchanged_save_adds_nothing()
    {
        using var db = AgentDb.Open(null);
        var f = Setup(db);
        var store = f.S.Store;
        var rec = store.Insert(new BlueprintRecord
        {
            Name = "hut", Description = "a hut", Requester = Fox, Bot = "maid_chan", Status = DesignStatus.Built, Dim = 0, Site = new BlockPos(-20, 63, 0),
            Dsl = "blueprint hut\nsize 3x2x3\norigin -21,64,-1\npalette C=cobblestone\nlayer 0-1\nCCC\nC.C\nCCC",
        });
        rec.Origin = new BlockPos(-21, 64, -1);
        store.Update(rec);
        Assert.Null(f.S.History!.Revisions.Head(rec.Id));
        var r1 = f.S.History.Ensure(rec)!;
        Assert.Equal(1, r1.Rev);
        Assert.Equal("saved before revisions", r1.Message);
        Assert.Equal(16, r1.Blocks);
        Assert.Equal(r1.Hash, f.S.History.Ensure(rec)!.Hash);
        Assert.Equal(1, f.S.History.Sync(rec, "maid_chan", RevisionAuthor.Bot, "saved again")!.Rev); // nothing changed: no r2
        Assert.Contains("world: −16 cobblestone", f.S.History.Status(rec)); // it isn't standing here (the world as the bot sees it)
    }

    // ------------------------------------------------------------------ the brain's design tool and chat (a whole bot)

    [Fact]
    public async Task The_design_tool_reads_and_moves_history_and_a_trusted_line_about_a_design_opens_it()
    {
        var (h, _) = TestBot.Create();
        h.Runtime.Options.Self.TrustedPlayers = [Fox];
        var wiring = h.Brain!.Blueprints!;
        var store = wiring.Service.Store;
        var rec = store.Insert(new BlueprintRecord
        {
            Name = "spiral_staircase", Description = "captured", Requester = Fox, Bot = "maid_chan", Status = DesignStatus.Captured, Site = new BlockPos(5, 63, 5),
            Dsl = "blueprint spiral_staircase\nsize 2x2x1\norigin 4,64,4\npalette S=stone\nlayer 0-1\nSS",
        });
        rec.Origin = new BlockPos(4, 64, 4);
        store.Update(rec);
        var id = rec.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var log = await h.Run("design", new { op = "log", id });
        Assert.True(log.Ok, log.Text);
        Assert.StartsWith($"#{id} spiral_staircase: r1 PoofImaFox", log.Text); // migrated on first look
        var status = await h.Run("design", new { op = "status", id });
        Assert.Contains("; world: −4 stone (unknown 4); commit{id=1} keeps it, revert{id=1} restores r1", status.Text); // nobody saw it go
        Assert.Contains("at r1 (log/status/diff)", (await h.Run("design", new { op = "show", id })).Text);
        Assert.Contains("needs a trusted player's word", (await h.Run("design", new { op = "revert", id })).Text); // nobody asked
        Assert.Contains("log|status|diff|commit|revert", (await h.Run("design", new { op = "rebase", id })).Text);

        var edits = h.Protection.Edits!;
        Assert.Null(edits.Open(rec.Id));
        wiring.TouchFromChat(h.Runtime, "griefer", "dig the stairs out");
        Assert.Null(edits.Open(rec.Id));
        wiring.TouchFromChat(h.Runtime, Fox, "keep digging the stairs down");
        Assert.Equal("PoofImaFox named it", edits.Open(rec.Id));
        Assert.True(BlueprintWiring.Names("the spiral staircase", "spiral_staircase"));
        Assert.False(BlueprintWiring.Names("go get some stone", "spiral_staircase"));
    }

    // ------------------------------------------------------------------ cross-version data

    [Fact]
    public void Snapshots_round_trip_and_diff_in_each_versions_own_ids()
    {
        var s = new DesignSnapshot();
        s.Blocks[new BlockPos(-5, 70, 3)] = (Blocks.Cobblestone, 0);
        s.Blocks[new BlockPos(-4, 70, 3)] = (Blocks.Cobblestone, 0);
        s.Blocks[new BlockPos(-2, 70, 3)] = (Blocks.Cobblestone, 0);
        s.Blocks[new BlockPos(0, 71, -1)] = (Blocks.WoodenStairs, 3);
        s.Air.Add(new BlockPos(-3, 70, 3));
        var back = DesignSnapshot.Decode(DesignSnapshot.EncodeStates(s.Blocks), DesignSnapshot.EncodeCells(s.Air));
        Assert.Equal(s.Hash(), back.Hash());
        Assert.Equal("70,3:-5/-4,-2", DesignSnapshot.EncodeCells(s.Blocks.Where(kv => kv.Value.Id == Blocks.Cobblestone).Select(kv => kv.Key)));

        // 1.12.2: its own ids and names (white → orange concrete is a state change of one block), and a door's use bits.
        using var data = GameData.Override(Release1122GameData.Instance);
        const int Concrete = 251, OakDoor = 64;
        var a = new DesignSnapshot();
        a.Blocks[new BlockPos(1, 64, 1)] = (Concrete, 0);
        a.Blocks[new BlockPos(2, 64, 1)] = (OakDoor, 1);  // lower half, facing 1
        a.Blocks[new BlockPos(2, 65, 1)] = (OakDoor, 8);  // upper half
        var b = a.Clone();
        b.Blocks[new BlockPos(1, 64, 1)] = (Concrete, 1);
        b.Blocks[new BlockPos(2, 64, 1)] = (OakDoor, 1 | 4); // opened
        b.Blocks[new BlockPos(2, 65, 1)] = (OakDoor, 8 | 2); // powered (1.12.2 upper half)
        var d = DesignDiff.Between(a, b);
        Assert.Equal("~1 concrete state", d.Text());
        Assert.True(DesignDiff.Same(OakDoor, 1, OakDoor, 5));
        Assert.False(DesignDiff.Same(OakDoor, 1, OakDoor, 3)); // another facing is an edit
    }
}
