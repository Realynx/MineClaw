using System.Text.Json;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Scripting.Tests.Js;

/// <summary>
/// <c>story.dig</c>: a block out of reach is dug from a nerd-pole that comes down again (docs/web/story-mode.md "Reaching high blocks").
/// Fox 11-04: "it should nerd-pole up to reach those blocks on its own and destroy the nerd pole on the way back down." A fake world:
/// dig works within 4.5 of the eyes (and gives the block back), place at the feet is a pillar step, goto moves to a standable cell.
/// </summary>
public sealed class StoryReachSdkTests
{
    private const string Ceiling = "2,71,0";

    private static BlockPos Pos(JsonElement a, string key)
    {
        var p = a.GetProperty(key).GetString()!.Split(',').Select(int.Parse).ToArray();
        return new BlockPos(p[0], p[1], p[2]);
    }

    private static Rig Make(Action<WorldState>? shape = null)
    {
        var rig = Rig.Js();
        var w = rig.World;
        w.SetBlock(2, 71, 0, Blocks.Cobblestone, 0); // a block hanging 6 above the floor: a cave ceiling, a ledge
        shape?.Invoke(w);
        BlockPos Feet() => new((int)Math.Floor(w.Player.Position.X), (int)Math.Floor(w.Player.Position.Y + 1e-6), (int)Math.Floor(w.Player.Position.Z));
        void Give(int id)
        {
            var slots = w.Inventory.PlayerSlots();
            int slot = Enumerable.Range(36, 9).FirstOrDefault(s => slots[s] is { IsEmpty: false } st && st.Id == id, -1);
            if (slot < 0) slot = Enumerable.Range(36, 9).First(s => slots[s].IsEmpty || slots[s].Count == 0);
            rig.Give(slot, id, (slots[slot].IsEmpty ? 0 : slots[slot].Count) + 1);
        }
        rig.On["dig"] = (a, _) =>
        {
            var p = Pos(a, "at");
            var eye = w.Player.EyePosition;
            double d = eye.DistanceTo(new Vec3d(p.X + 0.5, p.Y + 0.5, p.Z + 0.5));
            if (d > 4.5) return Task.FromResult(ActResult.Fail($"can't reach cobblestone at {p.X},{p.Y},{p.Z}: {d:0.0} blocks from my eyes (reach 4.5)"));
            int id = w.GetBlock(p.X, p.Y, p.Z);
            w.SetBlock(p.X, p.Y, p.Z, 0, 0);
            if (id != 0) Give(id == Blocks.Grass ? Blocks.Dirt : id); // grass drops dirt
            var f = Feet();
            if (p.X == f.X && p.Z == f.Z && p.Y == f.Y - 1) // dug its own floor: one step down
                w.Player.ServerTeleport(new Vec3d(f.X + 0.5, f.Y - 1, f.Z + 0.5), f.Y + 0.62, 0, 0, true);
            return Task.FromResult(ActResult.Success("dug"));
        };
        rig.On["place"] = (a, _) =>
        {
            var p = Pos(a, "at");
            if (p != Feet()) return Task.FromResult(ActResult.Fail("only pillars here"));
            int id = Items.ByName(a.GetProperty("block").GetString()!)!.Id;
            var slots = w.Inventory.PlayerSlots();
            int slot = Enumerable.Range(0, 45).First(s => slots[s] is { IsEmpty: false } st && st.Id == id && st.Count > 0);
            rig.Give(slot, id, slots[slot].Count - 1);
            w.SetBlock(p.X, p.Y, p.Z, id, 0);
            w.Player.ServerTeleport(new Vec3d(p.X + 0.5, p.Y + 1, p.Z + 0.5), p.Y + 2.62, 0, 0, true);
            return Task.FromResult(ActResult.Success("pillared"));
        };
        rig.On["goto"] = (a, _) =>
        {
            var to = Pos(a, "to");
            if (!MineClaw.World.BlockShapes.HasCollision(w, to.X, to.Y - 1, to.Z) || w.GetBlock(to.X, to.Y, to.Z) != 0)
                return Task.FromResult(ActResult.Fail($"no path to within 1 of ({to.X}, {to.Y}, {to.Z}) (no route); it's {to.Y - 65} blocks up"));
            w.Player.ServerTeleport(new Vec3d(to.X + 0.5, to.Y, to.Z + 0.5), to.Y + 1.62, 0, 0, true);
            return Task.FromResult(ActResult.Success("arrived"));
        };
        rig.On["withdraw"] = (a, _) =>
        {
            int id = Items.ByName(a.GetProperty("item").GetString()!)!.Id;
            rig.Give(40, id, a.GetProperty("count").GetInt32());
            return Task.FromResult(ActResult.Success("took"));
        };
        return rig;
    }

    private static int Count(Rig rig, int id) => rig.World.Inventory.CountItem(id);

