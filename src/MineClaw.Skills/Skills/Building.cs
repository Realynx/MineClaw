using System.Text.Json;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

internal static class BuildOps
{
    /// <summary>Places blocks into <paramref name="cells"/> in support order (each next to something solid), so the
    /// structure grows from what exists and nothing is placed in mid-air. Skips filled, protected and unsupported cells.</summary>
    public static async Task<(int Placed, int Skipped, string? Error)> PlaceAllAsync(SkillContext ctx, IReadOnlyCollection<BlockPos> cells,
        int block, CancellationToken ct, Func<BlockPos, bool>? veto = null)
    {
        var todo = new HashSet<BlockPos>(cells.Where(c => SkillWorld.Replaceable(ctx.World, c)));
        int placed = 0, skipped = cells.Count - todo.Count;
        var me = ctx.Me;
        while (todo.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var next = todo.Where(c => BlockFaceExtensions.All.Any(f => SkillWorld.Solid(ctx.World, c.Offset(f))))
                .OrderBy(c => c.DistanceSquaredTo(ctx.Me)).FirstOrDefault();
            if (!todo.Contains(next)) { skipped += todo.Count; break; } // nothing left touches support
            todo.Remove(next);
            if (ctx.Protected(next) || veto?.Invoke(next) == true) { skipped++; continue; }
            if (!ctx.Has(block)) return (placed, skipped + todo.Count + 1, $"out of {SkillWorld.Name(block)}");
            var r = await ctx.Game.PlaceAsync(next, block, ct);
            if (r.Ok) placed++;
            else
            {
                skipped++;
                if (r.Message.Contains("void", StringComparison.OrdinalIgnoreCase) || r.Message.Contains("danger", StringComparison.OrdinalIgnoreCase))
                    return (placed, skipped + todo.Count, r.Message);
            }
        }
        return (placed, skipped, null);
    }

    public static IEnumerable<BlockPos> Box(BlockPos a, BlockPos b)
    {
        for (int y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++)
        for (int x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++)
        for (int z = Math.Min(a.Z, b.Z); z <= Math.Max(a.Z, b.Z); z++)
            yield return new BlockPos(x, y, z);
    }

    public static string Report(string what, (int Placed, int Skipped, string? Error) r, SkillContext ctx, int block) =>
        $"{what}: placed {r.Placed}" + (r.Skipped > 0 ? $", skipped {r.Skipped}" : "") + (r.Error is null ? "" : $" ({r.Error})")
        + $"; {ctx.Count(block)} {SkillWorld.Name(block)} left";

    /// <summary>Hostile mob close enough to knock me off an edge.</summary>
    public static string? HostileVeto(SkillContext ctx, double r = 6) =>
        ctx.World.Entities.NearestHostile(ctx.Feet, r) is { } m ? $"{m.DisplayName} within {r:0}: not working at the edge" : null;
}

[Skill]
public sealed class PlaceLineSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "place_line", Category = SkillCategory.Building, Summary = "place a straight line of blocks from->to", Args = "from,to,block?",
        Schema = SkillJson.Schema("from:pos,to:pos,block:str?"), Safety = SafetyClass.Edge, Estimate = TimeSpan.FromSeconds(30),
        Pre = new() { Needs = "blocks" }, Keywords = "line row bridge path walkway place blocks build",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Pos("from") is not { } a || args.Pos("to") is not { } b) return Fail("from and to required");
        int axes = (a.X != b.X ? 1 : 0) + (a.Y != b.Y ? 1 : 0) + (a.Z != b.Z ? 1 : 0);
        if (axes > 1) return Fail("from/to must differ along one axis (use fill_floor for areas)");
        if (a.ManhattanTo(b) > 64) return Fail("line longer than 64");
        if (SkillWorld.BuildingBlock(ctx, args.Str("block")) is not { } block) return Fail("no building blocks");
        if (BuildOps.HostileVeto(ctx) is { } h) return Fail(h);
        var r = await BuildOps.PlaceAllAsync(ctx, BuildOps.Box(a, b).ToList(), block, ct);
        return new SkillResult(r.Placed > 0 || r.Skipped == a.ManhattanTo(b) + 1, BuildOps.Report("line", r, ctx, block));
    }
}

