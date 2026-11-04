using System.Globalization;
using System.Text;
using MineClaw.Bot.Blueprints;
using MineClaw.Bot.Building;
using MineClaw.Data;
using MineClaw.Data.Versioning;
using MineClaw.World;

namespace MineClaw.Bot.Tests;

/// <summary>
/// Pins the single-body slicer (<see cref="BuildPlanner.Plan"/>) over designs and worlds the crew golden doesn't reach: a captured
/// house with doors, beds, torches, ladders, stairs and slabs (and the same house half standing), a pole and a floating slab
/// to climb to, an arch, a bridge off the island's edge (the floor hint), a basement dug into dirt, obstructions in the cells, on
/// both versions, with options (no climbing, short scaffold, water, halves following the cursor, no hidden faces, short reach,
/// protected / kept / avoided cells, a stand filter). Every step's signature, the infeasible list, columns, gate, scaffold,
/// home and notes. A refactor must leave <see cref="BuildPlannerGoldenData.Expected"/> unchanged. Regenerate (only for an
/// intended change) by setting WP02_GOLDEN_OUT.
/// </summary>
public class BuildPlannerGoldenTests
{
    private static string Sig(PlanStep s) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{s.Index} {s.Action} {s.Technique} {s.Target} {s.Id}:{s.Meta} it {s.Item}:{s.Damage} {s.Stand} {s.Face} {s.HiddenFace} {s.Yaw} {s.Column} [{string.Join(",", s.Deps)}] {s.Gate} {s.Cost:0.###} {s.Note}");

    private static void DumpPlan(StringBuilder sb, BuildPlan bp)
    {
        sb.Append(CultureInfo.InvariantCulture, $"  steps {bp.Steps.Count} infeasible {bp.Infeasible.Count} home {bp.Home} solo {bp.SoloTicks:0.##} gate [{string.Join(" ", bp.Gate.Blocks)}] state {bp.State}\n");
        if (bp.Note != null) sb.Append("  note ").Append(bp.Note).Append('\n');
        if (bp.Foundation != null) sb.Append("  foundation ").Append(bp.Foundation).Append('\n');
        foreach (var s in bp.Steps) sb.Append("  ").Append(Sig(s)).Append('\n');
        foreach (var i in bp.Infeasible) sb.Append("  x ").Append(i.Pos).Append(' ').Append(i.Id).Append(' ').Append(i.Why).Append('\n');
        foreach (var c in bp.Columns) sb.Append(CultureInfo.InvariantCulture, $"  col {c.Index} {c.Source} {c.Face} {c.Entry} {c.Depth} {c.DescendTicks} {c.AscendTicks}\n");
        if (bp.ScaffoldUp.Count > 0) sb.Append("  scaffold up ").Append(string.Join(" ", bp.ScaffoldUp)).Append('\n');
    }

    private const string Pole = "size 1x9x1\npalette C=cobblestone\nlayer 0-8\nC";
    private const string Arch = "size 5x5x1\npalette C=cobblestone\nlayer 0-3\nC...C\nlayer 4\nCCCCC";
    private const string Slab = "size 3x1x3\npalette P=planks\nlayer 0\nPPP\nPPP\nPPP";
    private const string Bridge = "size 9x1x1\npalette C=cobblestone\nlayer 0\nCCCCCCCCC";
    private const string Pit = "size 3x3x3\nground 2\npalette C=cobblestone\nlayer 0\nCCC\nCCC\nCCC\nlayer 1\n...\n.C.\n...\nlayer 2\nC.C\n...\nC.C\n";

    private static WorldState Dirt(int r = 6)
    {
        var w = new WorldState();
        w.ApplyChunkRegion(-16, 0, -16, 32, 128, 32, new byte[32 * 128 * 32], null, null, null);
        for (int x = -r; x <= r; x++) for (int z = -r; z <= r; z++) for (int y = 56; y <= 63; y++) w.SetBlock(x, y, z, Blocks.Dirt, 0);
        return w;
    }

    private static WorldState Cluttered()
    {
        var w = MassiveBuildTests.Field(8);
        w.SetBlock(-1, 64, -2, Blocks.Dirt, 0);
        w.SetBlock(0, 65, -1, Blocks.Log, 0);
        w.SetBlock(1, 64, 0, Blocks.Glass, 0);
        w.SetBlock(0, 64, 1, Blocks.Cobblestone, 0);
        w.SetBlock(2, 64, 2, Blocks.Water, 0);
        w.SetBlock(-2, 63, 0, 0, 0);
        return w;
    }

    private sealed record Case(string Name, string Dsl, BlockPos At, Func<WorldState> World, BlockPos Start, BuildPlannerOptions O, bool Modern = false);

    private static IEnumerable<Case> Cases()
    {
        var house = HouseDsl();
        var o0 = new BuildPlannerOptions { GateBlocks = 0 };
        var start = new BlockPos(0, 64, 5);
        Func<WorldState> field = () => MassiveBuildTests.Field(8);
        foreach (var modern in new[] { false, true })
        {
            var tag = modern ? " 1.12.2" : "";
            yield return new("house" + tag, house, new BlockPos(-3, 64, -2), field, start, new BuildPlannerOptions(), modern);
            yield return new("house halves" + tag, house, new BlockPos(-3, 64, -2), field, start, new BuildPlannerOptions { HalvesFollowCursor = true, GateBlocks = 0 }, modern);
            yield return new("pole" + tag, Pole, new BlockPos(0, 64, 0), field, start, o0, modern);
            yield return new("arch" + tag, Arch, new BlockPos(-2, 64, 0), field, start, o0, modern);
        }
        yield return new("house no hidden", house, new BlockPos(-3, 64, -2), field, start, new BuildPlannerOptions { AllowHidden = false, GateBlocks = 0 });
        yield return new("house reach 3", house, new BlockPos(-3, 64, -2), field, start, new BuildPlannerOptions { Reach = 3, GateBlocks = 0 });
        yield return new("house half built", house, new BlockPos(-3, 64, -2), () =>
        {
            var w = MassiveBuildTests.Field(8);
            var (plan, _) = BlueprintNormalizer.FromText(house, new BlockPos(-3, 64, -2));
            foreach (var b in plan!.Blocks.Values.Where(b => b.Pos.Y == 64 || b.Pos.X == -3)) w.SetBlock(b.Pos.X, b.Pos.Y, b.Pos.Z, b.Id, b.Meta);
            w.SetBlock(-1, 65, -1, Blocks.Planks, 0);
            return w;
        }, start, o0);
        yield return new("house protected", house, new BlockPos(-3, 64, -2), field, start, new BuildPlannerOptions
        {
            GateBlocks = 0, IsProtected = (p, _) => p.X == -2 && p.Y == 64, Keep = new HashSet<BlockPos> { new(-1, 64, -2), new(0, 64, -2) },
            Avoid = new HashSet<BlockPos> { new(0, 64, 3), new(1, 64, 3) }, StandOk = p => p.Z != 4,
        });
        yield return new("pole no climb", Pole, new BlockPos(0, 64, 0), field, start, new BuildPlannerOptions { GateBlocks = 0, Climb = false });
        yield return new("pole short scaffold", Pole, new BlockPos(0, 64, 0), field, start, new BuildPlannerOptions { GateBlocks = 0, MaxScaffold = 3, MaxPillar = 4 });
        yield return new("pole shared climb", Pole, new BlockPos(0, 64, 0), field, start, new BuildPlannerOptions { GateBlocks = 0, SharedClimb = true });
        yield return new("pole from above", Pole, new BlockPos(0, 64, 0), field, new BlockPos(0, 73, 0), o0);
        yield return new("floating slab", Slab, new BlockPos(-1, 70, -1), field, start, o0);
        yield return new("floating slab water", Slab, new BlockPos(-1, 70, -1), field, start, new BuildPlannerOptions { GateBlocks = 0, WaterAvailable = true, Buckets = 2 });
        yield return new("floating slab no climb", Slab, new BlockPos(-1, 70, -1), field, start, new BuildPlannerOptions { GateBlocks = 0, Climb = false });
        yield return new("bridge off the edge", Bridge, new BlockPos(-2, 64, 0), () => BlueprintTests.Island(3), new BlockPos(0, 64, 2), o0);
        yield return new("bridge over the void", Bridge, new BlockPos(4, 64, 0), () => BlueprintTests.Island(3), new BlockPos(0, 64, 2), new BuildPlannerOptions { GateBlocks = 3, MaxScaffold = 2 });
        yield return new("pit", Pit, new BlockPos(-1, 61, -1), () => Dirt(), new BlockPos(0, 64, 4), o0);
        yield return new("pit water", Pit, new BlockPos(-1, 61, -1), () => Dirt(), new BlockPos(0, 64, 4), new BuildPlannerOptions { GateBlocks = 0, WaterAvailable = true });
        yield return new("hut cluttered", BlueprintTests.Hut, new BlockPos(-2, 64, -3), Cluttered, start, new BuildPlannerOptions { GateBlocks = 4 });
        yield return new("hut cluttered 1.12.2", BlueprintTests.Hut, new BlockPos(-2, 64, -3), Cluttered, start, new BuildPlannerOptions { GateBlocks = 4, HalvesFollowCursor = true }, true);
        yield return new("tower", ClimbTests.HollowTower, new BlockPos(-1, 64, -1), field, start, new BuildPlannerOptions { GateBlocks = 2 });
        yield return new("hut cluttered protected", BlueprintTests.Hut, new BlockPos(-2, 64, -3), Cluttered, start, new BuildPlannerOptions
        {
            GateBlocks = 0, IsProtected = (_, id) => id is Blocks.Log or Blocks.Glass,
        });
        foreach (var water in new[] { false, true })
        {
            yield return new("shaft floor" + (water ? " water" : ""), Slab, new BlockPos(-1, 55, -1), Shaft, new BlockPos(0, 64, 4),
                new BuildPlannerOptions { GateBlocks = 0, WaterAvailable = water, Buckets = 1 });
            yield return new("under the island" + (water ? " water" : ""), Slab, new BlockPos(1, 57, -1), () => BlueprintTests.Island(3), new BlockPos(0, 64, 2),
                new BuildPlannerOptions { GateBlocks = 0, WaterAvailable = water, MaxScaffold = 12 });
            yield return new("under the edge" + (water ? " water" : ""), "size 1x2x1\npalette C=cobblestone\nlayer 0\nC\nlayer 1\nC", new BlockPos(3, 61, 0),
                () => BlueprintTests.Island(3), new BlockPos(0, 64, 2), new BuildPlannerOptions { GateBlocks = 0, WaterAvailable = water });
        }
        yield return new("void platform", "size 7x1x7\npalette C=cobblestone\nlayer 0\n" + string.Join("\n", Enumerable.Repeat("CCCCCCC", 7)),
            new BlockPos(8, 64, -3), () => BlueprintTests.Island(3), new BlockPos(0, 64, 2), new BuildPlannerOptions { GateBlocks = 0, MaxScaffold = 1 });
    }

    /// <summary>A dirt island with a 3×3 shaft dug from the surface down to y 55.</summary>
    private static WorldState Shaft()
    {
        var w = Dirt();
        for (int y = 50; y < 56; y++) for (int x = -6; x <= 6; x++) for (int z = -6; z <= 6; z++) w.SetBlock(x, y, z, Blocks.Stone, 0);
        for (int y = 55; y <= 63; y++) for (int x = -2; x <= 2; x++) for (int z = -2; z <= 2; z++) w.SetBlock(x, y, z, 0, 0);
        return w;
    }

    /// <summary>Fox's captured house (doors, bed, torches, ladder, chest, furnace, slabs, stairs) as a design.</summary>
    private static string HouseDsl()
    {
        var w = StructureCaptureTests.Terrain();
        StructureCaptureTests.House(w);
        var cap = StructureCapture.Capture(new CaptureRequest { Name = "Fox's house", World = w.PlanningView, Seed = new BlockPos(3, 64, 2), FloorY = 63 });
        return cap.Dsl.ReplaceLineEndings("\n");
    }

    internal static string Dump()
    {
        var sb = new StringBuilder();
        foreach (var c in Cases())
        {
            sb.Append("== ").Append(c.Name).Append('\n');
            using (c.Modern ? GameData.Override(Release1122GameData.Instance) : null)
            {
                var w = c.World();
                var (plan, issues) = BlueprintNormalizer.FromText(c.Dsl.ReplaceLineEndings("\n"), c.At, worldHeight: 128);
                if (plan == null) { sb.Append("  no plan: ").Append(string.Join("; ", issues)).Append('\n'); continue; }
                try { DumpPlan(sb, BuildPlanner.Plan(plan, w.PlanningView, c.Start, null, c.O)); }
                catch (Exception e) { sb.Append("  THROW ").Append(e.GetType().Name).Append(": ").Append(e.Message).Append('\n'); }
            }
        }
        return sb.ToString();
    }

    [Fact]
    public void Plans_match_the_golden_dump()
    {
        var actual = Dump().ReplaceLineEndings("\n");
        if (Environment.GetEnvironmentVariable("WP02_GOLDEN_OUT") is { Length: > 0 } path) File.WriteAllText(path, actual);
        var expected = BuildPlannerGoldenData.Expected.ReplaceLineEndings("\n").TrimEnd('\n');
        actual = actual.TrimEnd('\n');
        if (expected == actual) return;
        var a = actual.Split('\n');
        var e = expected.Split('\n');
        int i = 0;
        while (i < Math.Min(a.Length, e.Length) && a[i] == e[i]) i++;
        Assert.Fail($"plan differs at line {i + 1}:\n expected: {(i < e.Length ? e[i] : "<end>")}\n actual:   {(i < a.Length ? a[i] : "<end>")}");
    }
}
