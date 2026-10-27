using System.Text.Json;
using MineClaw.Agent.Budget;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Tests;

public class ObservationTests
{
    private static (AgentDb Db, RagIndex Index, ObservationStore World, FakeEmbedder Emb, ManualTime Time) Open()
    {
        var time = new ManualTime();
        var db = AgentDb.Open(null);
        var emb = new FakeEmbedder();
        var idx = new RagIndex(db, new RagOptions(), emb, time: time, backgroundEmbedding: false);
        return (db, idx, new ObservationStore(idx, time), emb, time);
    }

    private static Observation Chest(int x, int y, int z, params ItemCount[] items) =>
        new("chest", ObservationStore.KeyFor(x, y, z), $"chest at {x},{y},{z}: " + string.Join(", ", items.Select(i => $"{i.Name} x{i.Count}")))
        {
            Bounds = Box.Point(x, y, z),
            Contents = items,
        };

    [Fact]
    public void Upsert_IsIdempotent_TracksFreshness_AndFindsItems()
    {
        var (db, _, world, _, time) = Open();
        using var _d = db;
        world.Upsert(Chest(10, 64, -3, new ItemCount(265, "iron_ingot", 12), new ItemCount(4, "cobblestone", 64)));
        time.Advance(TimeSpan.FromMinutes(5));
        var row = world.Upsert(Chest(10, 64, -3, new ItemCount(265, "iron_ingot", 12), new ItemCount(4, "cobblestone", 30)));
        Assert.Equal((2, 1), (row.SeenCount, world.Count("chest")));
        Assert.Equal(row.FirstSeen.AddMinutes(5), row.LastSeen);
        world.Upsert(Chest(0, 64, 0, new ItemCount(265, "iron_ingot", 3)));

        var hits = world.FindItemInContainers("iron ingots");          // name resolved via the item table
        Assert.Equal([12, 3], hits.Select(h => h.Count));
        Assert.Equal("10,64,-3", hits[0].Container.Where);
        Assert.Equal(30, world.Contents(row.Id).Single(c => c.ItemId == 4).Count);   // snapshot replaced

        var r = WorldTools.WhereIs(world, "iron_ingot", null, new BlockPos(0, 64, 0), time.GetUtcNow().AddMinutes(5));
        Assert.Equal("iron_ingot x12: chest@10,64,-3 10m E (opened 5m ago, may have changed); x3: chest@0,64,0 0m (opened 5m ago, may have changed) | total 15", r.Text);

        Assert.True(world.Remove("chest", "0,64,0"));                    // block gone: tombstoned
        Assert.Single(world.FindItemInContainers(265));
        Assert.NotNull(world.Get("chest", "0,64,0", includeRemoved: true)!.Removed);
        Assert.Null(world.Get("chest", "0,64,0"));
    }

    [Fact]
    public void TaughtEntries_OutrankAuto_AndAreOnlyAnnotated()
    {
        var (db, idx, world, _, _) = Open();
        using var _d = db;
        world.Upsert(new Observation("farm", "5,64,5", "robin's sugar cane farm, harvest the top two") { Source = ObservationSource.Taught, Bounds = Box.Point(5, 64, 5) });
        world.Upsert(new Observation("farm", "5,64,5", "sugar cane farm, 6 canes") { Bounds = Box.Point(5, 64, 5) });
        world.Upsert(new Observation("farm", "9,64,9", "sugar cane farm, 4 canes") { Bounds = Box.Point(9, 64, 9) });
        var taught = world.Get("farm", "5,64,5")!;
        Assert.Equal(ObservationSource.Taught, taught.Source);
        Assert.Equal("robin's sugar cane farm, harvest the top two", taught.Text);
        Assert.Equal("sugar cane farm, 6 canes", taught.Annotation);

        var hits = idx.Search(new RagQuery { Text = "sugar cane farm", Kinds = ["world"] });
        Assert.Equal("obs:farm:5,64,5", hits[0].Ref);                   // taught weight beats auto
        Assert.True(world.Remove("farm", "5,64,5"));                     // indexer can't delete it...
        Assert.NotNull(world.Get("farm", "5,64,5"));                     // ...only annotate
        Assert.StartsWith("not seen here", world.Get("farm", "5,64,5")!.Annotation);
    }

    [Fact]
    public async Task Batching_Nearest_ListKinds_AndEmbeddingPolicy()
    {
        var (db, idx, world, emb, _) = Open();
        using var _d = db;
        int n = world.UpsertMany(Enumerable.Range(0, 50).Select(i =>
            new Observation(i % 2 == 0 ? "chest" : "furnace", ObservationStore.KeyFor(i, 64, 0), $"block {i}") { Bounds = Box.Point(i, 64, 0) })
            .Append(new Observation("tree", "3,64,3", "oak tree, 4 logs") { Bounds = Box.Point(3, 64, 3) }));
        Assert.Equal(51, n);
        Assert.Equal(["3,64,0", "5,64,0"], world.Nearest("furnace", 4, 64, 0, 2).Select(t => t.Row.Key));
        var kinds = world.ListKinds(Box.Of(0, 60, -5, 9, 70, 5));
        Assert.Equal((5, 5, 1), (kinds["chest"], kinds["furnace"], kinds["tree"]));

        await idx.FlushEmbeddingsAsync();
        Assert.Equal(1, emb.Texts);                                      // chest/furnace churn: FTS + columns only
        world.Upsert(new Observation("tree", "3,64,3", "oak tree, 6 logs") { Bounds = Box.Point(3, 64, 3) });
        await idx.FlushEmbeddingsAsync();
        Assert.Equal(1, emb.Texts);                                      // count change isn't material
        world.Upsert(new Observation("tree", "3,64,3", "birch tree, 6 logs") { Bounds = Box.Point(3, 64, 3) });
        await idx.FlushEmbeddingsAsync();
        Assert.Equal(2, emb.Texts);
    }

