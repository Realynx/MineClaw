using System.Text.Json;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>
/// A nether portal frame cast in place, no diamond pickaxe needed (obsidian can only be mined with one): every frame cell gets a
/// lava SOURCE from a bucket, and a water source poured in the cell above it turns that source to obsidian at once (1.12.2
/// BlockLiquid.checkForMixing: lava next to water on any side but below; a source becomes obsidian, flowing lava cobblestone).
/// The water is scooped back and the next cell is cast. Lava poured into an empty cell flows only after 30 ticks, so the water
/// goes in right behind it; a one-block-thick "back plate" behind the frame gives every cell a face to pour against and stops
/// lava running backwards. The plate is dug out afterwards. Needs a water bucket, one more bucket (or a lava bucket) and lava
/// within reach of the site; ~10 lava buckets' worth of trips. Overworld only: water evaporates in the nether.
/// </summary>
[Skill]
public sealed class CastPortalSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "cast_portal", Category = SkillCategory.Nether, Summary = "cast an obsidian portal frame with lava+water buckets",
        Args = "pos?,axis?,light?", Schema = SkillJson.Schema("pos:pos?,axis:str?,light:bool?"), Safety = SafetyClass.Destructive,
        Estimate = TimeSpan.FromSeconds(180),
        Pre = new() { Items = [[Items.WaterBucket]], Needs = "water bucket + 2nd bucket + lava nearby + 24 blocks", Dimension = Dim.Overworld, SurvivalGate = true },
        Keywords = "cast mold lava bucket water obsidian frame without diamond pickaxe nether gateway",
    };

    /// <summary>Plate cells behind the frame: u 0..3, v 0..5 (the row above the top frame row is where its water goes).</summary>
    private const int PlateWidth = 4, PlateHeight = 6;

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        bool light = args.Flag("light");
        var site = args.Pos() is { } given
            ? Check(ctx, given, !(args.Str("axis")?.Contains('z', StringComparison.OrdinalIgnoreCase) ?? false))
            : Find(ctx, args.Str("axis"));
        if (site.Site is not { } s) return Fail(site.Why!);
        if (SkillWorld.BuildingBlock(ctx) is not { } block) return Fail("needs cobblestone or dirt for the back plate");
        int plateNeeded = s.Plate.Count(c => SkillWorld.Replaceable(ctx.World, c));
        if (ctx.Count(block) < plateNeeded) return Fail($"needs {plateNeeded} {SkillWorld.Name(block)} for the back plate (have {ctx.Count(block)})");

        // Lava first: every empty bucket that can be filled is, so the frame needs few trips back to the lake.
        var fuel = await FillLavaAsync(ctx, ct);
        if (!ctx.Has(Items.LavaBucket)) return Fail(fuel ?? "no lava bucket and no lava source in reach");

        var placed = await BuildOps.PlaceAllAsync(ctx, s.Plate, block, ct);
        if (s.Plate.Any(c => SkillWorld.Replaceable(ctx.World, c)))
            return Fail($"back plate: placed {placed.Placed}/{s.Plate.Count}" + (placed.Error is null ? "" : $" ({placed.Error})"));

        int cast = 0, lavaBuckets = 0;
        foreach (var cell in s.Cells)
        {
            ct.ThrowIfCancellationRequested();
            if (ctx.Block(cell) == Blocks.Obsidian) { cast++; continue; }
            if (!ctx.Has(Items.LavaBucket))
            {
                var more = await FillLavaAsync(ctx, ct);
                if (!ctx.Has(Items.LavaBucket)) return Fail($"cast {cast}/10, then: {more ?? "out of lava"}");
            }
            var g = await ctx.Game.GotoAsync(new GoalBlock(s.Stand(cell)), null, ct);
            if (!g.Ok) return Fail($"cast {cast}/10, then: can't get in front of the frame ({g.Message})");
            var r = await CastCellAsync(ctx, cell, ct);
            if (!r.Ok) return Fail($"cast {cast}/10 at {P(cell)}: {r.Text}");
            lavaBuckets++;
            cast++;
        }

        // Dig the plate out so the portal can be walked through from both sides.
        int dug = 0;
        foreach (var c in s.Plate.OrderByDescending(c => c.Y))
        {
            if (ctx.Block(c) != block || ctx.Protected(c)) continue;
            if ((await ctx.ReachAsync(c, ct)).Ok && (await ctx.Game.DigAsync(c, ct)).Ok) dug++;
        }
        bool frame = PortalFrames.IsFrame(ctx.World, s.Inside, s.AlongX);
        if (!frame) return Fail($"frame at {P(s.Origin)} is not complete after casting");
        string msg = $"portal frame cast at {P(s.Origin)} ({(s.AlongX ? "x" : "z")} axis, {lavaBuckets} lava buckets, plate dug {dug}/{s.Plate.Count})";
        if (light && ctx.Has(Items.FlintAndSteel))
        {
            var l = await ctx.RunAsync("light_portal", new { pos = P(s.Inside) }, ct);
            return l.Ok ? Ok($"{msg}; {l.Text}") : Ok($"{msg}; light_portal: {l.Text}");
        }
        return Ok(msg + "; light_portal next");
    }

    /// <summary>One frame cell: lava in, water above it at once, wait for obsidian, water back. Lava left behind is scooped up again.</summary>
    private static async Task<SkillResult> CastCellAsync(SkillContext ctx, BlockPos cell, CancellationToken ct)
    {
        var up = cell.Up();
        if (!SkillWorld.Replaceable(ctx.World, up)) return SkillResult.Fail($"no room for the water above ({ctx.BlockName(up)} at {SkillJson.Fmt(up)})");
        if (!ctx.Has(Items.WaterBucket)) return SkillResult.Fail("no water bucket");
        var lava = await BucketOps.EmptyAsync(ctx, cell, true, ct);
        if (!lava.Ok) return lava;
        var water = await BucketOps.EmptyAsync(ctx, up, false, ct);
        if (water.Ok && await ctx.WaitBlockAsync(cell, (id, _) => id == Blocks.Obsidian, ct, TimeSpan.FromSeconds(2)))
        {
            if (FluidRules.IsSource(ctx.World, up, false) && ctx.Has(Items.Bucket)) await BucketOps.FillAsync(ctx, up, ct);
            return SkillResult.Success("cast");
        }
        // The water didn't go in or the lava didn't harden: get the lava back before it runs.
        if (FluidRules.IsSource(ctx.World, cell, true) && ctx.Has(Items.Bucket)) await BucketOps.FillAsync(ctx, cell, ct);
        if (FluidRules.IsSource(ctx.World, up, false) && ctx.Has(Items.Bucket)) await BucketOps.FillAsync(ctx, up, ct);
        return SkillResult.Fail(water.Ok ? "the lava did not turn to obsidian" : $"water: {water.Text}; lava taken back");
    }

    /// <summary>Fills every empty bucket from lava within reach. Returns why it stopped when nothing could be filled.</summary>
    private static async Task<string?> FillLavaAsync(SkillContext ctx, CancellationToken ct)
    {
        string? why = null;
        // The water bucket's scoop-back needs one empty bucket, which pouring it frees: all empties may take lava.
        while (ctx.Has(Items.Bucket))
        {
            var (src, w) = BucketOps.PickSource(ctx, null, lava: true);
            if (src is not { } s) { why = w; break; }
            var r = await BucketOps.FillAsync(ctx, s, ct);
            if (!r.Ok) { why = r.Text; break; }
        }
        return why;
    }

    // ---------------------------------------------------------------- site

    /// <param name="Cells">The 10 obsidian cells in casting order: bottom row, left column up, right column up, top row.</param>
    /// <param name="Plate">The back plate: u 0..3, v 0..5 one cell behind the frame plane.</param>
    private sealed record CastSite(BlockPos Origin, bool AlongX, List<BlockPos> Cells, List<BlockPos> Plate, BlockPos Inside, BlockPos Back)
    {
        /// <summary>The cell in front of the frame (opposite the plate) at the bottom-row height, level with the cell's column.</summary>
        public BlockPos Stand(BlockPos cell)
        {
            int u = Math.Clamp(AlongX ? cell.X - Origin.X : cell.Z - Origin.Z, 1, 2);
            var onFrame = AlongX ? Origin.Offset(u, 0, 0) : Origin.Offset(0, 0, u);
            return onFrame - Back;
        }
    }

    private static (CastSite? Site, string? Why) Check(SkillContext ctx, BlockPos o, bool alongX)
    {
        var (_, _, inside) = PortalFrames.Cells(o, alongX);
        BlockPos Plane(int u, int v) => alongX ? o.Offset(u, v, 0) : o.Offset(0, v, u);
        var back = alongX ? new BlockPos(0, 0, 1) : new BlockPos(1, 0, 0);
        var cells = new List<BlockPos>();
        foreach (var (u, v) in new[] { (1, 0), (2, 0), (0, 1), (0, 2), (0, 3), (3, 1), (3, 2), (3, 3), (1, 4), (2, 4) }) cells.Add(Plane(u, v));
        var plate = new List<BlockPos>();
        for (int v = 0; v < PlateHeight; v++) for (int u = 0; u < PlateWidth; u++) plate.Add(Plane(u, v) + back);
        var site = new CastSite(o, alongX, cells, plate, inside[0], back);

        var plane = new List<BlockPos>();       // the whole 4x6 plane: frame, inside, the row above
        for (int v = 0; v < PlateHeight; v++) for (int u = 0; u < PlateWidth; u++) plane.Add(Plane(u, v));
        foreach (var c in plane)
            if (!SkillWorld.Replaceable(ctx.World, c) && ctx.Block(c) != Blocks.Obsidian) return (null, $"{P(c)} is in the way ({ctx.BlockName(c)})");
        foreach (var c in plane.Concat(plate))
        {
            if (BlockShapes.IsLiquid(ctx.Block(c))) return (null, $"{ctx.BlockName(c)} at {P(c)}: not a dry spot");
            if (ctx.Protected(c)) return (null, ctx.ProtectedText(c, P(c)));
            if (!ctx.World.IsLoaded(c.X, c.Z)) return (null, $"{P(c)} is not loaded");
        }
        foreach (var c in plate.Where(c => c.Y == o.Y).Concat(cells.Where(c => c.Y == o.Y)))
            if (!SkillWorld.Solid(ctx.World, c.Down())) return (null, $"no solid floor under {P(c)}");
        var stand = site.Stand(cells[0]);
        if (!SkillWorld.Standable(ctx.World, stand)) return (null, $"nowhere to stand in front of the frame at {P(stand)}");
        if (FluidRules.FlammableNear(ctx.World, o.Offset(1, 2, 0), 5) is { } burn) return (null, $"{SkillWorld.Name(ctx.Block(burn))} at {P(burn)} would catch fire");
        return (site, null);
    }

    private static (CastSite? Site, string? Why) Find(SkillContext ctx, string? axis)
    {
        bool? only = axis is null ? null : !axis.Contains('z', StringComparison.OrdinalIgnoreCase);
        var me = ctx.Me;
        string why = "no flat, clear 4x6 spot with a solid floor within 8 blocks; give pos (bottom-left corner)";
        foreach (var (dx, dz) in Enumerable.Range(-8, 17).SelectMany(x => Enumerable.Range(-8, 17).Select(z => (x, z)))
                     .Where(t => Math.Max(Math.Abs(t.x), Math.Abs(t.z)) >= 2).OrderBy(t => t.x * t.x + t.z * t.z))
        foreach (bool alongX in only is { } o ? [o] : new[] { true, false })
        {
            var r = Check(ctx, new BlockPos(me.X + dx, me.Y, me.Z + dz), alongX);
            if (r.Site is not null) return r;
        }
        return (null, why);
    }
}
