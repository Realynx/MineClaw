using System.Text.Json;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>
/// b1.8.1 liquid rules used to plan bucket work. Lava hardens when water touches it from the side or above
/// (BlockFluid.checkForHarden): a lava SOURCE becomes obsidian, flowing lava cobblestone; lava flowing down into water
/// turns that water to stone. Lava spreads 3 blocks in the overworld (7 in the nether), water 7. Water can't be placed
/// in the nether (it evaporates). Planning over-estimates the flow area: it refuses more than strictly needed.
/// </summary>
public static class FluidRules
{
    public const int WaterReach = 7, LavaReach = 3, NetherLavaReach = 7, MaxFall = 12;

    private static readonly int[] Flammable = [Blocks.Planks, Blocks.Log, Blocks.Leaves, Blocks.Wool, Blocks.Bookshelf, Blocks.Tnt,
        Blocks.WoodenStairs, Blocks.Fence, Blocks.TallGrass, Blocks.Vine, Blocks.Chest, Blocks.Workbench];

    /// <summary>Cells a liquid released at <paramref name="p"/> could reach (horizontal spread + falls).</summary>
    public static HashSet<BlockPos> FlowArea(IBlockAccess w, BlockPos p, int reach)
    {
        var seen = new HashSet<BlockPos> { p };
        var q = new Queue<(BlockPos P, int D)>();
        q.Enqueue((p, 0));
        while (q.Count > 0)
        {
            var (c, d) = q.Dequeue();
            // Falls first: liquid pours straight down through anything that isn't solid.
            var below = c.Down();
            if (below.Y >= 0 && Open(w, below))
            {
                var f = below;
                int fallen = 1;
                seen.Add(f);
                while (f.Y > 0 && Open(w, f.Down()) && fallen < MaxFall) { f = f.Down(); seen.Add(f); fallen++; }
                // Landed on something: it spreads again from there.
                if (f.Y > 0 && !Open(w, f.Down()) && fallen < MaxFall) q.Enqueue((f, 0));
                continue;
            }
            if (d >= reach) continue;
            foreach (var o in SkillWorld.Horizontal)
            {
                var n = c + o;
                if (Open(w, n) && seen.Add(n)) q.Enqueue((n, d + 1));
            }
        }
        return seen;
    }

    private static bool Open(IBlockAccess w, BlockPos p)
    {
        if (p.Y < 0 || p.Y >= w.WorldHeight || !w.IsLoaded(p.X, p.Z)) return false;
        int id = w.GetBlock(p.X, p.Y, p.Z);
        return id == 0 || (!BlockShapes.HasCollision(w, p.X, p.Y, p.Z) && !BlockShapes.IsLiquid(id) && id != BlockShapes.Fire) || id is 8 or 10;
    }

    /// <summary>Why releasing lava at <paramref name="p"/> would touch water (null = it can't), naming what would form.</summary>
    public static string? LavaContact(IBlockAccess w, BlockPos p, bool nether)
    {
        foreach (var c in FlowArea(w, p, nether ? NetherLavaReach : LavaReach))
        {
            foreach (var f in BlockFaceExtensions.All)
            {
                var n = c.Offset(f);
                if (!BlockShapes.IsWater(w.GetBlock(n.X, n.Y, n.Z))) continue;
                string forms = f == BlockFace.Bottom ? "stone" : c == p ? "obsidian" : "cobblestone";
                return $"lava at {SkillJson.Fmt(p)} would reach water at {SkillJson.Fmt(n)} (makes {forms})";
            }
        }
        return null;
    }

    /// <summary>Why releasing water at <paramref name="p"/> would touch lava (null = it can't).</summary>
    public static string? WaterContact(IBlockAccess w, BlockPos p)
    {
        foreach (var c in FlowArea(w, p, WaterReach))
        foreach (var f in BlockFaceExtensions.All)
        {
            var n = c.Offset(f);
            int id = w.GetBlock(n.X, n.Y, n.Z);
            if (!BlockShapes.IsLava(id)) continue;
            bool source = w.GetMeta(n.X, n.Y, n.Z) == 0;
            return $"water from {SkillJson.Fmt(p)} would reach lava at {SkillJson.Fmt(n)} (makes {(source && f != BlockFace.Top ? "obsidian" : "cobblestone")})";
        }
        return null;
    }

