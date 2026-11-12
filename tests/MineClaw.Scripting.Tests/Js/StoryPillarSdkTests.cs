using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Scripting.Story;
using MineClaw.World;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>
/// Pillars the pawns leave and the ones they never should have built (docs/web/story-mode.md "Temporary blocks", "Stuck pawns"). Fox
/// 11-12: "pillar up and then delete 2 blocks in each level as you come down", and "If a bot can't navigate something without deleting
/// its nerd pole, then it should teleport." Live 11-12: escapes on open ground poled 18 to 39 blocks up and no pawn could take those
/// pillars down. A fake world: dig works within 4.5 of the eyes and gives the block back (its own floor: one step down), place at the
/// feet is a pillar step, goto moves the body to a standable cell. Synthetic coordinates only.
/// </summary>
public sealed class StoryPillarSdkTests
{
    private sealed class Colony : IStoryPort
    {
        public bool Teleport { get; set; }
        public ConcurrentQueue<string> Stuck { get; } = new();
        public HashSet<string> Temps { get; } = new();
        public JsonObject? Next(string body) => null;
        public bool Report(string body, string job, string status, string? note, double? pct) => true;
        string IStoryPort.Stuck(string body, string why) { Stuck.Enqueue(why); return Teleport ? "teleported home" : "help is on its way: a pawn brings blocks"; }
        bool IStoryPort.Teleports(string body) => Teleport;
        public void Temporary(string body, string cell, bool placed, string why) { lock (Temps) { if (placed) Temps.Add(cell); else Temps.Remove(cell); } }
    }

    private static BlockPos Pos(JsonElement a, string key)
    {
        var p = a.GetProperty(key).GetString()!.Split(',').Select(int.Parse).ToArray();
        return new BlockPos(p[0], p[1], p[2]);
    }

    private static BlockPos Feet(WorldState w) =>
        new((int)Math.Floor(w.Player.Position.X), (int)Math.Floor(w.Player.Position.Y + 1e-6), (int)Math.Floor(w.Player.Position.Z));

    /// <summary>The rig's stone floor (y 64, the body at 0,65,0), with the fake dig, pillar step and walk. <paramref name="slowPickup"/>:
    /// a block dug anywhere but under the feet reaches the bag only some ticks later, as a real drop does (b1.8.1: 10 ticks of pickup
    /// delay); its own floor's block is in the bag when it lands.</summary>
    private static (Rig Rig, Colony Port) Make(Action<WorldState>? shape = null, IReadOnlyList<ScriptAction>? more = null, bool slowPickup = false)
    {
        var rig = Rig.Js(more: more);
        var w = rig.World;
        shape?.Invoke(w);
        void Give(int id)
        {
            lock (w)
            {
                var slots = w.Inventory.PlayerSlots();
                int slot = Enumerable.Range(36, 9).FirstOrDefault(s => slots[s] is { IsEmpty: false } st && st.Id == id, -1);
                if (slot < 0) slot = Enumerable.Range(36, 9).First(s => slots[s].IsEmpty || slots[s].Count == 0);
                rig.Give(slot, id, (slots[slot].IsEmpty ? 0 : slots[slot].Count) + 1);
            }
        }
        rig.On["dig"] = (a, _) =>
        {
            var p = Pos(a, "at");
            double d = w.Player.EyePosition.DistanceTo(new Vec3d(p.X + 0.5, p.Y + 0.5, p.Z + 0.5));
            if (d > 4.5) return Task.FromResult(ActResult.Fail($"can't reach the block at {p.X},{p.Y},{p.Z}: {d:0.0} blocks from my eyes (reach 4.5)"));
            int id = w.GetBlock(p.X, p.Y, p.Z);
            w.SetBlock(p.X, p.Y, p.Z, 0, 0);
            var f = Feet(w);
            int drop = id == Blocks.Grass ? Blocks.Dirt : id;
            bool floor = p.X == f.X && p.Z == f.Z && p.Y == f.Y - 1;
            if (id != 0 && slowPickup && !floor) Task.Run(async () => { await Task.Delay(100); Give(drop); });
            else if (id != 0) Give(drop);
            if (p.X == f.X && p.Z == f.Z && p.Y == f.Y - 1) // its own floor: one step down
                w.Player.ServerTeleport(new Vec3d(f.X + 0.5, f.Y - 1, f.Z + 0.5), f.Y + 0.62, 0, 0, true);
            return Task.FromResult(ActResult.Success("dug"));
        };
        rig.On["place"] = (a, _) =>
        {
            var p = Pos(a, "at");
            if (p != Feet(w)) return Task.FromResult(ActResult.Fail("only pillars here"));
            int id = Items.ByName(a.GetProperty("block").GetString()!)!.Id;
            lock (w)
            {
                var slots = w.Inventory.PlayerSlots();
                int slot = Enumerable.Range(0, 45).First(s => slots[s] is { IsEmpty: false } st && st.Id == id && st.Count > 0);
                rig.Give(slot, id, slots[slot].Count - 1);
            }
            w.SetBlock(p.X, p.Y, p.Z, id, 0);
            w.Player.ServerTeleport(new Vec3d(p.X + 0.5, p.Y + 1, p.Z + 0.5), p.Y + 2.62, 0, 0, true);
            return Task.FromResult(ActResult.Success("pillared"));
        };
        rig.On["goto"] = (a, _) =>
        {
            var to = Pos(a, "to");
            bool stand = BlockShapes.HasCollision(w, to.X, to.Y - 1, to.Z) && w.GetBlock(to.X, to.Y, to.Z) == 0 && w.GetBlock(to.X, to.Y + 1, to.Z) == 0;
            if (!stand) return Task.FromResult(ActResult.Fail($"no path to within 1 of ({to.X}, {to.Y}, {to.Z}) (no route)"));
            w.Player.ServerTeleport(new Vec3d(to.X + 0.5, to.Y, to.Z + 0.5), to.Y + 1.62, 0, 0, true);
            return Task.FromResult(ActResult.Success("arrived"));
        };
        var port = new Colony();
        rig.Host.Story = port;
        return (rig, port);
    }