[Skill]
public sealed class FillFloorSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "fill_floor", Category = SkillCategory.Building, Summary = "fill a flat area (floor/platform) from->to", Args = "from,to,block?",
        Schema = SkillJson.Schema("from:pos,to:pos,block:str?"), Safety = SafetyClass.Edge, Estimate = TimeSpan.FromSeconds(60),
        Pre = new() { Needs = "blocks" }, Keywords = "floor platform area fill flat pave patio base surface",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Pos("from") is not { } a || args.Pos("to") is not { } b) return Fail("from and to required");
        if (a.Y != b.Y) return Fail("floor must be flat (same y)");
        var cells = BuildOps.Box(a, b).ToList();
        if (cells.Count > 256) return Fail("area larger than 256");
        if (SkillWorld.BuildingBlock(ctx, args.Str("block")) is not { } block) return Fail("no building blocks");
        if (BuildOps.HostileVeto(ctx) is { } h) return Fail(h);
        var r = await BuildOps.PlaceAllAsync(ctx, cells, block, ct);
        return new SkillResult(r.Error is null || r.Placed > 0, BuildOps.Report("floor", r, ctx, block));
    }
}

[Skill]
public sealed class BuildWallSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "build_wall", Category = SkillCategory.Building, Summary = "wall along from->to, height N, bottom-up", Args = "from,to,height?,block?",
        Schema = SkillJson.Schema("from:pos,to:pos,height:int?,block:str?"), Safety = SafetyClass.Edge, Estimate = TimeSpan.FromSeconds(60),
        Pre = new() { Needs = "blocks" }, Keywords = "wall fence barrier enclose railing border mob proof",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Pos("from") is not { } a || args.Pos("to") is not { } b) return Fail("from and to required");
        if (a.X != b.X && a.Z != b.Z) return Fail("walls run along x or z");
        int h = Clamp(args.Int("height"), 2, 1, 8);
        var cells = BuildOps.Box(a, b with { Y = a.Y + h - 1 }).ToList();
        if (cells.Count > 256) return Fail("wall larger than 256 blocks");
        if (SkillWorld.BuildingBlock(ctx, args.Str("block")) is not { } block) return Fail("no building blocks");
        var r = await BuildOps.PlaceAllAsync(ctx, cells, block, ct);
        return new SkillResult(r.Placed > 0 || r.Error is null, BuildOps.Report("wall", r, ctx, block));
    }
}

