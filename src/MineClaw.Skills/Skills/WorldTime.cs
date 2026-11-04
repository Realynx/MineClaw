using System.Text.Json;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>
/// b1.8.1 beds: right-click at night (sleeping allowed from tick 12541 to 23458; we use the world's night window),
/// within reach, with no hostile mob within 8 blocks, never in the nether (beds explode there). The server marks the
/// bed head occupied (meta 0x4) while someone sleeps; the night only skips when every player sleeps.
/// </summary>
[Skill]
public sealed class SleepSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "sleep_in_bed", Category = SkillCategory.Survival, Summary = "sleep in nearby bed if night and safe", Args = "pos?",
        Schema = SkillJson.Schema("pos:pos?"), Estimate = TimeSpan.FromSeconds(10), Pre = new() { Dimension = Dim.Overworld },
        Keywords = "sleep bed night rest skip night spawn point nap",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (CantSleepNow(ctx) is { } now) return Fail(now);
        var bed = args.Pos() ?? ctx.Find(Blocks.Bed, 16, 1).Select(f => (BlockPos?)f.Pos).FirstOrDefault();
        if (bed is not { } b || ctx.Block(b) != Blocks.Bed) return Fail("no bed nearby");
        b = HeadHalf(ctx, b);
        if (BedProblem(ctx, b) is { } problem) return Fail(problem);
        var reach = await ctx.ReachAsync(b, ct, 2.5);
        if (!reach.Ok) return Fail($"can't reach bed: {reach.Message}");
        var r = await ctx.Game.ActivateAsync(b, ct);
        if (!r.Ok) return SkillResult.From(r);
        return await ctx.WaitBlockAsync(b, (id, m) => id == Blocks.Bed && (m & 0x4) != 0, ct, TimeSpan.FromSeconds(2))
            ? Ok($"sleeping in bed at {P(b)} (spawn set; night skips when everyone sleeps)")
            : Fail("the server didn't let me sleep (monsters nearby or not night yet)");
    }

    /// <summary>Beds explode outside the overworld; sleeping only works at night.</summary>
    private static string? CantSleepNow(SkillContext ctx)
    {
        if (ctx.World.Dimension != 0) return ctx.InNether ? "beds explode in the nether" : "beds explode outside the overworld";
        long t = ctx.World.TimeOfDay;
        if (t is < 12541 or > 23458) return $"it's day ({TimeText(t)}); can only sleep at night";
        return null;
    }

    /// <summary>The head half (meta 0x8) of the bed at <paramref name="b"/>: that's where the occupied flag lives.</summary>
    private static BlockPos HeadHalf(SkillContext ctx, BlockPos b)
    {
        if ((ctx.Meta(b) & 0x8) != 0) return b;
        var head = SkillWorld.Horizontal.Select(o => b + o).FirstOrDefault(p => ctx.Block(p) == Blocks.Bed && (ctx.Meta(p) & 0x8) != 0);
        return head != default ? head : b;
    }

    private static string? BedProblem(SkillContext ctx, BlockPos b)
    {
        if ((ctx.Meta(b) & 0x4) != 0) return $"bed at {P(b)} is occupied";
        if (ctx.World.Entities.NearestHostile(b.Center, 8) is { } mob) return $"{mob.DisplayName} within 8 of the bed; can't sleep";
        return null;
    }

    public static string TimeText(long t)
    {
        long h = (t / 1000 + 6) % 24, m = t % 1000 * 60 / 1000;
        return $"{h:00}:{m:00}";
    }
}

[Skill]
public sealed class TimeWeatherSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "time_weather", Category = SkillCategory.Survival, Summary = "day/night, time to dusk/dawn, rain", Args = "",
        Estimate = TimeSpan.FromSeconds(1), Keywords = "time weather day night rain storm clock dusk dawn when dark",
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        long t = ctx.World.TimeOfDay;
        bool night = ctx.World.IsNight;
        long until = night ? (23000 - t + 24000) % 24000 : (13000 - t + 24000) % 24000;
        string dim = ctx.InNether ? "nether (no day/night, no rain)" : $"{(night ? "night" : "day")} {SleepSkill.TimeText(t)}, {(night ? "dawn" : "dusk")} in {until / 1200.0:0.#} min" + (ctx.Game.IsRaining ? ", raining" : "");
        return Task.FromResult(Ok(dim + (night && !ctx.InNether ? "; hostiles spawn at light<8" : "")));
    }
}

/// <summary>Compass (points to world spawn) and clock readings: works whether or not I hold one.</summary>
[Skill]
public sealed class NavigateInstrumentsSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "read_compass", Category = SkillCategory.Misc, Summary = "direction+distance to spawn; clock time", Args = "",
        Estimate = TimeSpan.FromSeconds(1), Keywords = "compass clock spawn direction home where north map",
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var s = ctx.World.Spawn;
        double dx = s.X + 0.5 - ctx.Feet.X, dz = s.Z + 0.5 - ctx.Feet.Z;
        string dir = Math.Abs(dx) > Math.Abs(dz) ? (dx > 0 ? "east" : "west") : (dz > 0 ? "south" : "north");
        return Task.FromResult(Ok($"spawn {P(s)} is {Math.Sqrt(dx * dx + dz * dz):0} blocks {dir}; time {SleepSkill.TimeText(ctx.World.TimeOfDay)}"
                                  + (ctx.InNether ? " (compass spins in the nether)" : "")));
    }
}