    private static string Cells(int x, int z, int bottom, int top) =>
        string.Join(",", Enumerable.Range(bottom, top - bottom + 1).Reverse().Select(y => $"'{x},{y},{z}'"));

    // ================================================================ any pillar, however tall, comes down (the owner's way)

    /// <summary>A 30-tall dirt pillar on open ground (its top far out of reach, nothing to walk onto): from the ground beside it the pawn
    /// builds its own column up, then comes down taking the pillar's block and the one under its feet at each level. Both columns gone,
    /// the pawn on the ground, every block in its pocket, nothing left in the colony's registry.</summary>
    [Fact]
    public async Task A_thirty_tall_pillar_is_built_up_alongside_and_both_columns_come_down()
    {
        var (rig, port) = Make(w => { for (int y = 65; y <= 94; y++) w.SetBlock(2, y, 0, Blocks.Dirt, 0); });
        using var _ = rig;
        rig.Give(36, Blocks.Dirt, 32);
        rig.World.Player.ServerTeleport(new Vec3d(3.5, 65, 0.5), 66.62, 0, 0, true);
        var s = await rig.Run($"return JSON.stringify(story.dig('2,65,0', {{stand:'3,65,0', unpole:{{beside:'3,65,0', cells:[{Cells(2, 0, 65, 94)}]}}}}))");
        Assert.True(s.Outcome!.Contains("\"ok\":true"), s.Outcome + " | " + string.Join(" | ", rig.Norm().TakeLast(8)));
        Assert.Contains("took 30 of 30 pillar block(s) down from beside it", s.Outcome);
        var w = rig.World;
        for (int y = 65; y <= 96; y++)
        {
            Assert.Equal(0, w.GetBlock(2, y, 0));
            Assert.Equal(0, w.GetBlock(3, y, 0)); // its own column too
        }
        Assert.Equal(65, (int)Math.Floor(w.Player.Position.Y));
        Assert.Equal(62, w.Inventory.CountItem(Blocks.Dirt));
        var calls = rig.Norm();
        int up = calls.Count(c => c.StartsWith("place "));
        Assert.True(up >= 20, "built up beside it: " + up);
        Assert.Equal(up, calls.Count(c => c.StartsWith("dig ") && c.Contains("\"3,")));
        Assert.Empty(port.Temps);
        Assert.Empty(port.Stuck);
    }