/// <summary>
/// Grows the island floor outward one ring at a time. Only cells touching existing floor are placed (no mid-air
/// blocks), the bot never targets a standing cell over the void (the client's pathfinder sneaks at edges and the
/// EdgeGuard clamps input), and it stops when a hostile is near or survival gating says no.
/// </summary>
[Skill]
public sealed class ExpandIslandSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "expand_island", Category = SkillCategory.Building, Summary = "grow island edge outward by rows, void-safe", Args = "side?,rows?,max?,block?",
        Schema = SkillJson.Schema("side:str?,rows:int?,max:int?,block:str?"), Safety = SafetyClass.Edge, Estimate = TimeSpan.FromMinutes(2),
        Pre = new() { Needs = "blocks", SurvivalGate = true },
        Keywords = "expand island edge bigger platform grow extend widen larger land skyblock floor",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (SkillWorld.BuildingBlock(ctx, args.Str("block")) is not { } block) return Fail("no building blocks");
        int rows = Clamp(args.Int("rows"), 1, 1, 4), max = Clamp(args.Int("max"), 32, 1, 128);
        var dir = args.Str("side")?.ToLowerInvariant() switch
        {
            "east" or "e" or "+x" => new BlockPos(1, 0, 0), "west" or "w" or "-x" => new BlockPos(-1, 0, 0),
            "south" or "s" or "+z" => new BlockPos(0, 0, 1), "north" or "n" or "-z" => new BlockPos(0, 0, -1),
            _ => (BlockPos?)null,
        };
        int y = ctx.Me.Y - 1; // the floor layer I'm standing on
        if (!SkillWorld.Solid(ctx.World, ctx.Me.Down()) && Safety.SupportBlocks(ctx.World, ctx.Feet).FirstOrDefault() is { } sup) y = sup.Y;
        int placed = 0;
        string? stop = null;
        var start = ctx.Feet;
        var missed = new HashSet<BlockPos>(); // edge cells left air (protected, no support, refused): "skipped"
        for (int ring = 0; ring < rows && placed < max && stop is null; ring++)
        {
            var edge = EdgeCells(ctx, start, y, dir).Take(max - placed).ToList();
            if (edge.Count == 0) { if (ring == 0) stop = "no open edge on that side"; break; }
            if (BuildOps.HostileVeto(ctx) is { } h) { stop = h; break; }
            if (ctx.SurvivalVeto("expand_island") is { } sv) { stop = sv; break; }
            var r = await FillRowAsync(ctx, edge, block, c => !SkillWorld.Solid(ctx.World, c - (dir ?? Inward(ctx.World, c))), ct);
            placed += r.Placed;
            missed.UnionWith(r.Missed);
            stop = r.Error;
        }
        missed.RemoveWhere(c => !SkillWorld.Air(ctx.World, c));
        string text = $"expanded island by {placed} block(s) at y {y}" + (missed.Count > 0 ? $", skipped {missed.Count}" : "") + (stop is null ? "" : $" ({stop})")
                      + $"; {ctx.Count(block)} {SkillWorld.Name(block)} left";
        return new SkillResult(placed > 0, text);
    }

    /// <summary>
    /// One row along a (possibly jagged) edge, re-planned after every placement so each new block is support for the next:
    /// cells placeable from where I stand first, then the neighbours of the newest block (its side face), then the nearest.
    /// A cell that failed is retried once a new block touches it. The client sneaks every placement and leans out over the
    /// edge (EdgeGuard on) when the floor block's outer face is the only support.
    /// </summary>
    private static async Task<(int Placed, List<BlockPos> Missed, string? Error)> FillRowAsync(SkillContext ctx, List<BlockPos> cells, int block,
        Func<BlockPos, bool> veto, CancellationToken ct)
    {
        var w = ctx.World;
        var todo = new HashSet<BlockPos>(cells.Where(c => SkillWorld.Replaceable(w, c)));
        var missed = todo.Where(c => ctx.Protected(c) || veto(c)).ToList();
        todo.ExceptWith(missed);
        var failedAt = new Dictionary<BlockPos, int>(); // cell → its support count when it failed
        int placed = 0;
        BlockPos? last = null;
        int Supports(BlockPos c) => BlockFaceExtensions.All.Count(f => SkillWorld.Solid(w, c.Offset(f)));
        while (todo.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var me = ctx.Feet;
            var eye = w.Player.EyePosition;
            var next = todo.Where(c => Supports(c) > (failedAt.TryGetValue(c, out int n) ? n : 0))
                .OrderBy(c => Aiming.FindPlaceAim(w, eye, c, ctx.Game.Reach, AABB.ForPlayer(me)) is null ? 1 : 0)
                .ThenBy(c => last is { } l && Math.Abs(c.X - l.X) + Math.Abs(c.Z - l.Z) == 1 && c.Y == l.Y ? 0 : 1)
                .ThenBy(c => Math.Pow(c.X + 0.5 - me.X, 2) + Math.Pow(c.Z + 0.5 - me.Z, 2))
                .Cast<BlockPos?>().FirstOrDefault();
            if (next is not { } c) break; // nothing left touches (new) support
            if (!ctx.Has(block)) return (placed, missed.Concat(todo).ToList(), $"out of {SkillWorld.Name(block)}");
            var r = await ctx.Game.PlaceAsync(c, block, ct);
            if (r.Ok && !SkillWorld.Replaceable(w, c))
            {
                placed++;
                todo.Remove(c);
                last = c;
                continue;
            }
            failedAt[c] = Supports(c);
            if (r.Message.Contains("void", StringComparison.OrdinalIgnoreCase) || r.Message.Contains("danger", StringComparison.OrdinalIgnoreCase))
                return (placed, missed.Concat(todo).ToList(), r.Message);
        }
        return (placed, missed.Concat(todo).ToList(), null);
    }

    /// <summary>Open floor cells at layer y next to existing floor within 16 of <paramref name="center"/> (where the skill
    /// started, so row 2 follows row 1 wherever I ended up), optionally only on one side, nearest to me first. Protected
    /// floor still counts as support (nothing is broken); cells in chunks I don't have live are left out.</summary>
    private static IEnumerable<BlockPos> EdgeCells(SkillContext ctx, Vec3d center, int y, BlockPos? dir)
    {
        var w = ctx.World;
        var me = ctx.Feet;
        var seen = new HashSet<BlockPos>();
        foreach (var f in w.FindBlocks(center, 16, (id, _) => id != 0 && !BlockShapes.IsLiquid(id), 4096).Where(f => f.Pos.Y == y))
        {
            if (!SkillWorld.Solid(w, f.Pos)) continue;
            foreach (var o in dir is { } d ? [d] : SkillWorld.Horizontal)
            {
                var c = f.Pos + o;
                if (!w.IsLoaded(c.X, c.Z) || !SkillWorld.Air(w, c) || !SkillWorld.OverVoid(w, c) || !SkillWorld.Air(w, c.Up())) continue;
                seen.Add(c);
            }
        }
        return seen.OrderBy(c => Math.Pow(c.X + 0.5 - me.X, 2) + Math.Pow(c.Z + 0.5 - me.Z, 2));
    }

    private static BlockPos Inward(IBlockAccess w, BlockPos c) =>
        SkillWorld.Horizontal.FirstOrDefault(o => SkillWorld.Solid(w, c - o), new BlockPos(1, 0, 0));
}

