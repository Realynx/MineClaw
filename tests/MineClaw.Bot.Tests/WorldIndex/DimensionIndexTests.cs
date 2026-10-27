using MineClaw.Agent.Rag;
using MineClaw.Bot.WorldIndex;
using MineClaw.Data;
using MineClaw.World;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests.WorldIndex;

/// <summary>Dimension switches on the memory side: indexer/tracker reset, portal pairing, digest scope, cross-dimension goto.</summary>
public class DimensionIndexTests(ITestOutputHelper output)
{
    /// <summary>What the client does on 0x09 with another dimension: world dropped, dimension set, new chunks arrive.</summary>
    private static void Travel(IndexFixture f, int dim, Vec3d arrive)
    {
        f.World.Clear();
        f.World.Entities.Clear();
        f.World.Dimension = dim;
        const int size = 64;
        f.World.ApplyChunkRegion(-32, 0, -32, size, 128, size, new byte[size * 128 * size], null, null, null);
        f.World.Player.ServerTeleport(arrive, arrive.Y + 1.62, 0, 0, true);
    }

    /// <summary>A 2x3 portal sheet along X at (x..x+1, y..y+2, z) in an obsidian frame.</summary>
    private static void Portal(IndexFixture f, int x, int y, int z)
    {
        for (int fx = x - 1; fx <= x + 2; fx++) { f.Set(fx, y - 1, z, Blocks.Obsidian); f.Set(fx, y + 3, z, Blocks.Obsidian); }
        for (int fy = y; fy <= y + 2; fy++) { f.Set(x - 1, fy, z, Blocks.Obsidian); f.Set(x + 2, fy, z, Blocks.Obsidian); }
        for (int px = x; px <= x + 1; px++)
        for (int py = y; py <= y + 2; py++)
            f.Set(px, py, z, Blocks.Portal);
    }

    [Fact]
    public void Switch_resets_indexer_state_keeps_stored_rows_and_uses_a_hazard_summary_instead_of_islands()
    {
        using var f = new IndexFixture();
        f.Island();
        f.Set(2, 64, 2, Blocks.Chest);
        f.Indexer.PumpAll();
        Assert.NotNull(f.Store.Get("chest", "2,64,2", dimension: 0));
        Assert.True(f.Store.Count(IndexKinds.Island, 0) >= 1);
        Assert.True(f.Indexer.IsProtected(new BlockPos(2, 64, 2), Blocks.Chest));

        f.Clock.Seconds(2);
        Travel(f, WorldDim.Nether, new Vec3d(0.5, 64, 0.5));
        f.Fill(-8, 63, -8, 8, 63, 8, Blocks.Netherrack);     // natural terrain: no "unknown build"
        f.Fill(-6, 59, -6, -2, 62, -2, Blocks.Netherrack);
        f.Set(2, 64, 2, Blocks.Chest);                         // same coordinates, other dimension
        f.Set(5, 40, 5, Blocks.Lava); f.Set(5, 41, 5, Blocks.Lava);
        for (int x = 0; x < 4; x++) f.Set(x, 20, 9, Blocks.Lava); // lava sea
        f.Set(-3, 64, -3, Blocks.Fire);
        f.Indexer.PumpAll();

        Assert.Equal(WorldDim.Nether, f.Indexer.Dimension);
        Assert.Equal(1, f.Indexer.Stats.DimensionSwitches);
        var census = f.Indexer.Census();
        output.WriteLine(string.Join(", ", census.Select(kv => $"{kv.Key} {kv.Value}")));
        Assert.False(census.ContainsKey(IndexKinds.Island));
        Assert.False(census.ContainsKey(IndexKinds.UnknownStructure));
        Assert.Equal(1, census[IndexKinds.Chest]);              // only this dimension's entries live in memory
        Assert.Equal(4, census[IndexKinds.Hazard]);              // the loaded -32..31 square spans four 64x64 areas
        var hazard = f.Store.Get(IndexKinds.Hazard, "0,0", dimension: -1)!;
        output.WriteLine(hazard.Text);
        Assert.Contains("nether area 0,0,0..63,127,63", hazard.Text);
        Assert.Contains("lava sea", hazard.Text);
        Assert.Contains("lava 2 above it (y40-41)", hazard.Text);
        Assert.Contains("fire 1", f.Store.Get(IndexKinds.Hazard, "-1,-1", dimension: -1)!.Text);

        // Stored overworld rows are untouched (not tombstoned by the nether scan of the same coordinates).
        Assert.NotNull(f.Store.Get("chest", "2,64,2", dimension: 0));
        Assert.NotNull(f.Store.Get("chest", "2,64,2", dimension: -1));
        Assert.Equal(2, f.Store.Count("chest"));
        Assert.True(f.Store.Count(IndexKinds.Island, 0) >= 1);
        Assert.Equal(0, f.Store.Count(IndexKinds.Island, -1));
        Assert.True(f.Indexer.IsProtected(new BlockPos(2, 64, 2), Blocks.Chest)); // the nether chest now

        // Back home: overworld rows are reconciled, not duplicated.
        f.Clock.Seconds(2);
        Travel(f, WorldDim.Overworld, new Vec3d(0.5, 65, 0.5));
        f.Island();
        f.Set(2, 64, 2, Blocks.Chest);
        f.Indexer.PumpAll();
        Assert.Equal(2, f.Store.Count("chest"));
        Assert.False(f.Indexer.Census().ContainsKey(IndexKinds.Hazard));
        Assert.True(f.Indexer.Census().ContainsKey(IndexKinds.Island));
    }