    /// <summary>With no blocks of its own it climbs on the pillar's: the lowest one in reach each step, never left over.</summary>
    [Fact]
    public async Task With_no_blocks_it_climbs_on_the_pillars_own_and_still_leaves_nothing()
    {
        var (rig, port) = Make(w => { for (int y = 65; y <= 76; y++) w.SetBlock(2, y, 0, Blocks.Dirt, 0); });
        using var _ = rig;
        rig.World.Player.ServerTeleport(new Vec3d(3.5, 65, 0.5), 66.62, 0, 0, true);
        var s = await rig.Run($"return JSON.stringify(story.dig('2,65,0', {{stand:'3,65,0', unpole:{{beside:'3,65,0', cells:[{Cells(2, 0, 65, 76)}]}}}}))");
        Assert.True(s.Outcome!.Contains("\"ok\":true"), s.Outcome);
        for (int y = 65; y <= 78; y++) { Assert.Equal(0, rig.World.GetBlock(2, y, 0)); Assert.Equal(0, rig.World.GetBlock(3, y, 0)); }
        Assert.Equal(65, (int)Math.Floor(rig.World.Player.Position.Y));
        Assert.Equal(12, rig.World.Inventory.CountItem(Blocks.Dirt));
        Assert.Empty(port.Temps);
    }

    /// <summary>Review 11-12: a real drop reaches the bag some ticks after the dig. With 16 dirt (the live pillar_stack) and a 30-tall
    /// pillar, it runs out 16 up and takes the pillar's block at its feet: it waits for that pickup instead of failing "no blocks"
    /// and leaving the pillar's top part floating. Both columns still end gone.</summary>
    [Fact]
    public async Task Out_of_its_own_blocks_partway_up_it_waits_for_the_pillar_blocks_pickup_and_still_takes_both_columns()
    {
        var (rig, port) = Make(w => { for (int y = 65; y <= 94; y++) w.SetBlock(2, y, 0, Blocks.Dirt, 0); }, slowPickup: true);
        using var _ = rig;
        rig.Give(36, Blocks.Dirt, 16);
        rig.World.Player.ServerTeleport(new Vec3d(3.5, 65, 0.5), 66.62, 0, 0, true);
        var s = await rig.Run($"return JSON.stringify(story.dig('2,65,0', {{stand:'3,65,0', unpole:{{beside:'3,65,0', cells:[{Cells(2, 0, 65, 94)}]}}}}))");
        Assert.True(s.Outcome!.Contains("\"ok\":true"), s.Outcome + " | " + string.Join(" | ", rig.Norm().TakeLast(8)));
        Assert.Contains("took 30 of 30 pillar block(s) down from beside it", s.Outcome);
        await Task.Delay(400); // the last drops' pickups
        for (int y = 65; y <= 96; y++)
        {
            Assert.Equal(0, rig.World.GetBlock(2, y, 0));
            Assert.Equal(0, rig.World.GetBlock(3, y, 0));
        }
        Assert.Equal(65, (int)Math.Floor(rig.World.Player.Position.Y));
        Assert.Equal(46, rig.World.Inventory.CountItem(Blocks.Dirt));
        Assert.True(rig.Norm().Count(c => c.StartsWith("place ")) > 16, "it went on past its own 16 blocks");
        Assert.Empty(port.Temps);
        Assert.Empty(port.Stuck);
    }

    /// <summary>A block under it that won't come out strands it up its own column: the colony is asked (the teleport home), its helper
    /// block stays registered for the colony's cleanup, and the job fails with why.</summary>
    [Fact]
    public async Task Stranded_up_its_own_column_it_asks_for_the_teleport_and_nothing_is_forgotten()
    {
        var (rig, port) = Make(w => { for (int y = 65; y <= 80; y++) w.SetBlock(2, y, 0, Blocks.Dirt, 0); });
        using var _ = rig;
        port.Teleport = true;
        rig.Give(36, Blocks.Dirt, 20);
        rig.World.Player.ServerTeleport(new Vec3d(3.5, 65, 0.5), 66.62, 0, 0, true);
        var dig = rig.On["dig"];
        rig.On["dig"] = (a, ct) => a.GetProperty("at").GetString() == "3,67,0"
            ? Task.FromResult(ActResult.Fail("won't dig the dirt at 3,67,0: a player's block")) : dig(a, ct);
        var s = await rig.Run($"return JSON.stringify(story.dig('2,65,0', {{stand:'3,65,0', unpole:{{beside:'3,65,0', cells:[{Cells(2, 0, 65, 80)}]}}}}))");
        Assert.Contains("\"ok\":false", s.Outcome);
        Assert.Contains("stranded at 3,68,0", s.Outcome);
        Assert.Contains("on 3 block(s) it put up beside a temporary pillar", Assert.Single(port.Stuck));
        Assert.True(port.Temps.SetEquals(["3,65,0", "3,66,0", "3,67,0"]), string.Join(" ", port.Temps));
    }

