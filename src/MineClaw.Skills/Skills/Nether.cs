using System.Collections.Concurrent;
using System.Text.Json;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

public sealed record PortalPair(BlockPos Overworld, BlockPos Nether, DateTimeOffset Seen);

/// <summary>Per-bot state that outlives one skill call (portal pairs, scratch). The Bot layer can persist it.</summary>
public sealed class SkillState
{
    private readonly List<PortalPair> _portals = new();
    public ConcurrentDictionary<string, string> Bag { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PortalPair> Portals { get { lock (_portals) return _portals.ToArray(); } }

    public void AddPortal(PortalPair p)
    {
        lock (_portals)
        {
            _portals.RemoveAll(o => o.Overworld.ManhattanTo(p.Overworld) <= 4 || o.Nether.ManhattanTo(p.Nether) <= 4);
            _portals.Add(p);
        }
    }

    /// <summary>The pair whose portal on this side is nearest to <paramref name="pos"/>.</summary>
    public PortalPair? Nearest(BlockPos pos, bool inNether) =>
        Portals.OrderBy(p => (inNether ? p.Nether : p.Overworld).DistanceSquaredTo(pos)).Cast<PortalPair?>().FirstOrDefault();

    /// <summary>b1.8.1 coordinate scale: nether = overworld / 8 (x, z), y unchanged.</summary>
    public static BlockPos Scale(BlockPos p, bool toNether) =>
        toNether ? new BlockPos((int)Math.Floor(p.X / 8.0), p.Y, (int)Math.Floor(p.Z / 8.0)) : new BlockPos(p.X * 8, p.Y, p.Z * 8);
}

/// <summary>
/// Nether hazard profile: lava seas and falls, ghast fireballs (knockback — stay off edges, they're ranged), zombie
/// pigmen (neutral, the whole group aggros if one is hit: never attack them), beds explode, netherrack burns forever
/// once lit, soul sand slows, gravel falls, no void at the bottom but bedrock under a lava ocean.
/// </summary>
public static class NetherHazards
{
    public static string Scan(SkillContext ctx)
    {
        var me = ctx.Feet;
        var parts = new List<string>();
        int lava = ctx.Find((id, _) => BlockShapes.IsLava(id), 6, 512).Count;
        if (lava > 0) parts.Add($"lava x{lava} within 6");
        var ghasts = ctx.World.Entities.Nearby(me, 48, e => e.Kind == EntityKind.Mob && e.TypeId == MobTypes.Ghast);
        if (ghasts.Count > 0) parts.Add($"{ghasts.Count} ghast(s) within 48 (fireballs: keep off edges)");
        var pigmen = ctx.World.Entities.Nearby(me, 16, e => e.Kind == EntityKind.Mob && e.TypeId == MobTypes.PigZombie);
        if (pigmen.Count > 0) parts.Add($"{pigmen.Count} pigmen (neutral: never hit)");
        if (ctx.Find(Blocks.Fire, 4, 8).Count > 0) parts.Add("fire nearby");
        if (ctx.Block(ctx.Me.Down()) == Blocks.SoulSand) parts.Add("on soul sand (slow)");
        if (ctx.Find(Blocks.Gravel, 3, 8).Any(f => f.Pos.Y > ctx.Me.Y + 1)) parts.Add("gravel overhead");
        if (Safety.EdgeDistance(ctx.World, ctx.Me.X, ctx.Me.Y, ctx.Me.Z) is var d and <= 2) parts.Add($"edge {d} away");
        return parts.Count == 0 ? "no nether hazards close" : string.Join("; ", parts);
    }

    /// <summary>Edge/combat work in the nether with a ghast in range is refused (knockback into lava).</summary>
    public static string? EdgeVeto(SkillContext ctx) =>
        ctx.InNether && ctx.World.Entities.Nearby(ctx.Feet, 48, e => e.Kind == EntityKind.Mob && e.TypeId == MobTypes.Ghast).Count > 0
            ? "ghast in range: no edge work in the nether until it's gone" : null;

    public static bool NeverAttack(MineClaw.World.Entity e) => e.Kind == EntityKind.Mob && e.TypeId == MobTypes.PigZombie;
}

internal static class PortalFrames
{
    /// <summary>Frame cells for a portal whose bottom-left frame corner is <paramref name="o"/> along axis x or z:
    /// 4 wide × 5 tall; corners (any block) separate so the sides have support while building.</summary>
    public static (List<BlockPos> Obsidian, List<BlockPos> Corners, List<BlockPos> Inside) Cells(BlockPos o, bool alongX)
    {
        BlockPos At(int u, int v) => alongX ? o.Offset(u, v, 0) : o.Offset(0, v, u);
        var obs = new List<BlockPos>();
        var corners = new List<BlockPos> { At(0, 0), At(3, 0), At(0, 4), At(3, 4) };
        var inside = new List<BlockPos>();
        for (int u = 1; u <= 2; u++) { obs.Add(At(u, 0)); obs.Add(At(u, 4)); }
        for (int v = 1; v <= 3; v++) { obs.Add(At(0, v)); obs.Add(At(3, v)); for (int u = 1; u <= 2; u++) inside.Add(At(u, v)); }
        return (obs, corners, inside);
    }