    [Fact]
    public void Going_through_a_portal_records_entry_and_exit_as_a_linked_pair()
    {
        using var f = new IndexFixture();
        f.Island();
        Portal(f, 0, 65, 5);
        f.World.Player.ServerTeleport(new Vec3d(0.5, 65, 5.5), 66.62, 0, 0, true); // standing in the sheet
        f.Indexer.PumpAll();
        var entry = f.Store.Get(IndexKinds.Portal, "0,65,5", dimension: 0);
        Assert.NotNull(entry);
        output.WriteLine(entry!.Text);
        Assert.Contains("step in at 0,65,5", entry.Text);
        Assert.True(f.Indexer.IsProtected(new BlockPos(-1, 65, 5), Blocks.Obsidian)); // the frame
        Assert.Empty(f.Store.Portals.All());

        f.Clock.Seconds(4);
        Travel(f, WorldDim.Nether, new Vec3d(8.5, 70, -3.5));
        Portal(f, 8, 70, -4); // (the client puts us inside the exit portal)
        f.World.Player.ServerTeleport(new Vec3d(8.5, 70, -3.5), 71.62, 0, 0, true);
        f.Indexer.PumpAll();

        var link = Assert.Single(f.Store.Portals.All());
        output.WriteLine($"{link.A} <-> {link.B}");
        Assert.Equal(new PortalEnd(0, "0,65,5", 0, 65, 5), link.A);
        Assert.Equal(new PortalEnd(-1, "8,70,-4", 8, 70, -4), link.B);
        Assert.Equal(1, f.Indexer.Stats.PortalLinks);
        Assert.NotNull(f.Store.Get(IndexKinds.Portal, "8,70,-4", dimension: -1));
        // where_is shows where it leads; planning a trip back uses it.
        f.Rag.CurrentDimension = () => f.World.Dimension;
        var w = Agent.Rag.WorldTools.WhereIs(f.Store, null, "portal", new Agent.Memory.BlockPos(8, 70, -3), f.Clock.GetUtcNow()).Text;
        output.WriteLine(w);
        Assert.Contains("-> overworld 0,65,5", w);
        var back = f.Store.Portals.Plan(-1, (8, 70, -3), 0, (2, 64, 2))!;
        Assert.Equal("8,70,-4", back.Entry.Pos);

        // A death respawn into the overworld (not through a portal) links nothing.
        f.Clock.Seconds(60);
        Travel(f, WorldDim.Overworld, new Vec3d(30.5, 65, 30.5));
        f.Indexer.PumpAll();
        Assert.Single(f.Store.Portals.All());
    }

