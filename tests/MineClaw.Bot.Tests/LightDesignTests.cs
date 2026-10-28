using MineClaw.Agent.Rag;
using MineClaw.Bot.Blueprints;
using MineClaw.Data;
using MineClaw.Skills;
using MineClaw.World;
using Xunit;
using Xunit.Abstractions;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Live 10-28 16:45 "we'll need to light up the floor of the build": design #4 (the drop tower) as built on its site, its
/// region from the blueprint service (no DSL role lines; the comment names the spawning platform), the ground under and
/// around it lit by the torch plan while the farm's own deck stays dark.
/// </summary>
public class LightDesignTests(ITestOutputHelper log)
{
    [Fact]
    public void Design_4_floor_gets_lit_and_its_spawn_deck_never_does()
    {
        // The fixture site in a wider loaded area: light updates need every column within 17 blocks (mc-dev areChunksLoaded).
        var w = new WorldState();
        w.ApplyChunkRegion(-80, 0, -32, 112, WorldState.Height, 96, new byte[112 * WorldState.Height * 96], null, null, null);
        foreach (var line in File.ReadLines(DropTowerReplayTests.Fixture("site.txt")))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var f = line.Split(' ').Select(int.Parse).ToArray();
            w.SetBlock(f[0], f[1], f[2], f[3], f[4]);
        }
        var store = new BlueprintStore(AgentDb.Open(null));
        var s = new BlueprintService(new BlueprintOptions(), store, new BlueprintPreview())
        {
            Site = () => new SiteContext { World = w.PlanningView, GeneratorKnown = true, Inventory = (_, _) => 0, Stored = (_, _) => 0 },
            Crew = () => new SingleBodyCrew(new SimBuildBody(w)),
            ResolveSite = (_, _) => (new SitePick(new BlockPos(-25, 63, 15), "here"), null),
        };
        var rec = store.Insert(new BlueprintRecord
        {
            Name = "mob drop tower", Description = "drop tower", Bot = "maid_chan", Status = DesignStatus.Built,
            Site = new BlockPos(-25, 63, 15), Origin = DropTowerReplayTests.Origin,
            Dsl = File.ReadAllText(DropTowerReplayTests.Fixture("design.dsl")),
        });
        var d = s.RegionOf(rec.Id.ToString());
        Assert.NotNull(d);
        Assert.Equal(new BlockPos(-33, 64, 7), d!.Min);
        Assert.Equal(new BlockPos(-18, 79, 22), d.Max);
        Assert.Contains("dark spawning platform", d.Roles);
        Assert.Null(s.RegionOf("no such design"));

        // As built: the floor under the footprint (expand_island did it live), then every design block.
        for (int x = -37; x <= -14; x++) for (int z = 3; z <= 26; z++)
            if (w.GetBlock(x, 63, z) == 0) w.SetBlock(x, 63, z, Blocks.Cobblestone, 0);
        var plan = s.PlanOf(rec, out _)!;
        foreach (var b in plan.Blocks.Values.OrderBy(b => b.Pos.Y)) w.SetBlock(b.Pos.X, b.Pos.Y, b.Pos.Z, b.Id, b.Meta);

        var region = new LightPlan.Region(d.Min.X - 4, d.Min.Y - 2, d.Min.Z - 4, d.Max.X + 4, d.Min.Y + 2, d.Max.Z + 4, "design #4 floor");
        var ex = new LightPlan.Exclusion();
        LightPlan.AddDecks(d, ex);
        Assert.Contains(ex.Notes, n => n.StartsWith(d.Label + " decks y") && n.Contains("dark spawning platform"));
        // The platform (floor at local y12 = y76, walled, slab roof) is a dark spawn floor even at noon.
        var deck = new BlockPos(-30, 77, 10);
        Assert.Contains(deck, ex.Cells);
        Assert.True(w.CanHostileSpawn(deck, 6000), "the deck spawns mobs by day: that's the farm");

        var dark = LightPlan.Dark(w, region, ex, 8, out _);
        // Under the tower the ground is shaded even by day.
        int byDay = LightPlan.Dark(w, region, ex, 8, out _, 6000).Count;
        log.WriteLine($"design #4 floor: {dark.Count} spawnable after dark, {byDay} by day, e.g. {LightPlan.Sample(dark)}");
        Assert.True(dark.Count > 100, $"{dark.Count}");
        Assert.True(byDay > 0, "the shade under the tower");
        var cover = LightPlan.Cover(w, region, dark, ex, 8, me: new Vec3d(-21.5, 64, 19.5));
        log.WriteLine($"{cover.Torches.Count} torches: {string.Join(" ", cover.Torches.Select(t => $"{t.Pos.X},{t.Pos.Y},{t.Pos.Z}"))}; unlit {cover.Unlit.Count}");
        Assert.Empty(cover.Unlit);
        Assert.All(cover.Torches, t =>
        {
            Assert.True(t.Pos.Y < 70, $"torch {t.Pos} up on the farm");
            Assert.False(ex.Contains(t.Pos));
            Assert.False(Safety.IsOverVoid(w, t.Pos.X, t.Pos.Y, t.Pos.Z), $"torch over the void at {t.Pos}");
        });
        // Place them: every ground cell lit, the deck still a spawn floor.
        foreach (var t in cover.Torches) w.SetBlock(t.Pos.X, t.Pos.Y, t.Pos.Z, Blocks.Torch, PlaceRules.TorchMeta(t.Face));
        Assert.Empty(LightPlan.Dark(w, region, ex, 8, out _));
        Assert.True(w.CanHostileSpawn(deck));
    }
}