    /// <summary>A complete frame around an interior whose bottom-left cell is <paramref name="i"/>.</summary>
    public static bool IsFrame(IBlockAccess w, BlockPos i, bool alongX)
    {
        var (obs, _, inside) = Cells(alongX ? i.Offset(-1, -1, 0) : i.Offset(0, -1, -1), alongX);
        return obs.All(p => w.GetBlock(p.X, p.Y, p.Z) == Blocks.Obsidian)
               && inside.All(p => w.GetBlock(p.X, p.Y, p.Z) is 0 or Blocks.Fire or Blocks.Portal);
    }

    public static (BlockPos Inside, bool AlongX)? FindFrame(SkillContext ctx, double radius = 12)
    {
        foreach (var f in ctx.Find(Blocks.Obsidian, radius, 256))
        {
            var i = f.Pos.Up();
            foreach (bool x in new[] { true, false })
                if (IsFrame(ctx.World, i, x)) return (i, x);
        }
        return null;
    }
}

[Skill]
public sealed class BuildPortalSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "build_portal", Category = SkillCategory.Nether, Summary = "build 4x5 obsidian nether portal frame", Args = "pos?,axis?",
        Schema = SkillJson.Schema("pos:pos?,axis:str?"), Safety = SafetyClass.Edge, Estimate = TimeSpan.FromSeconds(60),
        Pre = new() { Items = [[Blocks.Obsidian]], Needs = "10 obsidian+4 blocks" },
        Keywords = "nether portal frame obsidian build make gateway hell",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (ctx.Count(Blocks.Obsidian) < 10) return Fail($"needs 10 obsidian (have {ctx.Count(Blocks.Obsidian)}); make_obsidian first");
        bool alongX = !(args.Str("axis")?.Contains('z', StringComparison.OrdinalIgnoreCase) ?? false);
        var origin = args.Pos() ?? Spot(ctx, alongX);
        if (origin is not { } o) return Fail("no flat 4-wide spot with solid floor nearby; give pos (bottom-left corner)");
        var (obs, corners, inside) = PortalFrames.Cells(o, alongX);
        if (inside.Concat(obs).Concat(corners).Any(ctx.Protected)) return Fail("portal area overlaps protected blocks");
        if (inside.Any(p => !SkillWorld.Replaceable(ctx.World, p))) return Fail("portal interior isn't clear");
        int corner = SkillWorld.BuildingBlock(ctx) is { } b && b != Blocks.Obsidian ? b : Blocks.Obsidian;
        if (corner == Blocks.Obsidian && ctx.Count(Blocks.Obsidian) < 14) return Fail("needs 4 corner blocks (cobble/dirt) or 14 obsidian");
        var c = await BuildOps.PlaceAllAsync(ctx, corners.Where(p => p.Y == o.Y).ToList(), corner, ct);
        var r = await BuildOps.PlaceAllAsync(ctx, obs.Where(p => p.Y < o.Y + 4).ToList(), Blocks.Obsidian, ct);
        var top = await BuildOps.PlaceAllAsync(ctx, corners.Where(p => p.Y > o.Y).ToList(), corner, ct);
        var t = await BuildOps.PlaceAllAsync(ctx, obs.Where(p => p.Y == o.Y + 4).ToList(), Blocks.Obsidian, ct);
        bool ok = PortalFrames.IsFrame(ctx.World, inside[0], alongX);
        return ok ? Ok($"portal frame at {P(o)} ({(alongX ? "x" : "z")} axis); light_portal next")
                  : Fail($"frame incomplete at {P(o)}: placed {c.Placed + r.Placed + top.Placed + t.Placed}" + ((r.Error ?? t.Error) is { } e ? $" ({e})" : ""));
    }

    private static BlockPos? Spot(SkillContext ctx, bool alongX)
    {
        var me = ctx.Me;
        for (int r = 2; r <= 6; r++)
        foreach (var (dx, dz) in new[] { (r, 0), (-r - 3, 0), (0, r), (0, -r - 3) })
        {
            var o = new BlockPos(me.X + dx, me.Y, me.Z + dz);
            var (obs, corners, inside) = PortalFrames.Cells(o, alongX);
            var all = obs.Concat(corners).Concat(inside).ToList();
            if (all.Any(p => !SkillWorld.Air(ctx.World, p) || ctx.Protected(p))) continue;
            if (corners.Concat(obs).Where(p => p.Y == o.Y).Any(p => !SkillWorld.Solid(ctx.World, p.Down()))) continue;
            return o;
        }
        return null;
    }
}

