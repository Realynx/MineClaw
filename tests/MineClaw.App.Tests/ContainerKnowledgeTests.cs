using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.App.Supervisor;
using MineClaw.Data;
using MineClaw.Hive.Story;
using MineClaw.World;

namespace MineClaw.App.Tests;

/// <summary>
/// The viewer's container panel (<c>/api/world/containers</c>): bodies' looks recorded on open, slot changes and close; double chests
/// by either half (one 54-slot look, or two single looks joined); live vs. last look vs. world memory vs. Story's ledger; unknown and
/// non-container blocks; the map-badge box summary and its cap.
/// </summary>
[Collection(GameDataDefaultCollection.Name)]
public class ContainerKnowledgeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly BlockPos A = new(2, 64, 3), B = new(3, 64, 3), F = new(6, 64, 3);

    private static WorldState World(params (BlockPos P, int Id)[] blocks)
    {
        var w = new WorldState(inventory: InventoryModel.Beta181) { Dimension = 0 };
        var ids = new byte[16 * w.WorldHeight * 16];
        w.ApplyChunkRegion(0, 0, 0, 16, w.WorldHeight, 16, ids, null, null, null);
        foreach (var (p, id) in blocks) Assert.True(w.SetBlock(p.X, p.Y, p.Z, id, 0));
        return w;
    }

    private static ItemStack S(int id, int n, int dmg = 0) => new((short)id, (byte)n, (short)dmg);

    private sealed class Ledger : IStoryChests
    {
        public StoryChestView? View;
        public StoryChestView? At(int dim, BlockPos c) =>
            View is { } v && (v.Chest == new P3(c.X, c.Y, c.Z) || v.DoubleWith == new P3(c.X, c.Y, c.Z)) ? v : null;
        public IReadOnlyList<StoryChestView> All(int dim) => View is { } v ? [v] : [];
    }

    private static (ContainerKnowledge K, ContainerSightings Store, ManualClock Clock, Ledger Story) Make(WorldState w, ObservationStore? obs = null)
    {
        var clock = new ManualClock(T0);
        var store = new ContainerSightings();
        var story = new Ledger();
        return (new ContainerKnowledge(store, () => [w], () => obs, () => "b1.8.1", clock, () => story), store, clock, story);
    }

    [Fact]
    public void A_body_opening_a_chest_is_live_while_open_and_its_last_look_stays_after_the_close()
    {
        var w = World((A, Blocks.Chest));
        var (k, store, clock, _) = Make(w);
        var watch = new ContainerWatch("maid_chan_102", w, store, clock) { Throttle = TimeSpan.Zero };
        w.Inventory.WindowOpened += watch.Opened;
        w.Inventory.WindowClosed += watch.Closed;
        w.Inventory.Changed += watch.Changed;

        Assert.Equal("unknown", k.Get(0, A)!.Source);
        w.Inventory.Open(3, WindowType.Chest, "Chest", 27);
        watch.Located(3, A);
        var items = Enumerable.Repeat(ItemStack.Empty, 27 + 36).ToArray();
        items[0] = S(4, 64);
        items[5] = S(265, 9);
        w.Inventory.SetWindowItems(3, items);
        var live = k.Get(0, A)!;
        Assert.Equal("live", live.Source);
        Assert.Equal("maid_chan_102", live.SeenBy);
        Assert.Equal(("chest", 27), (live.Kind, live.Size));
        Assert.Equal(new[] { 0, 5 }, live.Slots.Select(s => s.Slot));
        Assert.Equal("iron_ingot", live.Slots[1].Name);

        // a deposit's slot update shows at once (the live copy), then the close keeps the last look
        clock.Now += TimeSpan.FromSeconds(2);
        w.Inventory.SetSlot(3, 7, S(50, 12));
        Assert.Contains(k.Get(0, A)!.Slots, s => s.Slot == 7 && s.Name == "torch");
        w.Inventory.Close(3);
        clock.Now += TimeSpan.FromMinutes(3);
        var after = k.Get(0, A)!;
        Assert.Equal("memory", after.Source);
        Assert.True(after.Exact);
        Assert.Equal((T0 + TimeSpan.FromSeconds(2)).ToUnixTimeMilliseconds(), after.SeenAt);
        Assert.Equal(new[] { 0, 5, 7 }, after.Slots.Select(s => s.Slot));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void The_close_is_kept_even_when_it_comes_before_the_client_names_the_block()
    {
        var w = World((F, Blocks.Furnace));
        var (k, store, clock, _) = Make(w);
        var watch = new ContainerWatch("maid_chan", w, store, clock);
        w.Inventory.WindowOpened += watch.Opened;
        w.Inventory.WindowClosed += watch.Closed;
        w.Inventory.Open(4, WindowType.Furnace, "Furnace", 3);
        w.Inventory.SetSlot(4, 0, S(15, 5));   // input: iron ore
        w.Inventory.SetSlot(4, 1, S(263, 3));  // fuel: coal
        w.Inventory.SetSlot(4, 2, S(265, 2));  // output: iron ingots
        w.Inventory.Close(4);
        watch.Located(4, F); // the event pump ran late
        var v = k.Get(0, F)!;
        Assert.Equal(("furnace", 3, "memory"), (v.Kind, v.Size, v.Source));
        Assert.Equal(new[] { "iron_ore", "coal", "iron_ingot" }, v.Slots.OrderBy(s => s.Slot).Select(s => s.Name));
    }

    [Fact]
    public void Workbenches_and_non_container_blocks_are_not_containers()
    {
        var w = World((A, Blocks.Workbench), (B, Blocks.Stone));
        var (k, store, clock, _) = Make(w);
        var watch = new ContainerWatch("maid_chan", w, store, clock);
        w.Inventory.WindowOpened += watch.Opened;
        w.Inventory.Open(2, WindowType.Workbench, "Crafting", 9);
        watch.Located(2, A);
        Assert.Equal(0, store.Count);
        Assert.Null(k.Get(0, A));
        Assert.Null(k.Get(0, B));
        // an unloaded spot: nothing says what is there, so it is an unknown container (the viewer saw one)
        var far = k.Get(0, new BlockPos(500, 64, 500))!;
        Assert.Equal(("unknown", "chest", 27), (far.Source, far.Kind, far.Size));
        Assert.Empty(far.Slots);
        Assert.Null(far.SeenAt);
    }

    [Fact]
    public void A_double_chest_answers_from_either_half_with_54_slots()
    {
        var w = World((A, Blocks.Chest), (B, Blocks.Chest));
        var (k, store, clock, _) = Make(w);
        var watch = new ContainerWatch("maid_chan", w, store, clock);
        w.Inventory.WindowOpened += watch.Opened;
        w.Inventory.WindowClosed += watch.Closed;
        w.Inventory.Open(5, WindowType.Chest, "Large chest", 54);
        watch.Located(5, B); // clicked the east half: the look is keyed by the west half
        w.Inventory.SetSlot(5, 0, S(17, 40));
        w.Inventory.SetSlot(5, 53, S(264, 3));
        w.Inventory.Close(5);
        foreach (var p in new[] { A, B })
        {
            var v = k.Get(0, p)!;
            Assert.Equal(("double_chest", 54), (v.Kind, v.Size));
            Assert.Equal(new[] { A.X, A.Y, A.Z }, v.Pos);
            Assert.Equal(2, v.Cells.Count);
            Assert.Equal(new[] { 0, 53 }, v.Slots.Select(s => s.Slot));
        }
    }

    [Fact]
    public void Two_halves_looked_at_as_singles_are_joined_first_half_first()
    {
        var w = World((A, Blocks.Chest), (B, Blocks.Chest));
        var (k, store, _, _) = Make(w);
        var east = new ItemStack[27];
        Array.Fill(east, ItemStack.Empty);
        east[0] = S(4, 10);
        store.Record(new ContainerSighting(0, B, [B], Blocks.Chest, east, T0.ToUnixTimeMilliseconds(), "maid_chan_67"));
        var half = k.Get(0, A)!;
        Assert.Equal(("double_chest", 54, true), (half.Kind, half.Size, half.Partial));
        Assert.Equal(27, Assert.Single(half.Slots).Slot);

        var west = new ItemStack[27];
        Array.Fill(west, ItemStack.Empty);
        west[1] = S(3, 5);
        store.Record(new ContainerSighting(0, A, [A], Blocks.Chest, west, T0.ToUnixTimeMilliseconds() - 60_000, "maid_chan"));
        var both = k.Get(0, B)!;
        Assert.Null(both.Partial);
        Assert.Equal(new[] { 1, 27 }, both.Slots.Select(s => s.Slot));
        Assert.Equal(T0.ToUnixTimeMilliseconds() - 60_000, both.SeenAt); // the older half's look
        Assert.Equal("maid_chan", both.SeenBy);

        // a later 54-slot look replaces both single looks
        var all = new ItemStack[54];
        Array.Fill(all, ItemStack.Empty);
        all[40] = S(1, 1);
        store.Record(new ContainerSighting(0, A, [A, B], Blocks.Chest, all, T0.ToUnixTimeMilliseconds() + 1000, "maid_chan"));
        Assert.Equal(1, store.Count);
        Assert.Equal(40, Assert.Single(k.Get(0, B)!.Slots).Slot);
    }

    [Fact]
    public void A_count_only_source_wins_only_when_clearly_newer_and_different()
    {
        var w = World((A, Blocks.Chest));
        var (k, store, _, story) = Make(w);
        var look = new ItemStack[27];
        Array.Fill(look, ItemStack.Empty);
        look[3] = S(4, 20);
        long t = T0.ToUnixTimeMilliseconds();
        store.Record(new ContainerSighting(0, A, [A], Blocks.Chest, look, t, "maid_chan_102"));

        // the ledger right after the same look (a deposit's bookkeeping): the exact look stays
        story.View = new StoryChestView("colony-1", new P3(A.X, A.Y, A.Z), null, [new Stack(4, 0, 84)], T0 + TimeSpan.FromSeconds(5), 27, 25, "normal", null);
        var v = k.Get(0, A)!;
        Assert.Equal(("memory", true), (v.Source, v.Exact));
        Assert.Equal(3, Assert.Single(v.Slots).Slot);

        // a minute later and different: Story's ledger knows more (laid out from its totals)
        story.View = story.View with { KnownAt = T0 + TimeSpan.FromMinutes(1) };
        v = k.Get(0, A)!;
        Assert.Equal(("story", false, "colony-1"), (v.Source, v.Exact, v.SeenBy));
        Assert.Equal(new[] { 64, 20 }, v.Slots.Select(s => s.Count)); // 84 cobblestone as a full stack and the rest

        // newer but the same totals: the exact look still wins
        story.View = story.View with { Contents = [new Stack(4, 0, 20)] };
        Assert.Equal("memory", k.Get(0, A)!.Source);
    }

    [Fact]
    public void The_world_memory_answers_when_no_body_here_looked_and_live_beats_everything()
    {
        var db = AgentDb.Open(null);
        var rag = new RagIndex(db, backgroundEmbedding: false);
        var obs = new ObservationStore(rag);
        obs.Upsert(new Observation("chest", ObservationStore.KeyFor(A.X, A.Y, A.Z), "chest")
        {
            Bounds = Box.Of(A.X, A.Y, A.Z, B.X, B.Y, B.Z),
            Contents = [new ItemCount(265, "iron_ingot", 70)],
            ContentsAt = T0 - TimeSpan.FromHours(2),
            ContentsBy = "maid_chan_109",
            Dimension = 0,
        });
        var w = World((A, Blocks.Chest), (B, Blocks.Chest));
        var (k, store, clock, story) = Make(w, obs);
        var v = k.Get(0, B)!;
        Assert.Equal(("memory", false, "maid_chan_109"), (v.Source, v.Exact, v.SeenBy));
        Assert.Equal(("double_chest", 54), (v.Kind, v.Size));
        Assert.Equal(new[] { 64, 6 }, v.Slots.Select(s => s.Count));

        // a body has it open now: live, whatever the others say
        story.View = new StoryChestView("c", new P3(A.X, A.Y, A.Z), new P3(B.X, B.Y, B.Z), [new Stack(1, 0, 1)], T0 + TimeSpan.FromHours(1), 54, 53, "normal", null);
        var open = new ItemStack[54];
        Array.Fill(open, ItemStack.Empty);
        open[9] = S(266, 4);
        store.Record(new ContainerSighting(0, A, [A, B], Blocks.Chest, open, T0.ToUnixTimeMilliseconds() - 1_000_000, "maid_chan"), () => open);
        v = k.Get(0, A)!;
        Assert.Equal(("live", clock.Now.ToUnixTimeMilliseconds()), (v.Source, v.SeenAt));
        Assert.Equal("gold_ingot", Assert.Single(v.Slots).Name);
    }

    [Fact]
    public void The_box_summary_lists_known_containers_newest_first_with_top_items_and_is_capped()
    {
        var w = World((A, Blocks.Chest), (B, Blocks.Chest), (F, Blocks.Furnace));
        var (k, store, _, story) = Make(w);
        long t = T0.ToUnixTimeMilliseconds();
        var pair = new ItemStack[54];
        Array.Fill(pair, ItemStack.Empty);
        for (int i = 0; i < 7; i++) pair[i] = S(1 + i, 10 + i);
        pair[20] = S(1, 64);
        store.Record(new ContainerSighting(0, A, [A, B], Blocks.Chest, pair, t, "maid_chan"));
        store.Record(new ContainerSighting(0, F, [F], Blocks.Furnace, [S(15, 2), ItemStack.Empty, ItemStack.Empty], t + 5000, "maid_chan_67"));
        story.View = new StoryChestView("c", new P3(9, 64, 9), null, [new Stack(297, 0, 12)], T0 + TimeSpan.FromSeconds(1), 27, 26, "normal", "Food");
        w.SetBlock(9, 64, 9, Blocks.Chest, 0);

        var (items, capped) = k.InBox(0, new BlockPos(0, 0, 0), new BlockPos(15, 127, 15), 10);
        Assert.False(capped);
        Assert.Equal(new[] { "furnace", "chest", "double_chest" }, items.Select(s => s.Kind));
        var dbl = items[2];
        Assert.Equal((54, 8, 10 + 11 + 12 + 13 + 14 + 15 + 16 + 64), (dbl.Size, dbl.Used, dbl.Total));
        Assert.Equal(5, dbl.Top.Count);
        Assert.Equal(("stone", 74), (dbl.Top[0].Name, dbl.Top[0].Count));
        Assert.Equal("story", items[1].Source);

        var (few, cut) = k.InBox(0, new BlockPos(0, 0, 0), new BlockPos(15, 127, 15), 2);
        Assert.True(cut);
        Assert.Equal(2, few.Count);
        // outside the box: nothing
        Assert.Empty(k.InBox(0, new BlockPos(100, 0, 100), new BlockPos(110, 127, 110), 10).Items);
    }

    [Fact]
    public void The_store_is_bounded_and_drops_the_oldest_looks()
    {
        var store = new ContainerSightings { Capacity = 100 };
        for (int i = 0; i < 150; i++)
            store.Record(new ContainerSighting(0, new BlockPos(i, 64, 0), [new BlockPos(i, 64, 0)], Blocks.Chest, [S(4, 1)], i, "b"));
        Assert.True(store.Count <= 100);
        Assert.Null(store.Get(0, new BlockPos(0, 64, 0), 0));
        Assert.NotNull(store.Get(0, new BlockPos(149, 64, 0), 0));
    }

    // ---------------------------------------------------------------- the route

    private sealed class KnowledgeWorld(ContainerKnowledge k) : ISupervisorWorld
    {
        public IReadOnlyList<WorldColumnInfo> Index(int dim, int cx0, int cz0, int cx1, int cz1, int max) => [];
        public IReadOnlyList<ColumnPayloadEntry> Columns(int dim, IReadOnlyList<(int Cx, int Cz)> cols) => [];
        public IReadOnlyList<WorldBot> Bots() => [];
        public IReadOnlyList<WorldEntity> Entities(int? dim) => [];
        public WorldOverlays Overlays(int dim) => new(DateTimeOffset.UnixEpoch, dim, [], [], [], [], [], []);
        public ContainerView? Container(int dim, BlockPos pos) => k.Get(dim, pos);
        public (IReadOnlyList<ContainerSummary> Items, bool Capped) Containers(int dim, BlockPos min, BlockPos max, int limit) => k.InBox(dim, min, max, limit);
    }

    private static SupervisorRequest Get(string query, string? token = "s3cret")
    {
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = kv.IndexOf('=');
            q[kv[..i]] = kv[(i + 1)..];
        }
        return new SupervisorRequest("GET", "/api/world/containers", q, token);
    }

    [Fact]
    public async Task The_route_answers_one_container_or_a_box_and_refuses_bad_queries()
    {
        var w = World((A, Blocks.Chest), (F, Blocks.Stone));
        var (k, store, _, _) = Make(w);
        var look = new ItemStack[27];
        Array.Fill(look, ItemStack.Empty);
        look[0] = S(278, 1, 100);
        store.Record(new ContainerSighting(0, A, [A], Blocks.Chest, look, T0.ToUnixTimeMilliseconds(), "maid_chan"));
        await using var s = new SupervisorServer(new SupervisorOptions { Token = "s3cret" }, new FakeSupervisorData())
        {
            World = new SupervisorWorldRoutes(new KnowledgeWorld(k), new WorldRouteLimits(ContainersPerSec: 1000, ContainersBurst: 1000)),
        };
        Assert.Equal(401, (await s.HandleAsync(Get("dim=0&x=2&y=64&z=3", token: null))).Status);
        var r = await s.HandleAsync(Get("dim=0&x=2&y=64&z=3"));
        Assert.Equal(200, r.Status);
        var j = JsonDocument.Parse(r.Text).RootElement;
        Assert.Equal("chest", j.GetProperty("kind").GetString());
        Assert.Equal("memory", j.GetProperty("source").GetString());
        Assert.Equal("maid_chan", j.GetProperty("seen_by").GetString());
        Assert.Equal(T0.ToUnixTimeMilliseconds(), j.GetProperty("seen_at").GetInt64());
        var slot = j.GetProperty("slots")[0];
        Assert.Equal(1461, slot.GetProperty("durability").GetProperty("left").GetInt32());
        Assert.Equal(new[] { 2, 64, 3 }, j.GetProperty("pos").EnumerateArray().Select(e => e.GetInt32()));

        Assert.Equal(404, (await s.HandleAsync(Get("dim=0&x=6&y=64&z=3"))).Status); // stone
        Assert.Equal(400, (await s.HandleAsync(Get("dim=0&x=2&z=3"))).Status);
        Assert.Equal(400, (await s.HandleAsync(Get("dim=0&x0=0&z0=0&x1=900&z1=10"))).Status);
        var box = await s.HandleAsync(Get("dim=0&x0=0&z0=0&x1=15&z1=15&limit=5"));
        Assert.Equal(200, box.Status);
        var bj = JsonDocument.Parse(box.Text).RootElement;
        Assert.False(bj.GetProperty("capped").GetBoolean());
        var c = Assert.Single(bj.GetProperty("containers").EnumerateArray().ToList());
        Assert.Equal(1, c.GetProperty("used").GetInt32());
        Assert.Equal("diamond_pickaxe", c.GetProperty("top")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_route_is_rate_limited()
    {
        var w = World((A, Blocks.Chest));
        var (k, _, _, _) = Make(w);
        await using var s = new SupervisorServer(new SupervisorOptions { Token = "s3cret" }, new FakeSupervisorData())
        {
            World = new SupervisorWorldRoutes(new KnowledgeWorld(k), new WorldRouteLimits(ContainersPerSec: 0.01, ContainersBurst: 2)),
        };
        Assert.Equal(200, (await s.HandleAsync(Get("x=2&y=64&z=3"))).Status);
        Assert.Equal(200, (await s.HandleAsync(Get("x=2&y=64&z=3"))).Status);
        var limited = await s.HandleAsync(Get("x=2&y=64&z=3"));
        Assert.Equal(429, limited.Status);
    }
}
