using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.Scripting.Story;
using MineClaw.World;

namespace MineClaw.Scripting.Tests.Js;

public sealed class StoryBuildReachSdkTests
{
    private const string Job = "{id:'build',reach:true,block:'cobblestone',want:'cobblestone',cells:['2,71,0','2,71,1','2,71,2']}";

    private sealed class Colony : IStoryPort
    {
        public bool Active = true, AcceptReports = true;
        public readonly HashSet<string> Temps = [];
        public readonly List<string> Reports = [];
        public Action? AddedTemp;
        public JsonObject? Next(string body) => null;
        public bool Holds(string body, string job) => Active;
        public bool Report(string body, string job, string status, string? note, double? pct)
        { Reports.Add(note ?? ""); return AcceptReports; }
        public void Temporary(string body, string cell, bool placed, string why)
        { if (placed) { Temps.Add(cell); AddedTemp?.Invoke(); } else Temps.Remove(cell); }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Rig Rig;
        public readonly Colony Colony = new();
        public readonly List<string> Events = [];
        public Action? Built;
        private WorldState W => Rig.World;
        public Fixture(bool modern = false, int dirt = 8, int cobble = 3)
        {
            Rig = Rig.Js(data: modern ? Release1122GameData.Instance : Beta181GameData.Instance, actions:
            [
                new ScriptAction("goto", ["to", "dist", "scaffold", "keep"]),
                new ScriptAction("place", ["block", "at"], Atomic: true),
                new ScriptAction("place_here", ["block", "at", "meta"], Atomic: true),
                new ScriptAction("dig_here", ["at"], Atomic: true),
                new ScriptAction("dig", ["at"], Atomic: true),
                new ScriptAction("withdraw", ["item", "count", "at"], Atomic: true),
            ]);
            Rig.Host.Story = Colony;
            for (int z = 0; z < 3; z++) W.SetBlock(3, 71, z, Blocks.Stone, 0);
            Rig.Give(36, Blocks.Dirt, dirt);
            Rig.Give(37, Blocks.Cobblestone, cobble);
            Rig.On["goto"] = (a, _) =>
            {
                var p = Pos(a, "to");
                if (!BlockShapes.HasCollision(W, p.X, p.Y - 1, p.Z) || W.GetBlock(p) != 0)
                    return Result(false, "no path to high work cell");
                Assert.True(W.Player.Position.Y < 66, "walked off a temporary pillar");
                Move(p.X + 0.5, p.Y, p.Z + 0.5);
                Events.Add("approach");
                return Result(true, "arrived");
            };
            Rig.On["place"] = (a, _) =>
            {
                var p = Pos(a, "at");
                Assert.Equal(Feet(), p); // permanent work must use the stationary action
                int item = Items.ByName(a.GetProperty("block").GetString()!)!.Id;
                if (!Consume(item)) return Result(false, "no blocks");
                W.SetBlock(p.X, p.Y, p.Z, item, 0);
                Move(p.X + 0.5, p.Y + 1, p.Z + 0.5);
                Events.Add("pole " + p);
                return Result(true, "pillared");
            };
            Rig.On["place_here"] = (a, _) =>
            {
                var p = Pos(a, "at");
                int item = Items.ByName(a.GetProperty("block").GetString()!)!.Id;
                int? meta = a.TryGetProperty("meta", out var m) ? m.GetInt32() : null;
                if (!ReachScan.PlacementWithin(W, Rig.Host.Data, W.Player.Position, p, item, meta))
                    return Result(false, "no visible support face");
                if (!Consume(item)) return Result(false, "no blocks");
                W.SetBlock(p.X, p.Y, p.Z, item, meta ?? 0);
                Events.Add("built " + p);
                Built?.Invoke();
                return Result(true, "placed");
            };
            Rig.On["dig_here"] = (a, _) =>
            {
                var p = Pos(a, "at");
                Assert.Equal(new BlockPos(Feet().X, Feet().Y - 1, Feet().Z), p);
                int item = W.GetBlock(p), slot = item == Blocks.Dirt ? 36 : 37;
                Rig.Give(slot, item, W.Inventory.CountItem(item) + 1);
                W.SetBlock(p.X, p.Y, p.Z, 0, 0);
                Move(p.X + 0.5, p.Y, p.Z + 0.5);
                Events.Add("down " + p);
                return Result(true, "dug");
            };
        }
        public BlockPos Feet() => new((int)Math.Floor(W.Player.Position.X), (int)Math.Floor(W.Player.Position.Y), (int)Math.Floor(W.Player.Position.Z));
        private void Move(double x, double y, double z) => W.Player.ServerTeleport(new Vec3d(x, y, z), y + 1.62, 0, 0, true);
        private bool Consume(int item)
        {
            int count = W.Inventory.CountItem(item);
            if (count == 0) return false;
            Rig.Give(item == Blocks.Dirt ? 36 : 37, item, count - 1);
            return true;
        }
        public void Recovered()
        {
            Assert.Empty(Colony.Temps);
            Assert.Equal(65, Feet().Y);
        }
        public void Dispose() => Rig.Dispose();
    }

