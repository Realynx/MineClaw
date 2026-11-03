using System.Text.Json;
using MineClaw.Client;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>Biome answers from <see cref="BiomeMemory"/>: every chunk this body has walked past, not only what is loaded now.</summary>
internal static class BiomeReport
{
    /// <summary>"north-east" for a step of (dx, dz) blocks (north = −z, east = +x).</summary>
    public static string Compass(double dx, double dz)
    {
        string[] names = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];
        double deg = Math.Atan2(dx, -dz) * 180 / Math.PI;
        return names[(int)Math.Round(((deg % 360) + 360) % 360 / 45) % 8];
    }

    /// <summary>The nearest patch with at least <paramref name="minChunks"/> chunks within <paramref name="max"/> blocks, as one line; null = none.</summary>
    public static (string Line, BiomePatch Patch)? Nearest(SkillContext ctx, string query, IReadOnlySet<int> ids, int minChunks, double max)
    {
        var me = ctx.Feet;
        var patches = ctx.World.BiomeMemory.Patches(ctx.World.Dimension, me.X, me.Z, ids).Where(p => p.Distance <= max).ToList();
        var best = patches.FirstOrDefault(p => p.Chunks >= minChunks);
        if (best is null) return null;
        int x = best.ChunkX * 16 + 8, z = best.ChunkZ * 16 + 8;
        string line = $"{query}: nearest {best.Distance:0} m {Compass(x - me.X, z - me.Z)} at {x},{z} (patch of {best.Chunks} chunks, ~{best.Columns} columns, " +
                      $"x {best.MinX}..{best.MaxX} z {best.MinZ}..{best.MaxZ}); {patches.Count - 1} other patch(es) seen";
        return (line, best);
    }
}

[Skill]
public sealed class FindBiomeSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "find_biome", Category = SkillCategory.Misc, Summary = "where a biome (desert, beach, mesa) was seen", Args = "biome?,max?,min_chunks?",
        Schema = SkillJson.Schema("biome:str?,max:int?,min_chunks:int?"), Estimate = TimeSpan.FromSeconds(1),
        Keywords = "biome desert beach mesa badlands locate where sand region explored seen survey",
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var mem = ctx.World.BiomeMemory;
        int dim = ctx.World.Dimension, seen = mem.Seen(dim);
        if (seen == 0) return Task.FromResult(Fail("no biome data yet: this world's chunks carry none (b1.8.1) or none have arrived"));
        string? q = args.Str("biome");
        if (string.IsNullOrWhiteSpace(q))
            return Task.FromResult(Ok($"seen {seen} chunks: " + string.Join(", ", mem.Survey(dim).Take(8).Select(s => $"{BiomeNames.Name(s.Biome)} {s.Chunks}"))));
        var ids = BiomeNames.Resolve(q);
        if (ids.Count == 0) return Task.FromResult(Fail($"unknown biome '{q}' (try desert, beach, mesa, plains, forest, ocean)"));
        int minChunks = Clamp(args.Int("min_chunks"), 1, 1, 10000);
        double max = Clamp(args.Int("max"), 100000, 1, 1000000);
        if (BiomeReport.Nearest(ctx, q, ids, minChunks, max) is { } hit) return Task.FromResult(Ok(hit.Line));
        return Task.FromResult(Fail($"no {q} seen within {max:0} m in {seen} explored chunks" + (minChunks > 1 ? $" (patch of {minChunks}+ chunks)" : "") +
                                    "; scout_biome walks out to look"));
    }
}

