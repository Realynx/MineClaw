using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Scripting.Story;
using MineClaw.World;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>
/// <c>story.go</c> / <c>story.escape</c> (docs/web/story-mode.md "Stuck pawns"). Live 11-04: a pawn stood for good in a 1×1 hole two
/// deep ("no path … it's 2 blocks up"), others in waterfalls and caves. A fake world: dig clears the cell (dirt drops dirt), place at
/// the feet is a nerd-pole step, goto moves the body to a standable cell next to it (a far target is "no path" from inside the pit).
/// </summary>
public sealed class StoryEscapeSdkTests
{
    private sealed class Director : IStoryPort
    {
        public ConcurrentQueue<string> Stuck { get; } = new();
        public JsonObject? Next(string body) => null;
        public bool Report(string body, string job, string status, string? note, double? pct) => true;
        string IStoryPort.Stuck(string body, string why) { Stuck.Enqueue(why); return "help is on its way: a pawn brings blocks"; }
    }

    private const string Far = "9,67,0";

    /// <summary>Ground of dirt (y 64..66, walk level 67) with a 1×1 pit at 0,0 (floor 64, the body in it at 65).</summary>
    private static (Rig Rig, Director Port) Pit(Action<WorldState>? shape = null)
    {
        var rig = Rig.Js();
        var w = rig.World;
        for (int x = -12; x <= 12; x++)
            for (int z = -12; z <= 12; z++)
                for (int y = 64; y <= 66; y++)
                    w.SetBlock(x, y, z, x == 0 && z == 0 && y > 64 ? 0 : Blocks.Dirt, 0);
        shape?.Invoke(w);
        w.Player.ServerTeleport(new Vec3d(0.5, 65, 0.5), 66.62, 0, 0, true);
        var port = new Director();
        rig.Host.Story = port;
        Wire(rig);
        return (rig, port);
    }

    private static BlockPos Pos(JsonElement a, string key)
    {
        var p = a.GetProperty(key).GetString()!.Split(',').Select(int.Parse).ToArray();
        return new BlockPos(p[0], p[1], p[2]);
    }

    private static void Wire(Rig rig)
    {
        var w = rig.World;
        bool Solid(int x, int y, int z) => MineClaw.World.BlockShapes.HasCollision(w, x, y, z);
        BlockPos Feet() => new((int)Math.Floor(w.Player.Position.X), (int)Math.Floor(w.Player.Position.Y + 1e-6), (int)Math.Floor(w.Player.Position.Z));
        rig.On["goto"] = (a, _) =>
        {
            var to = Pos(a, "to");
            var f = Feet();
            bool near = Math.Abs(to.X - f.X) <= 1 && Math.Abs(to.Z - f.Z) <= 1 && to.Y - f.Y <= 1;
            bool stand = Solid(to.X, to.Y - 1, to.Z) && !Solid(to.X, to.Y, to.Z) && !Solid(to.X, to.Y + 1, to.Z);
            // From the pit's floor nothing far is reachable (from a notch, a pillar or the water it is: the real planner jumps/swims out).
            bool trapped = f.X == 0 && f.Z == 0 && f.Y <= 65 && !near && w.GetBlock(f.X, f.Y, f.Z) is not (8 or 9);
            if (!stand || trapped) return Task.FromResult(ActResult.Fail($"no path to within 1 of ({to.X}, {to.Y}, {to.Z}) (no route); it's {to.Y - f.Y} blocks up"));
            w.Player.ServerTeleport(new Vec3d(to.X + 0.5, to.Y, to.Z + 0.5), to.Y + 1.62, 0, 0, true);
            return Task.FromResult(ActResult.Success("arrived"));
        };
        rig.On["dig"] = (a, _) =>
        {
            var p = Pos(a, "at");
            int id = w.GetBlock(p.X, p.Y, p.Z);
            if (id == Blocks.Obsidian) return Task.FromResult(ActResult.Fail("too hard"));
            w.SetBlock(p.X, p.Y, p.Z, 0, 0);
            if (id is Blocks.Dirt or Blocks.Grass) rig.Give(30, Blocks.Dirt, w.Inventory.CountItem(Blocks.Dirt) + 1);
            return Task.FromResult(ActResult.Success("dug"));
        };
        rig.On["place"] = (a, _) =>
        {
            var p = Pos(a, "at");
            if (p != Feet()) return Task.FromResult(ActResult.Fail("only pillars here"));
            var item = a.GetProperty("block").GetString()!;
            int id = Items.ByName(item)!.Id;
            int slot = Enumerable.Range(0, 45).First(s => w.Inventory.PlayerSlots()[s] is { IsEmpty: false } st && st.Id == id);
            var st = w.Inventory.PlayerSlots()[slot];
            rig.Give(slot, id, st.Count - 1);
            w.SetBlock(p.X, p.Y, p.Z, id, 0);
            w.Player.ServerTeleport(new Vec3d(p.X + 0.5, p.Y + 1, p.Z + 0.5), p.Y + 2.62, 0, 0, true);
            return Task.FromResult(ActResult.Success("pillared"));
        };
    }