[Skill]
public sealed class LightPortalSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "light_portal", Category = SkillCategory.Nether, Summary = "light a portal frame with flint and steel", Args = "pos?",
        Schema = SkillJson.Schema("pos:pos?"), Estimate = TimeSpan.FromSeconds(8),
        Pre = new() { Items = [[Items.FlintAndSteel]], Needs = "flint and steel" },
        Keywords = "light ignite activate portal flint steel fire nether",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (ctx.Find(Blocks.Portal, 12, 1).Count > 0 && args.Pos() is null) return Ok("portal already lit");
        var frame = args.Pos() is { } p ? (PortalFrames.IsFrame(ctx.World, p, true) ? (p, true) : PortalFrames.IsFrame(ctx.World, p, false) ? (p, false) : ((BlockPos, bool)?)null)
                                        : PortalFrames.FindFrame(ctx);
        if (frame is not { } f) return Fail("no complete obsidian frame nearby");
        var inside = f.Item1;
        // Flint and steel sets fire on the clicked face; fire inside a valid frame becomes portal blocks.
        await ctx.Game.PlaceAsync(inside, Items.FlintAndSteel, ct);
        return await ctx.WaitBlockAsync(inside, (id, _) => id == Blocks.Portal, ct, TimeSpan.FromSeconds(2))
            ? Ok($"portal lit at {P(inside)}") : Fail($"no portal formed at {P(inside)} (frame broken or fire went out)");
    }
}

[Skill]
public sealed class EnterPortalSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "enter_portal", Category = SkillCategory.Nether, Summary = "walk into nearest portal, record the pair", Args = "pos?",
        Schema = SkillJson.Schema("pos:pos?"), Safety = SafetyClass.Edge, Estimate = TimeSpan.FromSeconds(20),
        Pre = new() { SurvivalGate = true },
        Keywords = "enter go nether portal travel teleport dimension hell trip visit",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var portal = args.Pos() ?? ctx.Find(Blocks.Portal, 24, 1).Select(x => (BlockPos?)x.Pos).FirstOrDefault();
        if (portal is not { } pp || ctx.Block(pp) != Blocks.Portal) return Fail("no lit portal nearby");
        return await Travel(ctx, pp, ct);
    }

    internal static async Task<SkillResult> Travel(SkillContext ctx, BlockPos portal, CancellationToken ct)
    {
        int dim = ctx.World.Dimension;
        var g = await ctx.Game.GotoAsync(new GoalBlock(portal), null, ct);
        if (!g.Ok && ctx.World.Dimension == dim) return Fail($"couldn't step into the portal: {g.Message}");
        // b1.8.1: ~4 s standing in the portal, then 0x09 Respawn with the other dimension; then chunks + a 0x0D.
        if (!await ctx.Game.WaitUntilAsync(() => ctx.World.Dimension != dim, TimeSpan.FromSeconds(15), ct))
            return Fail("stood in the portal but nothing happened");
        await ctx.Game.WaitUntilAsync(() => ctx.World.IsLoaded(ctx.Me.X, ctx.Me.Z) && !ctx.World.IsPlayerOverVoid(ctx.Feet), TimeSpan.FromSeconds(15), ct);
        var arrived = ctx.Me;
        bool toNether = ctx.World.Dimension == -1;
        var pair = toNether ? new PortalPair(portal, arrived, DateTimeOffset.UtcNow) : new PortalPair(arrived, portal, DateTimeOffset.UtcNow);
        ctx.State.AddPortal(pair);
        string hazards = toNether ? "; " + NetherHazards.Scan(ctx) : "";
        return Ok($"now in the {(toNether ? "nether" : "overworld")} at {P(arrived)} (portal pair overworld {P(pair.Overworld)} / nether {P(pair.Nether)}){hazards}");
    }
}

[Skill]
public sealed class ReturnPortalSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "return_portal", Category = SkillCategory.Nether, Summary = "go back through the remembered portal", Args = "",
        Safety = SafetyClass.Edge, Estimate = TimeSpan.FromSeconds(60), Pre = new() { SurvivalGate = true },
        Keywords = "return back home leave exit nether portal overworld",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var pair = ctx.State.Nearest(ctx.Me, ctx.InNether);
        var mine = pair is null ? (BlockPos?)null : ctx.InNether ? pair.Nether : pair.Overworld;
        var portal = ctx.Find(Blocks.Portal, 32, 64).Select(f => f.Pos)
            .OrderBy(p => mine is { } m ? p.DistanceSquaredTo(m) : p.DistanceSquaredTo(ctx.Me)).Cast<BlockPos?>().FirstOrDefault();
        if (portal is null && mine is { } known)
        {
            var g = await ctx.Game.GotoAsync(new GoalNear(known, 3), null, ct);
            if (!g.Ok) return Fail($"can't get back to the portal at {P(known)}: {g.Message}");
            portal = ctx.Find(Blocks.Portal, 6, 1).Select(f => (BlockPos?)f.Pos).FirstOrDefault();
        }
        if (portal is not { } p) return Fail("no portal known or in sight" + (ctx.InNether ? $"; overworld spawn is ~{P(SkillState.Scale(ctx.World.Spawn, true))} here" : ""));
        return await EnterPortalSkill.Travel(ctx, p, ct);
    }
}