    [Fact]
    public void Tracker_switch_closes_sessions_keeps_them_stored_and_the_digest_reports_only_the_current_dimension()
    {
        using var a = new ActivityFixture();
        var dig = a.F.Generator(-3, 64, 2);
        a.Step();
        var since = a.F.Clock.GetUtcNow();
        int kiwi = a.Player("kiwi", 0.5, 64, 3.5);
        a.Step();
        for (int i = 0; i < 8; i++)
        {
            a.Look(kiwi, dig);
            a.Events.Swing(kiwi);
            a.World.SetBlock(dig.X, dig.Y, dig.Z, 0, 0);
            a.Step(0.5);
            a.World.SetBlock(dig.X, dig.Y, dig.Z, Blocks.Cobblestone, 0);
            a.Step(4);
        }
        var before = a.Tracker.Digest.Since(since);
        output.WriteLine(before);
        Assert.Contains("kiwi mined", before);

        a.World.Dimension = WorldDim.Nether;
        a.Step();
        Assert.Equal(1, a.Tracker.DimensionSwitches);
        Assert.Equal(WorldDim.Nether, a.Tracker.Dimension);
        Assert.Equal("", a.Tracker.Digest.Since(since));          // overworld work isn't "what happened here"
        var stored = Assert.Single(a.Log.Rollups(dimension: 0));   // the session was closed and stored, with its dimension
        Assert.Contains("kiwi mined", stored.Text);
        Assert.Empty(a.Log.Rollups(dimension: -1));
        Assert.Contains("(overworld)", a.Log.Ask("what did kiwi do today")); // asking still works, tagged
        Assert.Equal(0, a.F.Db.ScalarLong("SELECT count(*) FROM activity_log WHERE dimension <> 0"));

        a.World.Dimension = WorldDim.Overworld;
        a.Step();
        Assert.Contains("kiwi mined", a.Tracker.Digest.Since(since)); // back home: stored data is still there
    }

    [Fact]
    public async Task Goto_a_waypoint_in_the_other_dimension_uses_a_linked_portal_or_fails_honestly()
    {
        var (h, c) = TestBot.Create();
        using var _ = h;
        c.World.Dimension = WorldDim.Nether;
        Assert.True((await h.Run("remember_waypoint", new { name = "fortress", x = 100, y = 70, z = -40 })).Ok);
        Assert.Equal(-1, h.Runtime.Memory.GetWaypoint("fortress")!.Dimension);
        c.World.Dimension = WorldDim.Overworld;
        Assert.Null(h.MemoryAdapter.Resolve("fortress")); // never nether coordinates in the overworld

        var none = await h.Run("goto", new { to = "fortress" });
        Assert.False(none.Ok);
        Assert.Equal("no known portal to nether", none.Text);
        Assert.DoesNotContain(c.CallList, x => x.StartsWith("goto"));

        h.Runtime.Observations.Portals.Link(new PortalEnd(0, "3,65,3", 3, 65, 3), new PortalEnd(-1, "12,70,-5", 12, 70, -5));
        c.Effects["goto"] = () => { if (c.World.Dimension == WorldDim.Overworld) c.World.Dimension = WorldDim.Nether; }; // stepping in = 0x09
        var r = await h.Run("goto", new { to = "fortress" });
        output.WriteLine(r.Text);
        Assert.True(r.Ok, r.Text);
        Assert.StartsWith("via portal 3,65,3 -> nether 12,70,-5", r.Text);
        Assert.Equal(2, c.CallList.Count(x => x.StartsWith("goto")));
        var goal = Assert.IsType<GoalNear>(c.LastGoal);
        Assert.True(goal.IsReached(100, 70, -40));
        // Same-dimension names still resolve the normal way.
        Assert.NotNull(h.MemoryAdapter.Resolve("fortress"));
    }
}