/// <summary>Walks out in a straight line (steering round what blocks it), checking the biome memory after every leg, until a patch of the
/// biome with enough chunks has been seen. A desert shows from the edge of the view distance, so legs are short of that.</summary>
[Skill]
public sealed class ScoutBiomeSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "scout_biome", Category = SkillCategory.Movement, Summary = "walk out until a biome (desert, beach) is seen", Args = "biome,dir?,max?,min_chunks?,leg?",
        Schema = SkillJson.Schema("biome:str,dir:int?,max:int?,min_chunks:int?,leg:int?,night_ok:bool?"), Estimate = TimeSpan.FromMinutes(5), Safety = SafetyClass.Edge,
        Pre = new() { SurvivalGate = true, DaylightOnly = true },
        Keywords = "scout explore walk look for biome desert beach mesa far sand source",
    };


    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        string q = args.Str("biome") ?? "";
        var ids = BiomeNames.Resolve(q);
        if (ids.Count == 0) return Fail($"unknown biome '{q}' (try desert, beach, mesa)");
        int minChunks = Clamp(args.Int("min_chunks"), 4, 1, 10000), max = Clamp(args.Int("max"), 600, 8, 20000);
        var start = ctx.Feet;
        int leg = Clamp(args.Int("leg"), 48, 8, 96);   // short of the server's view distance, so the next chunks arrive before the walk ends
        if (BiomeReport.Nearest(ctx, q, ids, minChunks, double.MaxValue) is { } known && known.Patch.Distance <= 160)
            return Ok("already seen: " + known.Line);

        double heading = args.Int("dir") is { } d ? d : EmptiestHeading(ctx);   // degrees clockwise from north
        int stuck = 0, deflections = 0;
        double walked = 0;
        while (walked < max)
        {
            ct.ThrowIfCancellationRequested();
            var from = ctx.Feet;
            double rad = heading * Math.PI / 180;
            int tx = (int)Math.Floor(from.X + Math.Sin(rad) * leg), tz = (int)Math.Floor(from.Z - Math.Cos(rad) * leg);
            int ty = SurfaceY(ctx.World, tx, tz) ?? ctx.Me.Y;
            var g = await ctx.Game.GotoAsync(new GoalNear(tx, ty, tz, 6), GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(60) }, ct);
            double moved = Math.Sqrt(Math.Pow(ctx.Feet.X - from.X, 2) + Math.Pow(ctx.Feet.Z - from.Z, 2));
            walked = Math.Sqrt(Math.Pow(ctx.Feet.X - start.X, 2) + Math.Pow(ctx.Feet.Z - start.Z, 2));
            if (BiomeReport.Nearest(ctx, q, ids, minChunks, double.MaxValue) is { } hit)
                return Ok($"found after {walked:0} m: {hit.Line}");
            if (!g.Ok || moved < 8)
            {
                if (++stuck >= 2)
                {
                    // Water, a cliff or a wall in the way: turn 40 degrees, alternating sides, a few times before giving up.
                    if (++deflections > 5) return Fail($"no {q} within {walked:0} m: stuck {walked:0} m out ({g.Message})");
                    heading += deflections % 2 == 0 ? 40 * deflections : -40 * deflections;
                    stuck = 0;
                }
            }
            else stuck = 0;
        }
        return Fail($"no {q} patch of {minChunks}+ chunks within {max} m (walked {walked:0} m, heading {heading % 360:0} deg); try another dir");
    }

    /// <summary>The compass heading (0 north, 90 east) whose rays out to 400 m cross the fewest chunks already seen.</summary>
    private static double EmptiestHeading(SkillContext ctx)
    {
        var mem = ctx.World.BiomeMemory;
        double best = 0, bestKnown = double.MaxValue;
        for (int deg = 0; deg < 360; deg += 45)
        {
            double rad = deg * Math.PI / 180;
            int known = 0;
            for (int r = 64; r <= 400; r += 32)
                if (mem.Knows(ctx.World.Dimension, (int)Math.Floor((ctx.Feet.X + Math.Sin(rad) * r) / 16), (int)Math.Floor((ctx.Feet.Z - Math.Cos(rad) * r) / 16))) known++;
            if (known < bestKnown) { bestKnown = known; best = deg; }
        }
        return best;
    }

    private static int? SurfaceY(WorldState w, int x, int z)
    {
        if (!w.IsLoaded(x, z)) return null;
        for (int y = w.WorldHeight - 1; y > 0; y--)
            if (BlockShapes.HasCollision(w, x, y, z) || BlockShapes.IsLiquid(w.GetBlock(x, y, z))) return y + 1;
        return null;
    }
}