    private static async Task<string> Run(Rig rig, string code)
    {
        var s = await rig.Run(code);
        Assert.True(s.Ok, s.Outcome);
        return s.Outcome ?? "";
    }

    [Fact]
    public async Task Out_of_a_two_deep_hole_by_a_dug_staircase_without_any_blocks_then_on_to_the_target()
    {
        var (rig, port) = Pit();
        using var _ = rig;
        var outcome = await Run(rig, $"const r = story.go('{Far}', 1); return JSON.stringify(r)");
        Assert.Contains("\"ok\":true", outcome);
        var calls = rig.Norm();
        Assert.Contains(calls, c => c.StartsWith("dig ")); // the stair: one wall block
        Assert.DoesNotContain(calls, c => c.StartsWith("place "));
        Assert.Equal(Far, $"{(int)Math.Floor(rig.World.Player.Position.X)},{(int)Math.Floor(rig.World.Player.Position.Y)},0");
        Assert.All(calls.Where(c => c.StartsWith("dig ")), c => Assert.DoesNotContain(",64,", c)); // never down
        Assert.Empty(port.Stuck);
    }

    [Fact]
    public async Task With_walls_too_hard_to_dig_it_nerd_poles_out_with_the_blocks_it_carries()
    {
        var (rig, port) = Pit(w => { foreach (var (x, z) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }) w.SetBlock(x, 66, z, Blocks.Obsidian, 0); });
        using var _ = rig;
        rig.Give(36, Blocks.Cobblestone, 4);
        var outcome = await Run(rig, $"return JSON.stringify(story.go('{Far}', 1))");
        Assert.Contains("\"ok\":true", outcome);
        Assert.Contains("""place {"at":"0,65,0","block":"cobblestone"}""", rig.Norm());
        Assert.Empty(port.Stuck);
    }

    [Fact]
    public async Task With_no_blocks_it_digs_the_walls_for_dirt_and_poles_out_with_that()
    {
        var (rig, port) = Pit(w => { foreach (var (x, z) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }) w.SetBlock(x, 66, z, Blocks.Obsidian, 0); });
        using var _ = rig;
        var outcome = await Run(rig, $"return JSON.stringify(story.go('{Far}', 1))");
        Assert.Contains("\"ok\":true", outcome);
        var calls = rig.Norm();
        int dig = calls.FindIndex(c => c.StartsWith("dig ") && c.Contains(",65,"));
        int pole = calls.FindIndex(c => c.StartsWith("place ") && c.Contains("\"dirt\""));
        Assert.True(dig >= 0 && pole > dig, string.Join(" | ", calls));
        Assert.Empty(port.Stuck);
    }

    [Fact]
    public async Task In_an_obsidian_pit_with_nothing_to_pole_with_it_asks_the_colony_for_help()
    {
        var (rig, port) = Pit(w =>
        {
            foreach (var (x, z) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }) { w.SetBlock(x, 65, z, Blocks.Obsidian, 0); w.SetBlock(x, 66, z, Blocks.Obsidian, 0); }
        });
        using var _ = rig;
        var outcome = await Run(rig, $"return JSON.stringify(story.go('{Far}', 1))");
        Assert.Contains("\"stuck\":true", outcome);
        Assert.Contains("stuck in a pit at 0,65,0", outcome);
        Assert.Contains("help is on its way", outcome);
        Assert.Single(port.Stuck);
        Assert.DoesNotContain(rig.Norm(), c => c.StartsWith("dig ") || c.StartsWith("place "));
    }

    [Fact]
    public async Task Not_in_a_pit_a_failed_walk_is_just_a_failed_walk()
    {
        using var rig = Rig.Js();
        var port = new Director();
        rig.Host.Story = port;
        rig.On["goto"] = (_, _) => Task.FromResult(ActResult.Fail("no path to within 1 of (40, 65, 0) (no route)"));
        var outcome = await Run(rig, "return JSON.stringify(story.go('40,65,0', 1))");
        Assert.Contains("\"ok\":false", outcome);
        Assert.DoesNotContain("stuck", outcome);
        Assert.Single(rig.Norm()); // one walk, no digging, no second try
        Assert.Empty(port.Stuck);
    }

    [Fact]
    public async Task Out_of_the_water_onto_the_nearest_bank()
    {
        var (rig, port) = Pit(w =>
        {
            for (int x = -2; x <= 2; x++)
                for (int z = -2; z <= 2; z++)
                    for (int y = 65; y <= 66; y++) w.SetBlock(x, y, z, 9, y == 66 ? 1 : 0); // a pond, flowing at the top
        });
        using var _ = rig;
        var outcome = await Run(rig, "return JSON.stringify(story.escape())");
        Assert.Contains("\"ok\":true", outcome);
        var f = rig.World.Player.Position;
        Assert.Equal(67, (int)Math.Floor(f.Y)); // on the bank, out of the water
        Assert.True(Math.Abs(f.X) > 2 || Math.Abs(f.Z) > 2);
    }

    [Fact]
    public async Task Shut_in_a_cave_pocket_it_digs_a_staircase_up_to_the_surface()
    {
        var (rig, port) = Pit(w =>
        {
            for (int x = -12; x <= 12; x++)
                for (int z = -12; z <= 12; z++)
                    for (int y = 67; y <= 70; y++) w.SetBlock(x, y, z, Blocks.Dirt, 0); // four more layers: the pit is a closed pocket
        });
        using var _ = rig;
        var outcome = await Run(rig, "return JSON.stringify(story.escape())");
        Assert.Contains("\"ok\":true", outcome);
        Assert.True(rig.World.Player.Position.Y >= 70, "at the top (a step from the surface): " + rig.World.Player.Position);
        Assert.Empty(port.Stuck);
        Assert.All(rig.Norm().Where(c => c.StartsWith("dig ")), c => Assert.DoesNotContain(",64,", c));
    }

    [Fact]
    public void Pit_scan_says_where_it_is_and_never_calls_a_roofed_room_a_pit()
    {
        var (rig, _) = Pit();
        using var _r = rig;
        var s = PitScan.Scan(rig.World, rig.Host.Data, rig.World.Player.Position, null);
        Assert.True(s["pit"]!.GetValue<bool>());
        Assert.Equal(1, s["need"]!.GetValue<int>());
        Assert.NotNull(s["stair"]);
        // A plank roof over the hole: a room, nothing is dug out of it.
        rig.World.SetBlock(0, 68, 0, Blocks.Planks, 0);
        Assert.False(PitScan.Scan(rig.World, rig.Host.Data, rig.World.Player.Position, null)["pit"]!.GetValue<bool>());
    }
}
