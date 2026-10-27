using System.Text.Json;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>
/// Mechanism knowledge for skills: the learned graph (<see cref="WorldState.Mechanisms"/>, filled by the Bot's watcher
/// from observed activations, demonstrations and answers), unknown iron doors / piston walls nearby, and the safety rules
/// for firing triggers: never an unknown button/lever/plate near TNT or dispensers, never a link known to be dangerous
/// unless explicitly confirmed.
/// </summary>
public static class MechanismOps
{
    public static MechanismView View(SkillContext ctx) => ctx.World.Mechanisms.View(ctx.World.Dimension);

    /// <summary>Why firing the trigger at <paramref name="p"/> is unsafe, or null.</summary>
    public static string? TriggerVeto(SkillContext ctx, BlockPos p, bool confirmed)
    {
        var link = View(ctx).ForTrigger(p);
        if (link is { Dangerous: true } && !confirmed) return $"{SkillJson.Fmt(p)} is linked to TNT/a dispenser (say confirm to use it anyway)";
        if (link is null && Mechanisms.ExplosivesNear(ctx.World, p))
            return $"won't touch an unknown trigger at {SkillJson.Fmt(p)}: TNT/dispenser nearby";
        return null;
    }

    /// <summary>Known links whose effects include the door/wall block at <paramref name="p"/> (door halves count).</summary>
    public static IReadOnlyList<MechanismLink> Openers(SkillContext ctx, BlockPos p) => View(ctx).Affecting(Openables.StateBlock(ctx.World, p));

    /// <summary>Iron doors without a known opener or adjacent button/lever/plate, and retracted/extended pistons not part
    /// of any known link ("suspicious walls"), nearest first.</summary>
    public static List<(BlockPos Pos, string What)> Unknowns(SkillContext ctx, double radius = 16)
    {
        var view = View(ctx);
        var list = new List<(BlockPos, string)>();
        foreach (var f in ctx.Find((id, meta) => id == Openables.IronDoor && !Openables.IsUpperHalf(id, meta) || Mechanisms.IsPistonBase(id), radius, 32))
        {
            int id = ctx.Block(f.Pos);
            if (id == Openables.IronDoor)
            {
                if (view.Affecting(f.Pos).Count > 0 || DoorOps.FindActuator(ctx, f.Pos) is not null) continue;
                list.Add((f.Pos, "iron door"));
            }
            else if (view.Affecting(f.Pos).Count == 0 && list.All(x => Math.Abs(x.Item1.X - f.Pos.X) + Math.Abs(x.Item1.Y - f.Pos.Y) + Math.Abs(x.Item1.Z - f.Pos.Z) > 4))
                list.Add((f.Pos, "piston wall"));
        }
        return list;
    }

    /// <summary>"needs_input: how do I open the iron door at x,y,z? (default: go around)" — and remembers the question so
    /// a player's answer naming a trigger teaches the link.</summary>
    public static string Ask(SkillContext ctx, BlockPos subject, string what)
    {
        ctx.World.Mechanisms.Question = (subject, ctx.World.Dimension, DateTimeOffset.UtcNow);
        return $"needs_input: ask a player \"how do I open the {what} at {SkillJson.Fmt(subject)}?\" (default: go around)";
    }
}

[Skill]
public sealed class MechanismsSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "mechanisms", Category = SkillCategory.Redstone, Summary = "known button/lever/plate links + unknown doors", Args = "radius?",
        Schema = SkillJson.Schema("radius:int?"), Estimate = TimeSpan.FromSeconds(1),
        Keywords = "redstone mechanism secret door piston wall iron door button lever plate what opens",
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        int r = Clamp(args.Int("radius"), 24, 4, 64);
        var me = ctx.Me;
        var known = MechanismOps.View(ctx).TriggersNear(me, r).OrderBy(l => l.Trigger.DistanceSquaredTo(me)).Take(4).Select(l => l.Describe()).ToList();
        var unknown = MechanismOps.Unknowns(ctx, r).Take(3).Select(u => $"{u.What}@{P(u.Pos)} (opener unknown)").ToList();
        if (known.Count == 0 && unknown.Count == 0) return Task.FromResult(Ok("no mechanisms known nearby"));
        return Task.FromResult(Ok(string.Join("; ", known.Concat(unknown))));
    }
}

[Skill]
public sealed class TeachMechanismSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "teach_mechanism", Category = SkillCategory.Redstone, Summary = "record: trigger pos opens target pos", Args = "trigger,target",
        Schema = SkillJson.Schema("trigger:pos,target:pos"), Estimate = TimeSpan.FromSeconds(1),
        Keywords = "this button opens that door lever plate secret wall teach learn remember mechanism",
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Pos("trigger") is not { } t || args.Pos("target") is not { } target) return Task.FromResult(Fail("trigger and target required"));
        if (Mechanisms.KindOf(ctx.Block(t)) is null) return Task.FromResult(Fail($"no button/lever/plate at {P(t)} ({ctx.BlockName(t)})"));
        if (ctx.Block(target) == 0) return Task.FromResult(Fail($"nothing at {P(target)}"));
        var link = ctx.World.Mechanisms.Teach(ctx.World, t, [target], ctx.World.Dimension, "told", DateTimeOffset.UtcNow);
        return Task.FromResult(Ok("noted " + link.Describe() + "; confirmed on first use"));
    }
}

[Skill]
public sealed class HowToOpenSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "how_to_open", Category = SkillCategory.Redstone, Summary = "what opens this door/wall (or ask)", Args = "pos?",
        Schema = SkillJson.Schema("pos:pos?"), Estimate = TimeSpan.FromSeconds(1),
        Keywords = "how open iron door secret wall piston door button lever plate stuck locked",
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var p = args.Pos() ?? MechanismOps.Unknowns(ctx, 12).Select(u => (BlockPos?)u.Pos).FirstOrDefault() ?? DoorOps.Nearest(ctx, "iron_door", 12);
        if (p is not { } pos) return Task.FromResult(Ok("nothing unknown nearby"));
        var openers = MechanismOps.Openers(ctx, pos);
        if (openers.Count > 0) return Task.FromResult(Ok(string.Join("; ", openers.Take(3).Select(l => l.Describe()))));
        if (ctx.Block(pos) == Openables.IronDoor && DoorOps.FindActuator(ctx, pos) is { } a)
            return Task.FromResult(Ok($"probably the {Openables.Name(ctx.Block(a))} at {P(a)} (not seen it work yet)"));
        string what = ctx.Block(pos) == Openables.IronDoor ? "iron door" : "wall";
        return Task.FromResult(Fail(MechanismOps.Ask(ctx, pos, what)));
    }
}