    public static BlockPos? FlammableNear(IBlockAccess w, BlockPos p, int r = 2)
    {
        for (int dx = -r; dx <= r; dx++)
        for (int dy = -1; dy <= r; dy++)
        for (int dz = -r; dz <= r; dz++)
        {
            var n = p.Offset(dx, dy, dz);
            if (Flammable.Contains(w.GetBlock(n.X, n.Y, n.Z))) return n;
        }
        return null;
    }

    /// <summary>Liquid source at <paramref name="p"/>: ANY water (8 flowing or 9 still) / lava (10/11) id with meta 0
    /// (ItemBucket b1.8.1 checks material + meta 0). Meta 1..7 is a flow level, meta &gt;= 8 falling liquid.</summary>
    public static bool IsSource(IBlockAccess w, BlockPos p, bool lava) => IsSource(w.GetBlock(p.X, p.Y, p.Z), w.GetMeta(p.X, p.Y, p.Z), lava);

    public static bool IsSource(int id, int meta, bool lava) => lava ? Blocks.IsLavaSource(id, meta) : Blocks.IsWaterSource(id, meta);

    /// <summary>A water source that comes back after scooping (BlockFlowing b1.8.1: at least 2 horizontal source neighbours
    /// over a solid block or a water source): part of an infinite pool. Lava never refills.</summary>
    public static bool Refills(IBlockAccess w, BlockPos p)
    {
        int n = SkillWorld.Horizontal.Count(o => IsSource(w, p + o, false));
        if (n < 2) return false;
        var below = p.Down();
        return BlockShapes.IsFullCube(w, below.X, below.Y, below.Z) || IsSource(w, below, false);
    }

    /// <summary>"water source" / "flowing water (level 3)" / "falling water".</summary>
    public static string Describe(int id, int meta)
    {
        string what = BlockShapes.IsLava(id) ? "lava" : "water";
        return meta == 0 ? $"{what} source" : meta >= 8 ? $"falling {what}" : $"flowing {what} (level {meta})";
    }
}

/// <summary>Bucket primitives on public client calls: a bucket is used in the look direction (the server raytraces from
/// the player, ItemBucket.onItemRightClick), so we aim first, then use the item.</summary>
public static class BucketOps
{
    /// <summary>First liquid or solid block along the ray eye→target (what an empty bucket's server raytrace hits).</summary>
    public static BlockPos? FirstHit(IBlockAccess w, Vec3d eye, Vec3d target, bool liquids)
    {
        var d = target - eye;
        double len = d.Length;
        var dir = d.Normalized();
        BlockPos? last = null;
        for (double t = 0.05; t <= len + 0.05; t += 0.05)
        {
            var q = eye + dir * t;
            var c = q.ToBlockPos();
            if (c == last) continue;
            last = c;
            int id = w.GetBlock(c.X, c.Y, c.Z);
            if (id == 0) continue;
            if (BlockShapes.IsLiquid(id)) { if (liquids && w.GetMeta(c.X, c.Y, c.Z) == 0) return c; continue; }
            var boxes = new List<AABB>();
            BlockShapes.AddSelectionBoxes(w, c.X, c.Y, c.Z, boxes);
            if (boxes.Any(b => b.Contains(q))) return c;
        }
        return null;
    }

    /// <summary>A point just inside an open face of the source <paramref name="src"/> such that the server's bucket raytrace
    /// (ItemBucket: eye → look, liquids on, so it stops at the first liquid SOURCE or solid block and passes flowing
    /// liquid) hits exactly <paramref name="src"/> first: never through a solid block or another source. Top face first
    /// (the open surface), then the sides; null = no clear line from <paramref name="eye"/> within <paramref name="reach"/>.</summary>
    public static Vec3d? SourceAim(IBlockAccess w, Vec3d eye, BlockPos src, double reach)
    {
        const double In = 0.02; // just inside the face, so the ray enters the cell through it
        Vec3d? best = null;
        double bestScore = double.MaxValue;
        foreach (var f in AimFaces)
        {
            var n = src.Offset(f);
            if (BlockShapes.IsFullCube(w, n.X, n.Y, n.Z)) continue; // covered face
            var o = f.Normal();
            foreach (var (u, v) in FaceGrid)
            {
                // Face centre pushed In inside along the normal, spread over the face's two in-plane axes.
                double x = src.X + 0.5 + (o.X != 0 ? o.X * (0.5 - In) : u);
                double y = src.Y + 0.5 + (o.Y != 0 ? o.Y * (0.5 - In) : o.X != 0 ? u : v);
                double z = src.Z + 0.5 + (o.Z != 0 ? o.Z * (0.5 - In) : v);
                var p = new Vec3d(x, y, z);
                double d = p.DistanceTo(eye);
                if (d > reach || FirstHit(w, eye, p, liquids: true) != src) continue;
                double score = d + (f == BlockFace.Top ? 0 : 2);
                if (score < bestScore) { bestScore = score; best = p; }
            }
            if (best is not null && f == BlockFace.Top) return best;
        }
        return best;
    }