    [Fact]
    public async Task WhereIsTool_Registered_ForCheapTier()
    {
        var (rt, _, _) = TestRuntime.Create(position: () => new BlockPos(0, 64, 0));
        using var _r = rt;
        rt.Observations.Upsert(new Observation("furnace", "2,64,0", "furnace") { Bounds = Box.Point(2, 64, 0) });
        Assert.True(rt.Tools.TryGet("where_is", out var t) && t.AvailableToCheapTier);
        var r = await rt.Tools.ExecuteAsync("where_is", JsonSerializer.SerializeToElement(new { kind = "furnaces" }), default);
        Assert.StartsWith("furnace@2,64,0 2m", r.Text);
        Assert.Equal("within 32: furnace x1", (await rt.Tools.ExecuteAsync("where_is", JsonSerializer.SerializeToElement(new { }), default)).Text);
    }
}

public class CacheLayoutTests
{
    [Fact]
    public async Task RequestPrefix_IsByteIdentical_AcrossTurns_ExceptNewestMessage()
    {
        var (rt, llm, _) = TestRuntime.Create(position: () => new BlockPos(1, 64, 1));
        using var _r = rt;
        rt.Memory.SetWaypoint("melon farm", 1, 64, 2, "east side");
        rt.Tools.Register("look", "Look.", MineClaw.Agent.Tools.ToolSchema.Empty, (_, _) => Task.FromResult(MineClaw.Agent.Tools.ToolResult.Success("grass")));
        int turn = 0;
        rt.Agent.StateHeader = () => $"pos 1,64,1 hp 20/20 t{turn}";   // changes every turn: must only appear in the newest message
        llm.ThenTool("look", new { }).ThenText("ok").ThenText("sure").ThenText("yep");

        for (turn = 0; turn < 3; turn++)
            await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Alice", $"clawd mine near the melon farm please {turn}")]), default);

        var reqs = llm.Requests.Where(r => r.Tier == ModelTier.Main).ToList();
        Assert.Equal(4, reqs.Count);
        for (int i = 1; i < reqs.Count; i++)
        {
            var prev = reqs[i - 1];
            var cur = reqs[i];
            Assert.Equal(Snapshot.Of(prev.System), Snapshot.Of(cur.System));
            Assert.Equal(Snapshot.Of(prev.Tools), Snapshot.Of(cur.Tools));
            // Everything the previous request sent is an exact prefix of this one (append-only history).
            Assert.Equal(Snapshot.Of(prev.Messages), Snapshot.Of(cur.Messages.Take(prev.Messages.Count)));
        }
        Assert.All(reqs, r => Assert.DoesNotContain("wp melon farm", Snapshot.Of(r.System)));  // retrieved data never in system
        Assert.All(reqs, r => Assert.DoesNotContain("pos 1,64,1", Snapshot.Of(r.System)));
        Assert.Contains("[memory]", reqs[0].Messages[^1].Text);           // auto-retrieved block rides in the newest user message
        Assert.Contains("wp melon farm", reqs[0].Messages[^1].Text);
        Assert.DoesNotContain("wp melon farm", reqs[3].Messages[^1].Text); // still in the window: deduped
        Assert.True(reqs[0].System[0].Cache && reqs[0].System[1].Cache);

        // Cache hit ratio per call (FakeLlm usage: 100 in, 1000 cache read).
        Assert.Equal(1000.0 / 1100, rt.Usage.LastCacheHitRatio, 3);
        Assert.Equal(1000.0 / 1100, rt.Usage.Today.CacheHitRatio, 3);
        Assert.Contains("cache 91%", rt.Usage.Summary());
    }

    [Fact]
    public async Task CoreKnowledgeOnly_InCachedPrefix_RestRetrievable()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("k.md"), "# K\n<!-- core -->\n## Core\n- never dig the block you stand on\n<!-- /core -->\n## Cactus\ncactus only grows on sand with no neighbours\n");
        var (rt, llm, _) = TestRuntime.Create(o => o.KnowledgeDirectory = dir.Path);
        using var _r = rt;
        llm.ThenText("hi");
        await rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Alice", "how do i grow cactus")]), default);
        var system = llm.Requests[0].System[0].Text;
        Assert.Contains("never dig the block you stand on", system);
        Assert.DoesNotContain("grows on sand", system);
        Assert.Contains("grows on sand", llm.Requests[0].Messages[^1].Text);   // retrieved into this turn instead
        Assert.Equal(1, rt.KnowledgeChunks);
    }

    [Fact]
    public void AutoTtl_UsesOneHour_OnlyDuringActiveSession()
    {
        var time = new ManualTime();
        var (rt, llm, _) = TestRuntime.Create(o => o.Llm.StaticPrefixTtl = "auto", time);
        using var _r = rt;
        Assert.Equal("5m", rt.Agent.SystemPrompt[0].Ttl);
        llm.ThenText("hi");
        rt.Agent.HandleBatchAsync(new EventBatch([GameEvent.Chat("Alice", "hi clawd")]), default).GetAwaiter().GetResult();
        Assert.Equal("1h", llm.Requests[0].System[0].Ttl);
        Assert.Equal("5m", llm.Requests[0].System[1].Ttl);
        time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal("5m", rt.Agent.SystemPrompt[0].Ttl);
    }
}