    // ================================================================ reach pillars and drops

    /// <summary>The plan said the pillar reaches the ceiling block; the world disagreed (a step didn't hold). The pillar did not help:
    /// it comes down at once, in the same job, and the block is out of reach from here.</summary>
    [Fact]
    public async Task A_reach_pillar_that_did_not_reach_its_target_comes_down_in_the_same_job()
    {
        var (rig, port) = Make(w => w.SetBlock(2, 74, 0, Blocks.Cobblestone, 0));
        using var _ = rig;
        rig.Give(36, Blocks.Dirt, 8);
        var place = rig.On["place"];
        int steps = 0;
        rig.On["place"] = (a, ct) => ++steps > 1 ? Task.FromResult(ActResult.Fail("pillar at that cell didn't hold")) : place(a, ct);
        var s = await rig.Run("return JSON.stringify(story.dig('2,74,0'))");
        Assert.Contains("\"ok\":false", s.Outcome);
        Assert.Contains("did not reach 2,74,0; taken back down", s.Outcome);
        Assert.Equal(Blocks.Cobblestone, rig.World.GetBlock(2, 74, 0));
        for (int y = 65; y <= 73; y++) Assert.Equal(0, rig.World.GetBlock(2, y, 0));
        Assert.Single(rig.Norm(), c => c == """dig {"at":"2,74,0"}"""); // only the first try from the floor, never from the pillar
        Assert.Equal(8, rig.World.Inventory.CountItem(Blocks.Dirt));
        Assert.Empty(port.Temps);
    }

    /// <summary>Live 11-12: 144 collects "it's 3 blocks up; unreachable even by pillaring/bridging". A drop on a ledge 3 up is picked up
    /// from a pole beside the ledge (the body's box touches it one below its level), and the pole comes down again.</summary>
    [Fact]
    public async Task A_drop_three_blocks_up_on_a_ledge_is_collected_from_a_pole_that_comes_down_again()
    {
        var (rig, port) = Make(w => { for (int y = 65; y <= 67; y++) w.SetBlock(1, y, 0, Blocks.Stone, 0); },
            more: [new ScriptAction("collect_drops", ["radius"])]);
        using var _ = rig;
        rig.Give(36, Blocks.Dirt, 4);
        rig.World.Player.ServerTeleport(new Vec3d(4.5, 65, 0.5), 66.62, 0, 0, true);
        var walk = rig.On["goto"];
        rig.On["goto"] = (a, ct) => Pos(a, "to") == new BlockPos(1, 68, 0)
            ? Task.FromResult(ActResult.Fail("no path to within 1 of (1, 68, 0) (no route); it's 3 blocks up; unreachable even by pillaring/bridging"))
            : walk(a, ct);
        bool picked = false;
        rig.On["collect_drops"] = (_, _) =>
        {
            var f = Feet(rig.World);
            if (!picked && f.Y >= 67 && Math.Abs(f.X - 1) <= 1 && f.Z == 0) { picked = true; return Task.FromResult(ActResult.Success("picked up 3 item(s)")); }
            return Task.FromResult(ActResult.Fail(picked ? "no reachable drops nearby (none in sight within 2 with room for it)"
                : "no reachable drops nearby (1 seen: 1 with no way to a pickup spot, 0 not picked up)"));
        };
        var s = await rig.Run("return JSON.stringify(story.collect({ r: 4 }, '1,68,0'))");
        Assert.True(s.Outcome!.Contains("\"ok\":true"), s.Outcome + " | " + string.Join(" | ", rig.Norm()));
        Assert.True(picked);
        Assert.Contains("picked up from a pole beside 1,68,0", s.Outcome);
        Assert.Equal(2, rig.Norm().Count(c => c.StartsWith("place ")));
        for (int y = 65; y <= 67; y++) Assert.Equal(0, rig.World.GetBlock(2, y, 0)); // the pole is gone
        Assert.Equal(65, (int)Math.Floor(rig.World.Player.Position.Y));
        Assert.Equal(4, rig.World.Inventory.CountItem(Blocks.Dirt));
        Assert.Empty(port.Temps);
    }