    private static readonly BlockFace[] AimFaces = [BlockFace.Top, BlockFace.North, BlockFace.South, BlockFace.West, BlockFace.East];
    private static readonly (double U, double V)[] FaceGrid =
        [(0, 0), (-0.3, 0), (0.3, 0), (0, -0.3), (0, 0.3), (-0.3, -0.3), (0.3, 0.3), (-0.3, 0.3), (0.3, -0.3)];

    /// <summary>The source to scoop: <paramref name="pos"/> itself, the source above it (its floor was given), the nearest
    /// unprotected source of the same pool (a flowing cell was given), or the nearest one around me; infinite-pool cells
    /// (they refill) first. The error says why nothing fits (only flowing water / protected sources).</summary>
    public static (BlockPos? Src, string? Why) PickSource(SkillContext ctx, BlockPos? pos, bool lava)
    {
        string what = lava ? "lava" : "water";
        bool Usable(BlockPos p) => FluidRules.IsSource(ctx.World, p, lava) && !ctx.Protected(p);
        double far = Math.Max(12.0, ctx.Options.SearchRadius);
        if (pos is { } p0)
        {
            if (Usable(p0)) return (p0, null);
            if (!BlockShapes.IsLiquid(ctx.Block(p0)) && Usable(p0.Up())) return (p0.Up(), null);
            BlockPos? cell = BlockShapes.IsLiquid(ctx.Block(p0)) ? p0 : BlockShapes.IsLiquid(ctx.Block(p0.Up())) ? p0.Up() : null;
            if (cell is not { } c) return (null, $"no {what} at {SkillJson.Fmt(p0)} ({ctx.BlockName(p0)})");
            if (Nearest(ctx, c.Center, 6, lava) is { } s) return (s, null);
            string state = FluidRules.IsSource(ctx.World, c, lava) ? $"a protected {what} source" : FluidRules.Describe(ctx.Block(c), ctx.Meta(c));
            return (null, $"{SkillJson.Fmt(c)} is {state}; no unprotected {what} source within 6 of it{ProtectedHint(ctx, c.Center, far, lava, c)}");
        }
        foreach (double r in new[] { 12.0, far })
            if (Nearest(ctx, ctx.Feet, r, lava) is { } s) return (s, null);
        var flowing = ctx.World.FindBlocks(ctx.Feet, far, (i, m) => (lava ? BlockShapes.IsLava(i) : BlockShapes.IsWater(i)) && m != 0, 1);
        string seen = flowing.Count > 0
            ? $"; only {FluidRules.Describe(flowing[0].Id, flowing[0].Meta)} e.g. at {SkillJson.Fmt(flowing[0].Pos)} (can't be scooped)" : "";
        return (null, $"no {what} source within {far:0}{seen}{ProtectedHint(ctx, ctx.Feet, far, lava, null)}");
    }

    private static BlockPos? Nearest(SkillContext ctx, Vec3d at, double r, bool lava)
    {
        var all = ctx.World.FindBlocks(at, r, (i, m) => FluidRules.IsSource(i, m, lava), 256).Where(f => !ctx.Protected(f.Pos)).ToList();
        if (all.Count == 0) return null;
        // An infinite-pool cell refills, so prefer it over a lone source someone placed (irrigation, a demo).
        return all.OrderBy(f => !lava && FluidRules.Refills(ctx.World, f.Pos) ? 0 : 1).ThenBy(f => f.DistanceSquared).First().Pos;
    }

