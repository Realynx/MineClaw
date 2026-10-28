using MineClaw.Agent.Budget;
using MineClaw.Agent.Rag;
using MineClaw.Bot;
using MineClaw.Bot.Memory;
using MineClaw.Hive.Budget;
using MineClaw.Hive.Db;
using MineClaw.Hive.Hierarchy;
using MineClaw.Hive.Hierarchy.Bus;
using MineClaw.World;
using WPos = MineClaw.World.BlockPos;

namespace MineClaw.Hive.Tests;

/// <summary>
/// One memory for every hive: main, Opus hives, DeepSeek hives and their bodies share one store (the SQLite file), one groomer
/// (main's), one placed-block table and one budget. "Hive" is a brain loop, not a memory boundary.
/// </summary>
public class SharedMemoryTests
{
    /// <summary>Main's groomer stand-in: marks what it touched, so a test can tell main groomed a write.</summary>
    private sealed class MainGroomer : IDocGroomer
    {
        public int Calls;
        public IReadOnlyList<RagDoc> Groom(RagDoc doc) { Interlocked.Increment(ref Calls); return [doc with { Title = "groomed: " + doc.Title }]; }
    }

    private sealed class Hive(string name, AgentDb db, RagIndex rag, MemoryBusClient client)
    {
        public string Name => name;
        public AgentDb Db => db;
        public RagIndex Rag => rag;
        public MemoryBusClient Client => client;
        public int Invalidations;
    }

    private sealed class World : IDisposable
    {
        public string Path { get; }
        public AgentDb MainDb { get; }
        public RagIndex MainRag { get; }
        public MainGroomer Groomer { get; } = new();
        public HiveBus Bus { get; } = new("maid_chan");
        public MemoryBusHost Host { get; }
        private readonly List<IDisposable> _own = [];

        public World()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mineclaw-hive-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Path = System.IO.Path.Combine(dir, "mineclaw.db");
            MainDb = AgentDb.Open(Path);
            _ = new HiveStore(MainDb, "maid_chan"); // clone_id provenance columns (as in production)
            MainRag = new RagIndex(MainDb, backgroundEmbedding: false) { Groomer = Groomer };
            Host = new MemoryBusHost(MainRag, MainDb, Bus);
            Bus.MainHandler = (e, ct) => e.Type == BusTypes.MemWrite ? Host.HandleAsync(e, ct) : Task.FromResult(BusReply.Fail("no"));
        }

        /// <summary>Another hive process: its own connection to the same file, writes through main over the bus.</summary>
        public Hive Join(string name, string? brain)
        {
            var db = AgentDb.Open(Path);
            _own.Add(db);
            var rag = new RagIndex(db, backgroundEmbedding: false); // no local groomer: main's is the one chain
            Hive? hive = null;
            var token = Bus.Register(name, (e, _) =>
            {
                if (e.Type == BusTypes.MemChanged && hive != null) { Interlocked.Increment(ref hive.Invalidations); hive.Client.OnChanged(); }
                return Task.FromResult(new BusReply(true));
            });
            var client = new MemoryBusClient(async (env, _) => await Bus.RouteAsync(env, name), name, token, db, new MemProvenance(name, name, brain));
            rag.WriteThrough = client.Write;
            hive = new Hive(name, db, rag, client);
            return hive;
        }