[Skill]
public sealed class PillarUpSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "pillar_up", Category = SkillCategory.Building, Summary = "jump-place blocks under me to rise N", Args = "height,block?",
        Schema = SkillJson.Schema("height:int,block:str?"), Safety = SafetyClass.Edge, Estimate = TimeSpan.FromSeconds(15),
        Pre = new() { Needs = "blocks", SurvivalGate = true }, Keywords = "pillar tower up climb rise jump higher nerd pole",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int h = Clamp(args.Int("height"), 3, 1, 32);
        if (SkillWorld.BuildingBlock(ctx, args.Str("block")) is not { } block) return Fail("no building blocks");
        if (ctx.Count(block) < h) return Fail($"needs {h} blocks, have {ctx.Count(block)}");
        var me = ctx.Me;
        for (int dy = 2; dy <= h + 1; dy++)
            if (!SkillWorld.Air(ctx.World, me.Up(dy))) return Fail($"ceiling at {P(me.Up(dy))}");
        var r = await ctx.Game.GotoAsync(new GoalBlock(me.Up(h)), GotoOptions.Default with { AllowPlace = true, ScaffoldBudget = h + 1 }, ct);
        return r.Ok ? Ok($"pillared up {h} to y {ctx.Me.Y}") : Fail($"pillar stopped at y {ctx.Me.Y}: {r.Message}");
    }
}

/// <summary>Goes down N by digging a 1-wide staircase beside me (never the block I stand on, never into the void).</summary>
[Skill]
public sealed class PillarDownSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "pillar_down", Category = SkillCategory.Building, Summary = "go down N via dug staircase, void-checked", Args = "depth,side?",
        Schema = SkillJson.Schema("depth:int,side:str?"), Safety = SafetyClass.Edge, Estimate = TimeSpan.FromSeconds(30),
        Pre = new() { SurvivalGate = true }, Keywords = "down descend dig staircase lower go down pillar",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int depth = Clamp(args.Int("depth"), 3, 1, 16);
        var start = ctx.Me;
        foreach (var o in SkillWorld.Horizontal)
        {
            // Check the whole staircase first: every step needs a solid floor that isn't over the void, and no liquids.
            bool ok = true;
            for (int i = 1; i <= depth && ok; i++)
            {
                var c = start + new BlockPos(o.X * i, -i, o.Z * i);
                var floor = c.Down();
                ok = SkillWorld.Solid(ctx.World, floor) && !SkillWorld.OverVoid(ctx.World, floor.Down())
                     && !BlockShapes.IsLiquid(ctx.Block(c)) && !BlockShapes.IsLiquid(ctx.Block(c.Up()))
                     && !ctx.Protected(c) && !ctx.Protected(c.Up()) && !ctx.Protected(c.Up(2))
                     && FluidRules.LavaContact(ctx.World, c, false) is null;
            }
            if (!ok) continue;
            for (int i = 1; i <= depth; i++)
            {
                var c = start + new BlockPos(o.X * i, -i, o.Z * i);
                foreach (var b in new[] { c.Up(2), c.Up(), c })
                    if (!SkillWorld.Replaceable(ctx.World, b))
                    {
                        var d = await ctx.Game.DigAsync(b, ct);
                        if (!d.Ok) return Fail($"stair {i}: {d.Message}");
                    }
                var g = await ctx.Game.GotoAsync(new GoalBlock(c), null, ct);
                if (!g.Ok) return Fail($"stair {i}: {g.Message}");
            }
            return Ok($"down {depth} to y {ctx.Me.Y}");
        }
        return Fail($"no safe staircase {depth} down from here (void, liquid or protected)");
    }
}

