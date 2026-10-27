using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>
/// Live 10-27 16:33 ("ooh new cobble build by the gen, what is it?") and 21:20 ("what's that little cobble build with torches
/// by the cactus farm?"): right after a restart main asked about long-standing builds. The classifier flagged them as unknown
/// for the first time (no scenery-ledger row), and their columns' hashes differed from the baseline because the gen / the
/// cactus farm change every visit, so "unchanged since last seen" failed and they read as new. Only blocks that weren't in
/// the remembered world are news; hive-built blocks never are.
/// </summary>
public sealed class RejoinSceneryTests : IDisposable
{
    private readonly ITestOutputHelper output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mineclaw-rejoin-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.Zero));
    private string DbPath => Path.Combine(_dir, "test.db");

    public RejoinSceneryTests(ITestOutputHelper output)
    {
        this.output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static void Fill(Dictionary<BlockPos, (int, int)> w, int x0, int y0, int z0, int x1, int y1, int z1, int id)
    {
        for (int x = x0; x <= x1; x++)
        for (int y = y0; y <= y1; y++)
        for (int z = z0; z <= z1; z++)
            w[new BlockPos(x, y, z)] = (id, 0);
    }

    /// <summary>An island with a little cobble build with torches (the "cactus side build": cobblestone 27, torch 4).</summary>
    private static Dictionary<BlockPos, (int, int)> World()
    {
        var w = new Dictionary<BlockPos, (int, int)>();
        Fill(w, -6, 60, -6, 6, 62, 6, Blocks.Dirt);
        Fill(w, -6, 63, -6, 6, 63, 6, Blocks.Grass);
        Fill(w, 2, 64, 2, 4, 66, 4, Blocks.Cobblestone); // 27 cobblestone
        foreach (var (x, z) in new[] { (1, 3), (5, 3), (3, 1), (3, 5) }) w[new BlockPos(x, 64, z)] = (Blocks.Torch, 0);
        return w;
    }

    [Fact]
    public void A_long_standing_build_flagged_for_the_first_time_after_a_restart_is_not_news()
    {
        var world = World();
        using (var s = new MemorySession(DbPath, _clock))
        {
            s.Load(world);
            s.Run();
            // The old classifier never called it unknown (no ledger row) — only the remembered world has its blocks.
            s.Db.Exec("DELETE FROM scenery");
            s.Db.Exec("DELETE FROM observations");
        }
        // Its column changed elsewhere while the bot was away (the cactus farm grew / the gen was mined): the hash differs.
        world[new BlockPos(12, 40, 12)] = (Blocks.Sand, 0); // same column (0,0) as the build, far from it
        world[new BlockPos(13, 40, 12)] = (Blocks.Sand, 0);
        _clock.Advance(TimeSpan.FromMinutes(5));
        using (var s = new MemorySession(DbPath, _clock))
        {
            s.Load(world);
            s.Run();
            output.WriteLine("announced: " + string.Join(" | ", s.Announced.Select(a => a.Description)));
            Assert.Equal(BaselineState.Changed, s.Memory.State(0, 0, 0)); // the build's own column changed
            Assert.Empty(s.Announced);
            Assert.Single(s.WStore.Scenery(0)); // recorded silently: asked at most once from now on
        }
        // And again after another restart (index, restart, re-index).
        _clock.Advance(TimeSpan.FromMinutes(5));
        using (var s = new MemorySession(DbPath, _clock))
        {
            s.Load(world);
            s.Run();
            Assert.Empty(s.Announced);
        }
    }

    [Fact]
    public void A_build_that_appeared_while_offline_is_still_announced_once()
    {
        var world = World();
        foreach (var p in world.Keys.Where(p => p.Y >= 64).ToList()) world.Remove(p); // bare island first
        using (var s = new MemorySession(DbPath, _clock)) { s.Load(world); s.Run(); Assert.Empty(s.Announced); }
        world = World(); // a player built it while the bot was away
        _clock.Advance(TimeSpan.FromHours(1));
        using (var s = new MemorySession(DbPath, _clock)) { s.Load(world); s.Run(); Assert.Single(s.Announced); }
        _clock.Advance(TimeSpan.FromHours(1));
        using (var s = new MemorySession(DbPath, _clock)) { s.Load(world); s.Run(); Assert.Empty(s.Announced); }
    }

    [Fact]
    public void A_build_the_hive_placed_itself_is_never_announced()
    {
        var world = World();
        using var s = new MemorySession(DbPath, _clock); // fresh db: the blocks are new to the remembered world
        foreach (var (p, b) in world)
            if (p.Y >= 64) s.World.Placed.Note(p, 0, b.Item1, "maid_chan_67", _clock.GetUtcNow());
        s.Load(world);
        s.Run();
        Assert.Empty(s.Announced);
    }

    [Fact]
    public void A_known_build_that_re_clusters_differently_stays_quiet_when_its_blocks_were_already_there()
    {
        var world = World();
        using (var s = new MemorySession(DbPath, _clock)) { s.Load(world); s.Run(); Assert.Single(s.Announced); }
        // A classifier/recluster change: the same old blocks now read as a materially different mix (stored row says torches only).
        using (var db = MineClaw.Agent.Rag.AgentDb.Open(DbPath)) db.Exec("UPDATE scenery SET blocks = 'torch=4'");
        world[new BlockPos(12, 40, 12)] = (Blocks.Sand, 0); // same column (0,0) as the build, far from it
        _clock.Advance(TimeSpan.FromMinutes(5));
        using (var s = new MemorySession(DbPath, _clock)) { s.Load(world); s.Run(); Assert.Empty(s.Announced); }
    }
}