    private static Task<ActResult> Result(bool ok, string msg) => Task.FromResult(ok ? ActResult.Success(msg) : ActResult.Fail(msg));
    private static BlockPos Pos(JsonElement a, string field)
    {
        var xyz = a.GetProperty(field).GetString()!.Split(',').Select(int.Parse).ToArray();
        return new BlockPos(xyz[0], xyz[1], xyz[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task High_construction_cells_share_one_access_and_finish_before_its_pillar_is_removed(bool modern)
    {
        using var f = new Fixture(modern);
        var r = await f.Rig.Run("return JSON.stringify(story.place(" + Job + "))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("3/3 placed", r.Outcome);
        Assert.Single(f.Events, e => e == "approach");
        Assert.Equal(3, f.Events.Count(e => e.StartsWith("built ")));
        Assert.True(f.Events.FindLastIndex(e => e.StartsWith("built ")) < f.Events.FindIndex(e => e.StartsWith("down ")), string.Join(" | ", f.Events));
        Assert.Equal(8, f.Rig.World.Inventory.CountItem(Blocks.Dirt));
        Assert.Equal(0, f.Rig.World.Inventory.CountItem(Blocks.Cobblestone));
        f.Recovered();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Visible_later_cells_are_built_before_dismantling_access_for_a_distant_cell(bool modern)
    {
        using var f = new Fixture(modern);
        f.Rig.World.SetBlock(-3, 71, -4, Blocks.Stone, 0);
        const string job = "{id:'build',reach:true,block:'cobblestone',want:'cobblestone',cells:['2,71,0','-4,71,-4','2,71,2']}";
        var r = await f.Rig.Run("return JSON.stringify(story.place(" + job + "))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("3/3 placed", r.Outcome);
        Assert.Equal(2, f.Events.Count(e => e == "approach"));
        int nearby = f.Events.FindIndex(e => e == "built " + new BlockPos(2, 71, 2));
        int down = f.Events.FindIndex(e => e.StartsWith("down "));
        Assert.True(nearby >= 0 && down > nearby, string.Join(" | ", f.Events));
        Assert.Equal(8, f.Rig.World.Inventory.CountItem(Blocks.Dirt));
        f.Recovered();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reordering_visible_cells_keeps_each_requested_orientation(bool modern)
    {
        using var f = new Fixture(modern);
        f.Rig.Give(37, Blocks.Torch, 3);
        f.Rig.World.SetBlock(-3, 71, -4, Blocks.Stone, 0);
        f.Rig.World.SetBlock(2, 71, 3, Blocks.Stone, 0);
        const string job = "{id:'build',reach:true,block:'torch',want:'torch',cells:['2,71,0','-4,71,-4','2,71,2'],metas:[2,2,4]}";
        var r = await f.Rig.Run("return JSON.stringify(story.place(" + job + "))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("3/3 placed", r.Outcome);
        Assert.Equal(2, f.Events.Count(e => e == "approach"));
        Assert.Equal(2, f.Rig.World.GetMeta(new BlockPos(2, 71, 0)));
        Assert.Equal(2, f.Rig.World.GetMeta(new BlockPos(-4, 71, -4)));
        Assert.Equal(4, f.Rig.World.GetMeta(new BlockPos(2, 71, 2)));
        int nearby = f.Events.FindIndex(e => e == "built " + new BlockPos(2, 71, 2));
        Assert.True(nearby >= 0 && nearby < f.Events.FindIndex(e => e.StartsWith("down ")), string.Join(" | ", f.Events));
        f.Recovered();
    }

    [Fact]
    public async Task An_unsupported_later_cell_is_not_pulled_forward_onto_the_current_access()
    {
        using var f = new Fixture();
        f.Rig.World.SetBlock(-3, 71, -4, Blocks.Stone, 0);
        f.Rig.World.SetBlock(3, 71, 2, 0, 0);
        const string job = "{id:'build',reach:true,block:'cobblestone',want:'cobblestone',cells:['2,71,0','-4,71,-4','2,71,2']}";
        var r = await f.Rig.Run("return JSON.stringify(story.place(" + job + "))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("2/3 placed", r.Outcome);
        Assert.Equal(0, f.Rig.World.GetBlock(2, 71, 2));
        Assert.Equal(["cell 2,71,0 done", "cell -4,71,-4 done"], f.Colony.Reports.Where(n => n.EndsWith(" done")));
        f.Recovered();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_builder_escapes_to_its_actual_stand_once_then_places_without_repeated_pillaring(bool modern)
    {
        using var f = new Fixture(modern, cobble: 2);
        var w = f.Rig.World;
        w.SetBlock(1, 67, 0, Blocks.Stone, 0);
        w.SetBlock(3, 69, -1, Blocks.Stone, 0);
        w.SetBlock(3, 69, 1, Blocks.Stone, 0);
        int walks = 0;
        f.Rig.On["goto"] = (a, ct) =>
        {
            walks++;
            Assert.Equal("1,68,0", a.GetProperty("to").GetString());
            if (!a.TryGetProperty("scaffold", out var scaffold)) return Result(false, "no path to upper floor; reachable by building: need 2 blocks");
            Assert.True(scaffold.GetBoolean());
            Assert.False(a.TryGetProperty("build", out _));
            Assert.Equal(2, a.GetProperty("keep").GetProperty("4").GetInt32());
            // The path executor supplies the two temporary steps without spending either reserved construction block.
            f.Rig.Give(36, Blocks.Dirt, 6);
            w.SetBlock(0, 65, 0, Blocks.Dirt, 0);
            w.SetBlock(0, 66, 0, Blocks.Dirt, 0);
            w.Player.ServerTeleport(new Vec3d(1.5, 68, 0.5), 69.62, 0, 0, true);
            return Result(true, "arrived using two temporary blocks");
        };

        var r = await f.Rig.Run("return JSON.stringify(story.place({id:'build',reach:true,stand:'1,68,0',block:'cobblestone',want:'cobblestone',cells:['2,69,-1','2,69,1']}))");
        Assert.True(r.Ok, r.Outcome);
        Assert.True(r.Outcome?.Contains("2/2 placed") == true, r.Outcome + " | " + string.Join(" | ", f.Events) + " | " + string.Join(" | ", f.Colony.Reports));
        Assert.Equal(2, walks);
        Assert.Equal(2, f.Events.Count(e => e.StartsWith("built ")));
        Assert.DoesNotContain(f.Events, e => e.StartsWith("pole ") || e.StartsWith("down "));
        Assert.DoesNotContain(f.Rig.Norm(), c => c.StartsWith("dig ") || c.StartsWith("dig_here "));
        Assert.Equal(0, w.Inventory.CountItem(Blocks.Cobblestone));
        Assert.Equal(6, w.Inventory.CountItem(Blocks.Dirt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_or_incomplete_stand_recovery_never_starts_a_construction_pillar(bool reportsSuccess)
    {
        using var f = new Fixture();
        int walks = 0;
        f.Rig.On["goto"] = (a, ct) =>
        {
            walks++;
            return a.TryGetProperty("scaffold", out _) ? Result(reportsSuccess, "still below the stand") : Result(false, "no route");
        };
        var r = await f.Rig.Run("return JSON.stringify(story.place(" + Job.Replace("reach:true", "reach:true,stand:'0,69,0'") + "))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("\"ok\":false", r.Outcome);
        Assert.Equal(2, walks);
        Assert.Empty(f.Events);
        Assert.Empty(f.Colony.Reports);
        Assert.Equal(3, f.Rig.World.Inventory.CountItem(Blocks.Cobblestone));
    }

    [Fact]
    public async Task A_reachable_floor_repair_does_not_walk_to_an_unreachable_preferred_stand()
    {
        using var f = new Fixture();
        f.Rig.On["goto"] = (_, _) => Result(false, "no route to preferred stand");
        var r = await f.Rig.Run("return JSON.stringify(story.place({id:'build',reach:true,stand:'0,69,0',block:'cobblestone',want:'cobblestone',cells:['1,65,0']}))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("1/1 placed", r.Outcome);
        Assert.DoesNotContain(f.Rig.Norm(), c => c.StartsWith("goto "));
        Assert.Equal(Blocks.Cobblestone, f.Rig.World.GetBlock(1, 65, 0));
        Assert.DoesNotContain(f.Events, e => e.StartsWith("pole "));
    }

    [Fact]
    public async Task An_incomplete_preferred_stand_walk_can_still_finish_work_that_is_actually_reachable_now()
    {
        using var f = new Fixture();
        f.Rig.World.SetBlock(1, 68, 0, Blocks.Stone, 0);
        f.Rig.On["goto"] = (a, ct) =>
        {
            if (a.TryGetProperty("scaffold", out _))
                f.Rig.World.Player.ServerTeleport(new Vec3d(1.5, 69, 0.5), 70.62, 0, 0, true);
            return Result(false, "no route to preferred stand");
        };
        var r = await f.Rig.Run("return JSON.stringify(story.place({id:'build',reach:true,stand:'0,72,0',block:'cobblestone',want:'cobblestone',cells:['2,71,0']}))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("1/1 placed", r.Outcome);
        Assert.Equal(2, f.Rig.Norm().Count(c => c.StartsWith("goto ")));
        Assert.Equal(Blocks.Cobblestone, f.Rig.World.GetBlock(2, 71, 0));
        Assert.DoesNotContain(f.Events, e => e.StartsWith("pole "));
    }

    [Fact]
    public async Task A_pillar_never_starts_when_the_access_base_walk_reports_success_without_arriving()
    {
        using var f = new Fixture();
        f.Rig.On["goto"] = (_, _) => Result(true, "below the work but not at its access base");
        var r = await f.Rig.Run("return JSON.stringify(story.place(" + Job + "))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("0/3 placed", r.Outcome);
        Assert.Contains(f.Colony.Reports, n => n.Contains("did not arrive at construction stand"));
        Assert.Empty(f.Events);
        f.Recovered();
    }

    [Fact]
    public async Task Construction_fetch_recovers_a_chest_approach_once_then_withdraws_and_builds()
    {
        using var f = new Fixture(cobble: 0);
        bool reached = false;
        f.Rig.On["withdraw"] = (_, _) =>
        {
            if (!reached) return Result(false, "can't reach chest at 5,65,5: no route");
            f.Rig.Give(37, Blocks.Cobblestone, 2);
            return Result(true, "withdrew two cobblestone");
        };
        f.Rig.On["goto"] = (a, ct) =>
        {
            Assert.Equal("5,65,5", a.GetProperty("to").GetString());
            Assert.False(a.TryGetProperty("dist", out _)); // GoalUseBlock, never stand on the chest.
            Assert.False(a.TryGetProperty("build", out _));
            Assert.True(a.GetProperty("scaffold").GetBoolean());
            Assert.Equal(2, a.GetProperty("keep").GetProperty("4").GetInt32());
            reached = true;
            return Result(true, "in visible chest reach using temporary steps");
        };
        var r = await f.Rig.Run("const j={id:'build',reach:true,block:'cobblestone',want:'cobblestone',cells:['1,65,0','1,65,1'],fetch:[{from:'5,65,5',item:'cobblestone',n:2}]};const got=story.fetch(j);return JSON.stringify(ok(got)?story.place(j):got)");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("2/2 placed", r.Outcome);
        Assert.Equal(2, f.Rig.Norm().Count(c => c.StartsWith("withdraw ")));
        Assert.Single(f.Rig.Norm(), c => c.StartsWith("goto "));
        Assert.DoesNotContain(f.Events, e => e.StartsWith("pole "));
        Assert.Equal(0, f.Rig.World.Inventory.CountItem(Blocks.Cobblestone));
    }

    [Theory]
    [InlineData(true, "chest has no cobblestone")]
    [InlineData(true, "busy: another pawn is using the chest")]
    [InlineData(false, "can't reach chest: no route")]
    public async Task Fetch_recovery_is_only_for_construction_route_failures(bool construction, string failure)
    {
        using var f = new Fixture(cobble: 0);
        int takes = 0;
        f.Rig.On["withdraw"] = (_, _) => ++takes == 1 ? Result(false, failure) : Result(true, "taken");
        string reach = construction ? "true" : "false";
        var r = await f.Rig.Run("return JSON.stringify(story.fetch({id:'build',reach:" + reach + ",block:'cobblestone',cells:['1,65,0'],fetch:[{from:'5,65,5',item:'cobblestone',n:1}]}))");
        Assert.True(r.Ok, r.Outcome);
        Assert.DoesNotContain(f.Rig.Norm(), c => c.StartsWith("goto "));
    }

    [Fact]
    public async Task A_construction_source_gets_only_one_route_recovery_per_job_even_when_fetch_is_called_again()
    {
        using var f = new Fixture(cobble: 0);
        f.Rig.On["withdraw"] = (_, _) => Result(false, "can't reach chest: no route");
        f.Rig.On["goto"] = (_, _) => Result(false, "no safe scaffold route");
        var r = await f.Rig.Run("const j={id:'build',reach:true,block:'cobblestone',cells:['1,65,0'],fetch:[{from:'5,65,5',item:'cobblestone',n:1}]};story.fetch(j);return JSON.stringify(story.fetch({...j}))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Equal(2, f.Rig.Norm().Count(c => c.StartsWith("withdraw ")));
        Assert.Single(f.Rig.Norm(), c => c.StartsWith("goto "));
    }

    [Fact]
    public async Task A_revoked_construction_job_does_not_withdraw_after_its_recovery_route()
    {
        using var f = new Fixture(cobble: 0);
        f.Rig.On["withdraw"] = (_, _) => Result(false, "can't reach chest: no route");
        f.Rig.On["goto"] = (_, _) => { f.Colony.Active = false; return Result(true, "arrived"); };
        var r = await f.Rig.Run("return JSON.stringify(story.fetch({id:'build',reach:true,block:'cobblestone',cells:['1,65,0'],fetch:[{from:'5,65,5',item:'cobblestone',n:1}]}))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("\"cancelled\":true", r.Outcome);
        Assert.Single(f.Rig.Norm(), c => c.StartsWith("withdraw "));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_high_wall_torch_run_keeps_its_requested_faces_on_the_shared_access(bool modern)
    {
        using var f = new Fixture(modern);
        f.Rig.Give(37, Blocks.Torch, 3);
        string job = Job.Replace("cobblestone", "torch").Replace("reach:true", "reach:true,metas:[2,2,2]");
        var r = await f.Rig.Run("return JSON.stringify(story.place(" + job + "))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("3/3 placed", r.Outcome);
        Assert.Single(f.Events, e => e == "approach");
        for (int z = 0; z < 3; z++)
        {
            Assert.Equal(Blocks.Torch, f.Rig.World.GetBlock(2, 71, z));
            Assert.Equal(2, f.Rig.World.GetMeta(new BlockPos(2, 71, z)));
        }
        Assert.All(f.Rig.Norm().Where(c => c.StartsWith("place_here ")), c => Assert.Contains("\"meta\":2", c));
        f.Recovered();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoked_ownership_or_a_rejected_progress_report_stops_the_tail_but_recovers_the_access(bool rejectReport)
    {
        using var f = new Fixture();
        f.Built = () => { if (rejectReport) f.Colony.AcceptReports = false; else f.Colony.Active = false; };
        var r = await f.Rig.Run("return JSON.stringify(story.place(" + Job + "))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("\"cancelled\":true", r.Outcome);
        Assert.Single(f.Events, e => e.StartsWith("built "));
        Assert.Equal(0, f.Rig.World.GetBlock(2, 71, 1));
        Assert.Equal(0, f.Rig.World.GetBlock(2, 71, 2));
        f.Recovered();
    }

    [Fact]
    public async Task Cancellation_during_access_building_never_starts_the_permanent_work_and_recovers_every_placed_step()
    {
        using var f = new Fixture();
        f.Colony.AddedTemp = () => f.Colony.Active = false;
        var r = await f.Rig.Run("return JSON.stringify(story.place(" + Job + "))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("\"cancelled\":true", r.Outcome);
        Assert.DoesNotContain(f.Events, e => e.StartsWith("built "));
        Assert.Single(f.Events, e => e.StartsWith("pole "));
        f.Recovered();
    }

    [Fact]
    public async Task A_failed_support_ends_the_ordered_run_and_recovers_the_shared_pillar()
    {
        using var f = new Fixture();
        var place = f.Rig.On["place_here"];
        f.Rig.On["place_here"] = (a, ct) => a.GetProperty("at").GetString() == "2,71,1"
            ? Result(false, "nothing solid on any side to place against") : place(a, ct);
        var r = await f.Rig.Run("return JSON.stringify(story.place(" + Job + "))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("1/3 placed", r.Outcome);
        Assert.DoesNotContain(f.Rig.Norm(), c => c.StartsWith("place_here ") && c.Contains("2,71,2"));
        Assert.Contains(f.Colony.Reports, n => n.Contains("waiting:"));
        f.Recovered();
    }

    [Fact]
    public async Task A_failed_pillar_recovery_stops_before_walking_to_the_next_access()
    {
        using var f = new Fixture();
        f.Rig.World.SetBlock(-3, 71, -4, Blocks.Stone, 0);
        f.Rig.On["dig_here"] = (_, _) => Result(false, "server refused temporary block removal");
        var r = await f.Rig.Run("return JSON.stringify(story.place({id:'build',reach:true,block:'cobblestone',cells:['2,71,0','-4,71,-4']}))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("1/2 placed", r.Outcome);
        Assert.Single(f.Events, e => e == "approach");
        Assert.Single(f.Events, e => e.StartsWith("built "));
        Assert.NotEmpty(f.Colony.Temps);
        Assert.Contains(f.Colony.Reports, n => n.Contains("cleanup pending"));
        Assert.Equal(0, f.Rig.World.GetBlock(-4, 71, -4));
    }

    [Fact]
    public async Task Exact_building_stock_is_reserved_instead_of_being_consumed_by_the_access_pillar()
    {
        using var f = new Fixture(dirt: 0, cobble: 3);
        var r = await f.Rig.Run("return JSON.stringify(story.place(" + Job + "))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("0/3 placed", r.Outcome);
        Assert.DoesNotContain(f.Events, e => e.StartsWith("pole "));
        Assert.Equal(3, f.Rig.World.Inventory.CountItem(Blocks.Cobblestone));
        f.Recovered();
    }

    [Fact]
    public async Task Building_material_can_supply_the_pillar_when_enough_remains_for_the_whole_run()
    {
        using var f = new Fixture(dirt: 0, cobble: 8);
        var r = await f.Rig.Run("return JSON.stringify(story.place(" + Job + "))");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains("3/3 placed", r.Outcome);
        Assert.Equal(5, f.Rig.World.Inventory.CountItem(Blocks.Cobblestone));
        f.Recovered();
    }

    [Theory]
    [InlineData("story.dig('1,65,1',{replace:true})")]
    [InlineData("story.digBatch({id:'build',at:'1,65,1',dig_cells:['1,65,1'],replace:true})")]
    [InlineData("story.place({id:'build',at:'1,65,1',block:'cobblestone',clear:true,replace:true})")]
    public async Task Only_explicit_replacement_work_forwards_the_replacement_permission(string script)
    {
        using var rig = Rig.Js(actions:
        [new ScriptAction("clear_dig", ["at", "replacement"], Atomic: true), new ScriptAction("place", ["block", "at"], Atomic: true)]);
        rig.Host.Story = new Colony();
        rig.World.SetBlock(1, 65, 1, Blocks.Cobblestone, 0);
        rig.Give(36, Blocks.Cobblestone, 4);
        var r = await rig.Run("return JSON.stringify(" + script + ")");
        Assert.True(r.Ok, r.Outcome);
        Assert.Contains(rig.Norm(), c => c == "clear_dig {\"at\":\"1,65,1\",\"replacement\":true}");
    }
}