    private static string ProtectedHint(SkillContext ctx, Vec3d at, double r, bool lava, BlockPos? skip)
    {
        var prot = ctx.World.FindBlocks(at, r, (i, m) => FluidRules.IsSource(i, m, lava), 256)
            .FirstOrDefault(f => f.Pos != skip && ctx.Protected(f.Pos));
        return prot.Id != 0 ? $"; protected source at {SkillJson.Fmt(prot.Pos)} (farm/generator water, left alone)" : "";
    }

    /// <summary>Fills an empty bucket from the liquid source at <paramref name="src"/>.</summary>
    public static async Task<SkillResult> FillAsync(SkillContext ctx, BlockPos src, CancellationToken ct)
    {
        int id = ctx.Block(src);
        bool lava = BlockShapes.IsLava(id);
        if (!BlockShapes.IsLiquid(id)) return SkillResult.Fail($"no liquid at {SkillJson.Fmt(src)} ({ctx.BlockName(src)})");
        if (ctx.Meta(src) != 0) return SkillResult.Fail($"{SkillJson.Fmt(src)} is {FluidRules.Describe(id, ctx.Meta(src))}, not a source");
        if (!ctx.Has(Items.Bucket)) return SkillResult.Fail("no empty bucket");
        if (ctx.Protected(src)) return SkillResult.Fail(ctx.ProtectedText(src, $"{(lava ? "lava" : "water")} source at {SkillJson.Fmt(src)}"));
        var reach = await ctx.ReachAsync(src, ct, Math.Min(4.5, ctx.Game.Reach)).ConfigureAwait(false);
        if (!reach.Ok) return SkillResult.Fail($"can't reach {SkillJson.Fmt(src)}: {reach.Message}");
        var aim = SourceAim(ctx.World, ctx.World.Player.EyePosition, src, 4.5);
        if (aim is null)
        {
            await ctx.Game.GotoAsync(new GoalWithinReach(src, 2.0), null, ct).ConfigureAwait(false);
            aim = SourceAim(ctx.World, ctx.World.Player.EyePosition, src, 4.5);
        }
        if (aim is not { } point) return SkillResult.Fail($"no clear line to the source at {SkillJson.Fmt(src)}");
        int full = lava ? Items.LavaBucket : Items.WaterBucket;
        int before = ctx.Count(full);
        await ctx.Game.LookAtAsync(point, ct).ConfigureAwait(false);
        var r = await ctx.Game.UseItemAsync(Items.Bucket, ct).ConfigureAwait(false);
        if (!r.Ok) return SkillResult.From(r);
        if (!await ctx.Game.WaitUntilAsync(() => ctx.Count(full) > before, ctx.Options.BlockChangeTimeout, ct).ConfigureAwait(false))
            return SkillResult.Fail($"bucket didn't fill at {SkillJson.Fmt(src)}");
        return SkillResult.Success($"filled {(lava ? "lava" : "water")} bucket at {SkillJson.Fmt(src)}");
    }

    /// <summary>Pours a full bucket into the empty cell <paramref name="cell"/> by clicking a neighbour's face.</summary>
    public static async Task<SkillResult> EmptyAsync(SkillContext ctx, BlockPos cell, bool lava, CancellationToken ct, Func<string?>? veto = null)
    {
        int bucket = lava ? Items.LavaBucket : Items.WaterBucket;
        if (!ctx.Has(bucket)) return SkillResult.Fail($"no {(lava ? "lava" : "water")} bucket");
        if (!lava && ctx.InNether) return SkillResult.Fail("water evaporates in the nether");
        if (!SkillWorld.Replaceable(ctx.World, cell)) return SkillResult.Fail($"{SkillJson.Fmt(cell)} is not empty ({ctx.BlockName(cell)})");
        if (ctx.Protected(cell)) return SkillResult.Fail(ctx.ProtectedText(cell, SkillJson.Fmt(cell)));
        var eye = ctx.World.Player.EyePosition;
        var aim = Aiming.FindPlaceAim(ctx.World, eye, cell, 4.5, AABB.ForPlayer(ctx.Feet));
        if (aim is null)
        {
            // Step right next to it: from the side the face you need (often the floor of a hole) is usually hidden.
            var g = await ctx.Game.GotoAsync(new GoalWithinReach(cell, 2.0), null, ct).ConfigureAwait(false);
            if (!g.Ok) return SkillResult.Fail($"can't get near {SkillJson.Fmt(cell)}: {g.Message}");
            aim = Aiming.FindPlaceAim(ctx.World, ctx.World.Player.EyePosition, cell, 4.5, AABB.ForPlayer(ctx.Feet));
            if (aim is null) return SkillResult.Fail($"no block face to pour against at {SkillJson.Fmt(cell)}");
        }
        if (veto?.Invoke() is { } why) return SkillResult.Fail($"won't pour from here: {why}");
        await ctx.Game.LookAtAsync(aim.Value.Point, ct).ConfigureAwait(false);
        var r = await ctx.Game.UseItemAsync(bucket, ct).ConfigureAwait(false);
        if (!r.Ok) return SkillResult.From(r);
        bool ok = await ctx.WaitBlockAsync(cell, (id, _) => lava ? BlockShapes.IsLava(id) : BlockShapes.IsWater(id), ct).ConfigureAwait(false);
        return ok ? SkillResult.Success($"poured {(lava ? "lava" : "water")} at {SkillJson.Fmt(cell)}")
                  : SkillResult.Fail($"{(lava ? "lava" : "water")} didn't appear at {SkillJson.Fmt(cell)}");
    }
}