        public void Dispose()
        {
            foreach (var d in _own) d.Dispose();
            MainDb.Dispose();
        }
    }

    [Fact]
    public void Two_hives_write_and_read_each_others_memory_instantly()
    {
        using var w = new World();
        var a = w.Join("maid_chan_101", "opus");
        var b = w.Join("maid_chan_103", "deepseek");

        long idA = a.Rag.Upsert(new RagDoc("note:obsidian", "note", "obsidian stash", "the obsidian stash is under the cherry tree") { Source = "note" });
        Assert.True(idA > 0);
        // B sees A's write at once (same file; no copy, no sync job) and was told to reload right away.
        var seen = b.Rag.GetByRef("note:obsidian");
        Assert.NotNull(seen);
        Assert.Equal(idA, seen!.Id);
        Assert.Equal(1, b.Invalidations);
        Assert.Contains(b.Rag.Search(new RagQuery { Text = "obsidian cherry" }), h => h.Ref == "note:obsidian");

        long idB = b.Rag.Upsert(new RagDoc("note:wheat", "note", "wheat field", "the wheat field is north of the main house") { Source = "note" });
        Assert.Equal(idB, a.Rag.GetByRef("note:wheat")?.Id);
        Assert.Equal(1, a.Invalidations);
        Assert.Contains(w.MainRag.Search(new RagQuery { Text = "wheat north" }), h => h.Ref == "note:wheat"); // main too
        Assert.Equal(2, a.Client.Forwarded + b.Client.Forwarded);
    }

    [Fact]
    public void A_deepseek_hive_write_goes_through_mains_grooming_with_provenance()
    {
        using var w = new World();
        var ds = w.Join("maid_chan_101", "deepseek");
        ds.Rag.Upsert(new RagDoc("note:melon", "note", "melons", "melons grow on the east island") { Source = "note" });
        Assert.Equal(1, w.Groomer.Calls);                                    // main's chain, once
        Assert.StartsWith("groomed: ", w.MainRag.GetByRef("note:melon")!.Title);
        string? by;
        lock (w.MainDb.Gate) by = w.MainDb.Scalar("SELECT clone_id FROM docs WHERE ref = 'note:melon'") as string;
        Assert.Equal("maid_chan_101/deepseek", by);                         // provenance is metadata on the one store

        // Inside the hive's own transaction it never waits on main (lock order): written locally, still the same store.
        ds.Db.Tx(() => ds.Rag.Upsert(new RagDoc("note:tx", "note", "tx", "written inside a transaction") { Source = "note" }));
        Assert.Equal(1, ds.Client.Fallbacks);
        Assert.NotNull(w.MainRag.GetByRef("note:tx"));
    }

    private sealed class NoRegions : IProtectedRegionSource { public IReadOnlyList<(WPos Min, WPos Max)> ProtectedRegions() => []; }
    private sealed class NoReserved : IReservedItemSource { public IReadOnlyCollection<string> ReservedItemNames() => []; }

    [Fact]
    public void Blocks_any_of_our_hives_placed_are_ours_to_mine_in_every_hive()
    {
        using var w = new World();
        using var dbB = AgentDb.Open(w.Path);
        var logA = new PlacedBlockLog();
        var logB = new PlacedBlockLog();
        using var storeA = new PlacedBlockStore(w.MainDb, logA);
        using var storeB = new PlacedBlockStore(dbB, logB);
        var p = new BlockPos(10, 64, -3);
        logA.Note(p, 0, 4, "maid_chan_101", DateTimeOffset.UtcNow); // a DeepSeek hive's body placed cobble
        storeA.Flush();
        storeB.Sync(force: true);
        Assert.True(logB.IsPlaced(p, 0, 4));
        Assert.Equal("maid_chan_101", logB.Get(p, 0)!.Bot);
        // Main's protection: self-placed (by any of our hives) overrides every soft source.
        var protection = new Protection(new BotOptions(), new NoRegions(), new NoReserved()) { Placed = logB };
        Assert.True(protection.OverridesSoft(p, 4));
    }

    [Fact]
    public void One_budget_across_every_hive()
    {
        var ledger = new BudgetLedger(new BudgetOptions { DailyCapUsd = 1.00m, MonthlyCapUsd = 30m }, new HiveBudgetConfig(), () => (0.30m, 0.30m));
        Assert.True(ledger.Authorize("maid_chan_101", 0.01m).Ok);
        ledger.Record("maid_chan_101", 0.40m);   // an Opus hive
        ledger.Record("maid_chan_103", 0.35m);   // a DeepSeek hive (its spend is priced and counted the same way)
        var (today, _) = ledger.HiveSpend();
        Assert.Equal(1.05m, today);              // main 0.30 + both hives: one total
        Assert.True(ledger.AtRecallLine(out _)); // one cap for all of them
        Assert.False(ledger.Authorize("maid_chan_105", 0.01m).Ok);
    }
}