    [Fact]
    public async Task An_isolated_ledge_is_skipped_for_a_walkable_pillar_base()
    {
        using var rig = Make(w => w.SetBlock(3, 68, 0, Blocks.Stone, 0));
        rig.Give(36, Blocks.Dirt, 8);
        var s = await rig.Run($"return JSON.stringify(story.dig('{Ceiling}'))");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("\"ok\":true", s.Outcome);
        Assert.Equal(0, rig.World.GetBlock(2, 71, 0));
        Assert.DoesNotContain(rig.Norm(), c => c.StartsWith("goto ") && c.Contains("3,69,0"));
        Assert.Equal(8, Count(rig, Blocks.Dirt));
    }

    [Fact]
    public async Task A_target_below_the_start_can_be_approached_from_the_floor_then_reached_by_a_pillar()
    {
        using var rig = Make();
        rig.Give(36, Blocks.Dirt, 8);
        rig.World.Player.ServerTeleport(new Vec3d(0.5, 76, 0.5), 77.62, 0, 0, true);
        var walk = rig.On["goto"];
        rig.On["goto"] = (a, ct) => Pos(a, "to") == new BlockPos(2, 71, 0)
            ? Task.FromResult(ActResult.Fail("no path; reachable by building: need 1 blocks")) : walk(a, ct);
        var s = await rig.Run($"const r = story.go('{Ceiling}', 3); return JSON.stringify(ok(r) ? story.dig('{Ceiling}') : r)");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("\"ok\":true", s.Outcome);
        Assert.Equal(0, rig.World.GetBlock(2, 71, 0));
        Assert.Contains(rig.Norm(), c => c.StartsWith("place "));
        Assert.Equal(8, Count(rig, Blocks.Dirt));
        Assert.Equal(65, (int)Math.Floor(rig.World.Player.Position.Y));
        Assert.Single(rig.Norm(), c => c.StartsWith("goto ") && c.Contains(Ceiling)); // walk below before trying to bridge from the rim
    }

    [Fact]
    public async Task A_ceiling_block_is_dug_from_a_pillar_that_comes_down_again_with_its_blocks_collected()
    {
        using var rig = Make();
        rig.Give(36, Blocks.Dirt, 4);
        var s = await rig.Run($"return JSON.stringify(story.dig('{Ceiling}'))");
        Assert.True(s.Ok, s.Outcome);
        Assert.Contains("\"ok\":true", s.Outcome);
        var w = rig.World;
        Assert.Equal(0, w.GetBlock(2, 71, 0));          // the ceiling block is down
        for (int y = 65; y <= 70; y++) Assert.Equal(0, w.GetBlock(2, y, 0)); // no pillar left behind
        Assert.Equal(4, Count(rig, Blocks.Dirt));        // every pillar block collected again
        var calls = rig.Norm();
        int up = calls.FindIndex(c => c.StartsWith("place "));
        int target = calls.FindLastIndex(c => c == $$"""dig {"at":"{{Ceiling}}"}""");
        int down = calls.FindLastIndex(c => c.StartsWith("dig ") && c.Contains(",65,"));
        Assert.True(up >= 0 && target > up && down > target, string.Join(" | ", calls));
        Assert.Equal(65, (int)Math.Floor(w.Player.Position.Y));
    }

    /// <summary>The colony's side as the pawn sees it: its registry of temporary blocks and its answer to "stuck".</summary>
    private sealed class Colony : MineClaw.Scripting.Story.IStoryPort
    {
        public HashSet<string> Temps { get; } = new();
        public List<string> Stuck { get; } = new();
        public System.Text.Json.Nodes.JsonObject? Next(string body) => null;
        public bool Report(string body, string job, string status, string? note, double? pct) => true;
        public void Temporary(string body, string cell, bool placed, string why) { if (placed) Temps.Add(cell); else Temps.Remove(cell); }
        string MineClaw.Scripting.Story.IStoryPort.Stuck(string body, string why) { Stuck.Add(why); return "teleported home"; }
    }

    /// <summary>
    /// Fox 11-05: "If a bot can't navigate something without deleting its nerd pole, then it should teleport." A pillar of planks whose
    /// block under the pawn won't come out (the dig target was fine): every other block is still dug back, the pawn stranded up there is
    /// teleported home (the colony's rescue), and the block it couldn't take back stays in the colony's registry (a cleanup job): nothing
    /// is left standing on purpose, nothing forgotten.
    /// </summary>
    [Fact]
    public async Task A_pawn_stranded_on_its_pillar_is_teleported_home_and_what_it_couldnt_dig_back_is_the_colonys_cleanup()
    {
        // A ledge 10 above the floor: a pillar of five.
        using var rig = Make(w => { w.SetBlock(2, 71, 0, 0, 0); w.SetBlock(2, 75, 0, Blocks.Cobblestone, 0); });
        var colony = new Colony();
        rig.Host.Story = colony;
        rig.Give(36, Blocks.Planks, 8);
        var dig = rig.On["dig"];
        rig.On["dig"] = (a, ct) => a.GetProperty("at").GetString() == "2,66,0"
            ? Task.FromResult(ActResult.Fail("won't dig planks at 2,66,0: a player's block"))
            : dig(a, ct);
        var s = await rig.Run("return JSON.stringify(story.dig('2,75,0'))");
        Assert.Contains("\"ok\":true", s.Outcome); // the target came down
        Assert.Equal(0, rig.World.GetBlock(2, 75, 0));
        for (int y = 67; y <= 70; y++) Assert.Equal(0, rig.World.GetBlock(2, y, 0)); // everything above the stuck block dug back
        Assert.True(colony.Temps.SetEquals(["2,66,0"]), string.Join(" ", colony.Temps) + " | " + string.Join(" | ", rig.Norm()));
        var why = Assert.Single(colony.Stuck);
        Assert.Contains("stranded on its pillar", why);
    }