[Skill]
public sealed class FillBucketSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "fill_bucket", Category = SkillCategory.Liquids, Summary = "fill empty bucket from water/lava source", Args = "liquid?,pos?",
        Schema = SkillJson.Schema("liquid:str?,pos:pos?"), Estimate = TimeSpan.FromSeconds(5),
        Pre = new() { Items = [[Items.Bucket]], Needs = "bucket" },
        Keywords = "bucket water lava scoop pick up take liquid collect",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        bool lava = args.Str("liquid")?.Contains("lava", StringComparison.OrdinalIgnoreCase) == true
                    || (args.Str("liquid") is null && args.Pos() is { } given && BlockShapes.IsLava(ctx.Block(given)));
        var (src, why) = BucketOps.PickSource(ctx, args.Pos(), lava);
        if (src is not { } s) return Fail(why ?? $"no {(lava ? "lava" : "water")} source");
        return await BucketOps.FillAsync(ctx, s, ct);
    }
}

[Skill]
public sealed class EmptyBucketSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "empty_bucket", Category = SkillCategory.Liquids, Summary = "pour water bucket (lava: use move_lava)", Args = "pos",
        Schema = SkillJson.Schema("pos:pos"), Estimate = TimeSpan.FromSeconds(5),
        Pre = new() { Items = [[Items.WaterBucket]], Needs = "water bucket", Dimension = Dim.Overworld },
        Keywords = "bucket water pour place empty put down",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Pos() is not { } cell) return Fail("pos required");
        if (!args.Flag("allow_obsidian") && FluidRules.WaterContact(ctx.World, cell) is { } why) return Fail($"won't pour: {why}");
        return await BucketOps.EmptyAsync(ctx, cell, false, ct);
    }
}