    // ================================================================ escapes build only as high as needed

    private const string Far = "5,67,5";

    /// <summary>A 1×1 hole in dirt ground (walk level 67) whose walls are obsidian up to y 69: the rim is at 70, four blocks of pole up.</summary>
    private static (Rig Rig, Colony Port) DeepHole()
    {
        var (rig, port) = Make(w =>
        {
            for (int x = -6; x <= 6; x++)
                for (int z = -6; z <= 6; z++)
                    for (int y = 65; y <= 66; y++) w.SetBlock(x, y, z, x == 0 && z == 0 ? 0 : Blocks.Dirt, 0);
            foreach (var (x, z) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                for (int y = 65; y <= 69; y++) w.SetBlock(x, y, z, Blocks.Obsidian, 0);
        });
        var w = rig.World;
        var walk = rig.On["goto"];
        rig.On["goto"] = (a, ct) =>
        {
            var f = Feet(w);
            var to = Pos(a, "to");
            bool near = Math.Abs(to.X - f.X) <= 1 && Math.Abs(to.Z - f.Z) <= 1 && to.Y - f.Y <= 1;
            return f.X == 0 && f.Z == 0 && f.Y < 69 && !near
                ? Task.FromResult(ActResult.Fail($"no path to within 1 of ({to.X}, {to.Y}, {to.Z}) (no route)")) : walk(a, ct);
        };
        return (rig, port);
    }

    /// <summary>No teleport: the pole goes up exactly to the rim (four blocks), never higher; the pawn steps off onto the rim and digs
    /// back what it reaches from there (top first). The two blocks deep in the hole stay registered: the colony's cleanup.</summary>
    [Fact]
    public async Task Without_the_teleport_an_escape_poles_only_to_the_rim_and_digs_back_what_it_reaches_from_it()
    {
        var (rig, port) = DeepHole();
        using var _ = rig;
        rig.Give(36, Blocks.Cobblestone, 10);
        rig.World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true);
        var s = await rig.Run($"return JSON.stringify(story.go('{Far}', 1))");
        Assert.True(s.Outcome!.Contains("\"ok\":true"), s.Outcome + " | " + string.Join(" | ", rig.Norm()));
        Assert.Equal(4, rig.Norm().Count(c => c.StartsWith("place ")));
        for (int y = 67; y <= 70; y++) Assert.Equal(0, rig.World.GetBlock(0, y, 0));
        Assert.Equal(8, rig.World.Inventory.CountItem(Blocks.Cobblestone));
        Assert.True(port.Temps.SetEquals(["0,65,0", "0,66,0"]), string.Join(" ", port.Temps));
        Assert.Empty(port.Stuck);
    }

    /// <summary>Review 11-12: a 2-deep, 1-wide trench running east, its rim 2 over its floor. One block of pole and the pawn can step up
    /// onto the rim; stepping off the pole it must take that step up (west), never the step down east back onto the trench's floor
    /// (tried first before: it walked back in, dug its pole from there and was stuck again on every walk).</summary>
    [Fact]
    public async Task Stepping_off_its_pole_it_steps_up_onto_the_rim_never_back_down_into_the_trench()
    {
        var (rig, port) = Make(w =>
        {
            for (int x = -6; x <= 6; x++)
                for (int z = -6; z <= 6; z++)
                    for (int y = 65; y <= 66; y++) w.SetBlock(x, y, z, z == 0 && x >= 0 && x <= 5 ? 0 : Blocks.Dirt, 0);
        });
        using var _ = rig;
        var w = rig.World;
        var walk = rig.On["goto"];
        rig.On["goto"] = (a, ct) =>
        {
            var f = Feet(w);
            var to = Pos(a, "to");
            bool near = Math.Abs(to.X - f.X) <= 1 && Math.Abs(to.Z - f.Z) <= 1 && to.Y - f.Y <= 1;
            return f.Y < 67 && !near ? Task.FromResult(ActResult.Fail($"no path to within 1 of ({to.X}, {to.Y}, {to.Z}) (no route)")) : walk(a, ct);
        };
        rig.Give(36, Blocks.Cobblestone, 4);
        var s = await rig.Run($"return JSON.stringify(story.go('{Far}', 1))");
        Assert.True(s.Outcome!.Contains("\"ok\":true"), s.Outcome + " | " + string.Join(" | ", rig.Norm()));
        Assert.Equal(1, rig.Norm().Count(c => c.StartsWith("place ")));
        Assert.DoesNotContain(rig.Norm(), c => c.StartsWith("goto ") && (c.Contains("\"1,65,0\"") || c.Contains("\"0,65,0\"")));
        Assert.Equal(0, w.GetBlock(0, 65, 0)); // its pole, dug back from the rim
        Assert.Equal(4, w.Inventory.CountItem(Blocks.Cobblestone));
        Assert.Empty(port.Temps);
        Assert.Empty(port.Stuck);
    }

