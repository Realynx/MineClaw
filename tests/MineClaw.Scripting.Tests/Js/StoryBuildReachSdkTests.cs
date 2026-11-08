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
            Rig = Rig.Js(data: modern ? Release1122GameData.Instance : Beta181GameData.Instance, more:
            [new ScriptAction("place_here", ["block", "at", "meta"], Atomic: true), new ScriptAction("dig_here", ["at"], Atomic: true)]);
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