/// <summary>2×2 (or 1×3) infinite water source: two buckets poured into opposite corners (or the ends of a line)
/// make every cell a source, so one can be scooped forever.</summary>
[Skill]
public sealed class InfiniteWaterSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "infinite_water", Category = SkillCategory.Liquids, Summary = "make 2x2 (or 1x3 line) infinite water source", Args = "pos?,shape?",
        Schema = SkillJson.Schema("pos:pos?,shape:str?"), Estimate = TimeSpan.FromSeconds(40),
        Pre = new() { Items = [[Items.WaterBucket]], Needs = "2 water buckets", Dimension = Dim.Overworld },
        Keywords = "infinite water source endless pool well farm irrigation spring",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (ctx.Count(Items.WaterBucket) < 2) return Fail($"needs 2 water buckets (have {ctx.Count(Items.WaterBucket)})");
        bool line = args.Str("shape")?.Contains("line", StringComparison.OrdinalIgnoreCase) == true
                    || args.Str("shape")?.Contains("3") == true;
        var cells = Pick(ctx, args.Pos(), line);
        if (cells is null) return Fail($"no safe {(line ? "1x3 trench" : "2x2 basin")} spot near me; give pos = corner at water level");

        // Dig out the basin (never my own floor, never protected blocks).
        foreach (var c in cells.Where(c => !SkillWorld.Replaceable(ctx.World, c)))
        {
            if (ctx.Protected(c)) return Fail(ctx.ProtectedText(c, P(c)));
            var d = await ctx.Game.DigAsync(c, ct);
            if (!d.Ok) return Fail($"couldn't dig the basin at {P(c)}: {d.Message}");
        }
        if (Leak(ctx.World, cells) is { } leak) return Fail($"basin would leak at {P(leak)}");
        foreach (var c in cells)
            if (FluidRules.WaterContact(ctx.World, c) is { } why) return Fail($"won't pour: {why}");

        var (a, b) = line ? (cells[0], cells[2]) : (cells[0], cells[3]);
        foreach (var c in new[] { a, b })
        {
            if (FluidRules.IsSource(ctx.World, c, false)) continue;
            var r = await BucketOps.EmptyAsync(ctx, c, false, ct);
            if (!r.Ok) return r;
        }
        bool all = await ctx.Game.WaitUntilAsync(() => cells.All(c => FluidRules.IsSource(ctx.World, c, false)), TimeSpan.FromSeconds(3), ct);
        int sources = cells.Count(c => FluidRules.IsSource(ctx.World, c, false));
        return all ? Ok($"infinite water at {P(cells[0])} ({cells.Length} sources)")
                   : Fail($"only {sources}/{cells.Length} cells became sources at {P(cells[0])}");
    }

    /// <summary>Basin cells: from <paramref name="corner"/>, else a nearby solid patch one below feet level.</summary>
    private static BlockPos[]? Pick(SkillContext ctx, BlockPos? corner, bool line)
    {
        BlockPos[] Shape(BlockPos o) => line ? [o, o.Offset(1, 0, 0), o.Offset(2, 0, 0)] : [o, o.Offset(1, 0, 0), o.Offset(0, 0, 1), o.Offset(1, 0, 1)];
        if (corner is { } c) return Shape(c);
        var me = ctx.Me;
        for (int r = 2; r <= 5; r++)
        for (int dx = -r; dx <= r; dx++)
        for (int dz = -r; dz <= r; dz++)
        {
            if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;
            var cells = Shape(new BlockPos(me.X + dx, me.Y - 1, me.Z + dz));
            if (cells.Any(p => ctx.Protected(p) || !SkillWorld.Solid(ctx.World, p) || !SkillWorld.Solid(ctx.World, p.Down()))) continue;
            if (cells.Any(p => Math.Abs(p.X + 0.5 - ctx.Feet.X) < 1.3 && Math.Abs(p.Z + 0.5 - ctx.Feet.Z) < 1.3)) continue;
            if (Leak(ctx.World, cells, assumeDug: true) is not null) continue;
            return cells;
        }
        return null;
    }

    private static BlockPos? Leak(IBlockAccess w, BlockPos[] cells, bool assumeDug = false)
    {
        foreach (var c in cells)
        {
            if (!SkillWorld.Solid(w, c.Down()) && !BlockShapes.IsWater(w.GetBlock(c.X, c.Y - 1, c.Z))) return c.Down();
            foreach (var o in SkillWorld.Horizontal)
            {
                var n = c + o;
                if (cells.Contains(n)) continue;
                int id = w.GetBlock(n.X, n.Y, n.Z);
                if (!BlockShapes.HasCollision(w, n.X, n.Y, n.Z) && !BlockShapes.IsWater(id)) return n;
            }
        }
        return null;
    }
}