[Skill]
public sealed class NetherCheckSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "hazard_scan", Category = SkillCategory.Nether, Summary = "scan nether hazards: lava, ghasts, pigmen", Args = "",
        Estimate = TimeSpan.FromSeconds(1), Pre = new() { Dimension = Dim.Nether },
        Keywords = "nether safety hazards danger check scan ghast pigman lava",
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct) =>
        Task.FromResult(Ok(NetherHazards.Scan(ctx)));
}

/// <summary>Nether gathering on the client's mine macro (which already refuses lava-adjacent digs), with the nether
/// rules: pickaxe for glowstone/netherrack, never under gravel, refused while a ghast is in range.</summary>
public abstract class NetherGatherSkill : Skill
{
    protected abstract string Block { get; }
    protected virtual bool NeedsPickaxe => false;

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (NetherHazards.EdgeVeto(ctx) is { } veto) return Fail(veto);
        if (NeedsPickaxe && ctx.FirstOwned(Items.DiamondPickaxe, Items.IronPickaxe, Items.StonePickaxe, Items.WoodenPickaxe, Items.GoldenPickaxe) is null)
            return Fail($"{Block} needs a pickaxe to drop anything");
        int n = Clamp(args.Int("count"), 16, 1, 64);
        var r = await ctx.Game.MineAsync(Block, n, Clamp(args.Int("radius"), 16, 4, 32), ct);
        return new SkillResult(r.Ok, r.Message + "; " + NetherHazards.Scan(ctx));
    }
}

[Skill]
public sealed class GatherGlowstoneSkill : NetherGatherSkill
{
    protected override string Block => "glowstone";
    protected override bool NeedsPickaxe => true;
    public override SkillSpec Spec { get; } = new()
    {
        Name = "gather_glowstone", Category = SkillCategory.Nether, Summary = "mine glowstone (dust) safely", Args = "count?,radius?",
        Schema = SkillJson.Schema("count:int?,radius:int?"), Safety = SafetyClass.Edge, Estimate = TimeSpan.FromSeconds(60),
        Pre = new() { Dimension = Dim.Nether, Needs = "pickaxe", SurvivalGate = true },
        Keywords = "glowstone dust light nether ceiling gather mine",
    };
}

[Skill]
public sealed class GatherNetherrackSkill : NetherGatherSkill
{
    protected override string Block => "netherrack";
    protected override bool NeedsPickaxe => true;
    public override SkillSpec Spec { get; } = new()
    {
        Name = "gather_netherrack", Category = SkillCategory.Nether, Summary = "mine netherrack blocks", Args = "count?,radius?",
        Schema = SkillJson.Schema("count:int?,radius:int?"), Safety = SafetyClass.Edge, Estimate = TimeSpan.FromSeconds(40),
        Pre = new() { Dimension = Dim.Nether, Needs = "pickaxe", SurvivalGate = true },
        Keywords = "netherrack nether rack hellrock mine gather blocks",
    };
}

[Skill]
public sealed class GatherSoulSandSkill : NetherGatherSkill
{
    protected override string Block => "soul_sand";
    public override SkillSpec Spec { get; } = new()
    {
        Name = "gather_soul_sand", Category = SkillCategory.Nether, Summary = "dig soul sand", Args = "count?,radius?",
        Schema = SkillJson.Schema("count:int?,radius:int?"), Safety = SafetyClass.Edge, Estimate = TimeSpan.FromSeconds(40),
        Pre = new() { Dimension = Dim.Nether, SurvivalGate = true }, Keywords = "soul sand slow nether dig gather",
    };
}

[Skill]
public sealed class GatherGravelSkill : NetherGatherSkill
{
    protected override string Block => "gravel";
    public override SkillSpec Spec { get; } = new()
    {
        Name = "gather_gravel", Category = SkillCategory.Nether, Summary = "dig gravel (1/10 drops flint)", Args = "count?,radius?",
        Schema = SkillJson.Schema("count:int?,radius:int?"), Safety = SafetyClass.Edge, Estimate = TimeSpan.FromSeconds(40),
        Pre = new() { SurvivalGate = true }, Keywords = "gravel flint arrows flint and steel dig gather",
    };
}