    /// <summary>With the colony's rescue teleport a pole it couldn't take back from the rim is never built: "shut in", and the colony
    /// teleports it (no "in a pit", which would send a pawn with blocks).</summary>
    [Fact]
    public async Task With_the_teleport_a_hole_too_deep_to_take_the_pole_back_asks_for_the_teleport_and_places_nothing()
    {
        var (rig, port) = DeepHole();
        using var _ = rig;
        port.Teleport = true;
        rig.Give(36, Blocks.Cobblestone, 10);
        rig.World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true);
        var s = await rig.Run($"return JSON.stringify(story.go('{Far}', 1))");
        Assert.Contains("\"stuck\":true", s.Outcome);
        Assert.DoesNotContain(rig.Norm(), c => c.StartsWith("place "));
        var why = Assert.Single(port.Stuck);
        Assert.StartsWith("stuck shut in at 0,65,0", why);
        Assert.Contains("a pole out would be 4 block(s)", why);
        Assert.DoesNotContain("in a pit", why);
    }

    /// <summary>Review 11-12: the colony sends a pawn marooned up high (no walk down) an escape with <c>pole: false</c> when it can't
    /// teleport it. Even where a pole to the rim would fit and it carries blocks, that escape never takes a pillar step: it asks the
    /// colony, every block still in its pocket.</summary>
    [Fact]
    public async Task An_escape_with_pole_false_never_poles_even_where_a_pole_to_the_rim_would_fit()
    {
        var (rig, port) = DeepHole();
        using var _ = rig;
        rig.Give(36, Blocks.Cobblestone, 10);
        rig.World.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true);
        var s = await rig.Run($"return JSON.stringify(story.work({{ id: 'e1', kind: 'escape', at: '{Far}', dist: 1, s: 0.1, pole: false }}))");
        Assert.Contains("\"stuck\":true", s.Outcome);
        Assert.DoesNotContain(rig.Norm(), c => c.StartsWith("place "));
        Assert.Contains("no pole from here", Assert.Single(port.Stuck));
        Assert.Equal(10, rig.World.Inventory.CountItem(Blocks.Cobblestone));
        Assert.Empty(port.Temps);
    }

    /// <summary>The live case: an escape job on open ground with no walkable way home. Its climb used to pole straight up with every
    /// block it carried (18 to 39 blocks). With no ground beside it to step onto, it never poles: it asks the colony.</summary>
    [Fact]
    public async Task On_open_ground_with_no_way_home_the_escape_never_poles_into_the_sky()
    {
        var (rig, port) = Make();
        using var _ = rig;
        port.Teleport = true;
        rig.Give(36, Blocks.Cobblestone, 30);
        rig.On["goto"] = (a, _) => Task.FromResult(ActResult.Fail("no path to within 2 of (40, 65, 0) (no route)"));
        var s = await rig.Run("return JSON.stringify(story.work({ id: 'e1', kind: 'escape', at: '40,65,0', dist: 2, s: 0.1 }))");
        Assert.Contains("\"stuck\":true", s.Outcome);
        Assert.DoesNotContain(rig.Norm(), c => c.StartsWith("place "));
        Assert.Contains("no way home", Assert.Single(port.Stuck));
        Assert.Equal(30, rig.World.Inventory.CountItem(Blocks.Cobblestone));
    }
}