[Skill]
public sealed class LightAreaSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "light_area", Category = SkillCategory.Building, Summary = "torch dark spawnable floor (light<8) nearby", Args = "radius?,max?",
        Schema = SkillJson.Schema("radius:int?,max:int?"), Estimate = TimeSpan.FromSeconds(40),
        Pre = new() { Items = [[Blocks.Torch]], Needs = "torches" },
        Keywords = "torch torches light lamp dark mobs spawn spawnproof brighten night",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int radius = Clamp(args.Int("radius"), 10, 3, 20), max = Clamp(args.Int("max"), 8, 1, 32);
        var lit = new HashSet<BlockPos>();
        var dark = Dark(ctx, radius).ToList();
        if (dark.Count == 0) return Ok($"no dark spawnable spots within {radius}");
        int n = 0;
        foreach (var c in dark)
        {
            if (n >= max || !ctx.Has(Blocks.Torch)) break;
            if (lit.Any(l => l.ManhattanTo(c) <= 6)) continue; // a torch lights ≥8 within 6 blocks
            var r = await ctx.Game.PlaceAsync(c, Blocks.Torch, ct);
            if (!r.Ok) continue;
            n++;
            lit.Add(c);
        }
        int left = dark.Count(c => !lit.Any(l => l.ManhattanTo(c) <= 6));
        return n > 0 ? Ok($"placed {n} torch(es); ~{left} dark spots left; {ctx.Count(Blocks.Torch)} torches") : Fail("couldn't place torches");
    }

    /// <summary>Air cells on an opaque full floor with headroom and block light ≤ 7 (b1.8.1 hostile spawn rule).</summary>
    public static IEnumerable<BlockPos> Dark(SkillContext ctx, int radius) =>
        ctx.Find((id, _) => id == 0, radius, 4096).Select(f => f.Pos)
            .Where(p => SkillWorld.Solid(ctx.World, p.Down()) && !Blocks.Get(ctx.Block(p.Down())).Transparent
                        && SkillWorld.Air(ctx.World, p.Up()) && ctx.World.GetBlockLight(p.X, p.Y, p.Z) <= 7 && !ctx.Protected(p));
}

[Skill]
public sealed class PlaceLadderSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "place_ladder", Category = SkillCategory.Building, Summary = "ladder column up a wall from pos", Args = "pos,height?",
        Schema = SkillJson.Schema("pos:pos,height:int?"), Estimate = TimeSpan.FromSeconds(20),
        Pre = new() { Items = [[Blocks.Ladder]], Needs = "ladders" }, Keywords = "ladder climb wall up shaft",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Pos() is not { } p) return Fail("pos required (the first ladder cell, next to a wall)");
        int h = Clamp(args.Int("height"), 1, 1, 32);
        int n = 0;
        for (int i = 0; i < h; i++)
        {
            var c = p.Up(i);
            if (ctx.Block(c) == Blocks.Ladder) { n++; continue; }
            if (!SkillWorld.Horizontal.Any(o => SkillWorld.Solid(ctx.World, c + o))) return Fail($"no wall behind {P(c)} ({n} placed)");
            if (!ctx.Has(Blocks.Ladder)) return Fail($"out of ladders ({n} placed)");
            var r = await ctx.Game.PlaceAsync(c, Blocks.Ladder, ct);
            if (!r.Ok) return Fail($"ladder at {P(c)}: {r.Message} ({n} placed)");
            // Ladders need a side face: one placed against the floor gets meta 0 and pops off.
            if (!await ctx.WaitBlockAsync(c, (id, m) => id == Blocks.Ladder && m is >= 2 and <= 5, ct))
                return Fail($"ladder at {P(c)} didn't stick to a wall ({n} placed)");
            n++;
        }
        return Ok($"ladder x{n} at {P(p)}");
    }
}

[Skill]
public sealed class PlaceTorchSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "place_torch", Category = SkillCategory.Building, Summary = "place one torch at pos (or my feet)", Args = "pos?",
        Schema = SkillJson.Schema("pos:pos?"), Estimate = TimeSpan.FromSeconds(4),
        Pre = new() { Items = [[Blocks.Torch]], Needs = "torch" }, Keywords = "torch light place",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var p = args.Pos() ?? SkillWorld.Horizontal.Select(o => ctx.Me + o).FirstOrDefault(c => SkillWorld.Air(ctx.World, c) && SkillWorld.Solid(ctx.World, c.Down()));
        if (!SkillWorld.Replaceable(ctx.World, p)) return Fail($"{P(p)} is occupied");
        return SkillResult.From(await ctx.Game.PlaceAsync(p, Blocks.Torch, ct));
    }
}