    /// <summary>A pillar never stands on a chest (a chest under a block won't open: live 11-06, planks on two storage chests' lids).</summary>
    [Fact]
    public async Task A_pillar_never_goes_up_on_a_chest()
    {
        using var rig = Make(w => w.SetBlock(2, 64, 0, Blocks.Chest, 0));
        rig.Give(36, Blocks.Dirt, 8);
        var s = await rig.Run($"return JSON.stringify(story.dig('{Ceiling}'))");
        Assert.Contains("\"ok\":true", s.Outcome);
        Assert.DoesNotContain(rig.Norm(), c => c.StartsWith("place ") && c.Contains("\"2,65,0\""));
        Assert.Equal(Blocks.Chest, rig.World.GetBlock(2, 64, 0));
        Assert.Equal(0, rig.World.GetBlock(2, 65, 0));
    }

    /// <summary>A pillar comes down even when the work at its top is cut short (the dig there throws): try/finally, not luck.</summary>
    [Fact]
    public async Task The_pillar_comes_down_and_is_unregistered_even_when_the_work_at_the_top_fails()
    {
        using var rig = Make();
        var colony = new Colony();
        rig.Host.Story = colony;
        rig.Give(36, Blocks.Planks, 8);
        var dig = rig.On["dig"];
        rig.On["dig"] = (a, ct) => a.GetProperty("at").GetString() == Ceiling && Math.Floor(rig.World.Player.Position.Y) >= 66
            ? Task.FromResult(ActResult.Fail("too hard: needs a better pickaxe"))
            : dig(a, ct);
        var s = await rig.Run($"return JSON.stringify(story.dig('{Ceiling}'))");
        Assert.True(s.Outcome.Contains("too hard"), s.Outcome + " | " + string.Join(" | ", rig.Norm()));
        for (int y = 65; y <= 70; y++) Assert.Equal(0, rig.World.GetBlock(2, y, 0));
        Assert.Empty(colony.Temps);
        Assert.Empty(colony.Stuck);
    }

    [Fact]
    public async Task With_no_blocks_on_it_it_takes_the_jobs_pillar_stack_from_storage_first()
    {
        using var rig = Make();
        var s = await rig.Run($$"""return JSON.stringify(story.dig('{{Ceiling}}', { pillar: { from: '0,64,9', item: 'cobblestone', n: 16 } }))""");
        Assert.Contains("\"ok\":true", s.Outcome);
        var calls = rig.Norm();
        int take = calls.FindIndex(c => c.StartsWith("withdraw "));
        int place = calls.FindIndex(c => c.StartsWith("place ") && c.Contains("cobblestone"));
        Assert.True(take >= 0 && place > take, string.Join(" | ", calls));
        Assert.Equal(0, rig.World.GetBlock(2, 71, 0));
    }

    [Fact]
    public async Task With_no_blocks_and_no_stack_it_digs_dirt_around_the_column_to_pole_with()
    {
        using var rig = Make(w => { for (int x = -6; x <= 6; x++) for (int z = -6; z <= 6; z++) w.SetBlock(x, 64, z, Blocks.Grass, 0); });
        var s = await rig.Run($"return JSON.stringify(story.dig('{Ceiling}'))");
        Assert.Contains("\"ok\":true", s.Outcome);
        Assert.Equal(0, rig.World.GetBlock(2, 71, 0));
        Assert.Contains(rig.Norm(), c => c.StartsWith("dig ") && c.Contains(",64,")); // dug the ground around for a block
    }

    [Fact]
    public async Task Over_water_it_won_t_pillar_and_says_why()
    {
        using var rig = Make(w =>
        {
            for (int x = 0; x <= 5; x++)
                for (int z = -3; z <= 3; z++) { w.SetBlock(x, 64, z, 9, 0); w.SetBlock(x, 65, z, 9, 0); }
        });
        rig.World.Player.ServerTeleport(new Vec3d(-4.5, 65, 0.5), 66.62, 0, 0, true);
        rig.Give(36, Blocks.Dirt, 8);
        var s = await rig.Run($"return JSON.stringify(story.dig('{Ceiling}'))");
        Assert.Contains("\"ok\":false", s.Outcome);
        Assert.Contains("out of reach even from a pillar", s.Outcome);
        Assert.Contains("liquid", s.Outcome);
        Assert.DoesNotContain(rig.Norm(), c => c.StartsWith("place "));
    }
}