/// <summary>Moves a lava source with a bucket, refusing any target where the lava could meet water (no accidental
/// obsidian/cobble), burn something, or flow onto me.</summary>
[Skill]
public sealed class MoveLavaSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "move_lava", Category = SkillCategory.Liquids, Summary = "bucket a lava source to pos, never near water", Args = "to,from?,allow_obsidian?",
        Schema = SkillJson.Schema("to:pos,from:pos?,allow_obsidian:bool?"), Safety = SafetyClass.Destructive, Estimate = TimeSpan.FromSeconds(15),
        Pre = new() { Items = [[Items.Bucket, Items.LavaBucket]], Needs = "bucket", SurvivalGate = true },
        Keywords = "lava move relocate bucket place pour carry",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Pos("to") is not { } to) return Fail("to required");
        bool allow = args.Flag("allow_obsidian");
        if (Check(ctx, to, allow) is { } veto) return Fail($"won't place lava: {veto}");

        if (!ctx.Has(Items.LavaBucket))
        {
            var from = args.Pos("from") ?? ctx.Find((id, m) => BlockShapes.IsLava(id) && m == 0, 12, 4).Select(f => f.Pos).FirstOrDefault(p => p != to);
            if (!FluidRules.IsSource(ctx.World, from, true)) return Fail("no lava source nearby (give from)");
            var f = await BucketOps.FillAsync(ctx, from, ct);
            if (!f.Ok) return f;
        }
        if (Check(ctx, to, allow) is { } veto2) return Fail($"won't place lava: {veto2} (lava still in my bucket)");
        var r = await BucketOps.EmptyAsync(ctx, to, true, ct, () => Check(ctx, to, allow));
        return r.Ok ? Ok($"moved lava to {P(to)}") : r;
    }

    public static string? Check(SkillContext ctx, BlockPos to, bool allowObsidian)
    {
        if (!SkillWorld.Replaceable(ctx.World, to)) return $"{P(to)} is not empty";
        if (ctx.Protected(to)) return ctx.ProtectedText(to, P(to));
        if (!allowObsidian && FluidRules.LavaContact(ctx.World, to, ctx.InNether) is { } why) return why;
        if (FluidRules.FlammableNear(ctx.World, to) is { } burn) return $"{SkillWorld.Name(ctx.Block(burn))} at {P(burn)} would catch fire";
        var area = FluidRules.FlowArea(ctx.World, to, ctx.InNether ? FluidRules.NetherLavaReach : FluidRules.LavaReach);
        var me = ctx.Me;
        if (area.Any(c => c.X == me.X && c.Z == me.Z && c.Y >= me.Y - 1 && c.Y <= me.Y + 1))
            return "it could flow onto me";
        return null;
    }
}

/// <summary>Turns lava sources into obsidian by pouring water onto them (then scoops the water back).</summary>
[Skill]
public sealed class MakeObsidianSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "make_obsidian", Category = SkillCategory.Liquids, Summary = "pour water on lava sources -> obsidian", Args = "pos?,count?",
        Schema = SkillJson.Schema("pos:pos?,count:int?"), Safety = SafetyClass.Destructive, Estimate = TimeSpan.FromSeconds(20),
        Pre = new() { Items = [[Items.WaterBucket]], Needs = "water bucket", Dimension = Dim.Overworld, SurvivalGate = true },
        Keywords = "obsidian obby lava water portal nether make create harden",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int want = Clamp(args.Int("count"), 1, 1, 14);
        var targets = args.Pos() is { } one ? [one]
            : ctx.Find((id, m) => BlockShapes.IsLava(id) && m == 0, 12, 32).Select(f => f.Pos)
                .Where(p => SkillWorld.Replaceable(ctx.World, p.Up()) && !BlockShapes.IsLiquid(ctx.Block(p.Up()))).ToList();
        int made = 0;
        foreach (var lava in targets)
        {
            if (made >= want) break;
            ct.ThrowIfCancellationRequested();
            if (!FluidRules.IsSource(ctx.World, lava, true)) continue;
            if (ctx.Protected(lava)) continue; // e.g. a cobble generator's lava
            var above = lava.Up();
            if (!SkillWorld.Air(ctx.World, above)) continue;
            // Water must flow onto the lava (a full bucket aimed at the lava would replace it, not harden it):
            // pour next to the cell above the lava, on solid ground, and let it run over.
            var pour = SkillWorld.Horizontal.Select(o => above + o)
                .FirstOrDefault(c => SkillWorld.Air(ctx.World, c) && SkillWorld.Solid(ctx.World, c.Down()) && !ctx.Protected(c));
            if (pour == default) continue;
            var r = await BucketOps.EmptyAsync(ctx, pour, false, ct);
            if (!r.Ok) return made > 0 ? Ok($"made {made} obsidian; then {r.Text}") : r;
            await ctx.WaitBlockAsync(lava, (id, _) => id == Blocks.Obsidian, ct, TimeSpan.FromSeconds(3));
            if (ctx.Block(lava) == Blocks.Obsidian) made++;
            if (ctx.Has(Items.Bucket) && FluidRules.IsSource(ctx.World, pour, false))
                await BucketOps.FillAsync(ctx, pour, ct); // take the water back
            if (!ctx.Has(Items.WaterBucket)) break;
        }
        return made > 0 ? Ok($"made {made} obsidian") : Fail("no reachable lava source with open air above");
    }
}
