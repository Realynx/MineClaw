using System.Text.Json;
using MineClaw.Client;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Skills;

/// <summary>
/// Opening and closing doors, gates and trapdoors. Open = the doorway is passable (<see cref="Openables.LooksOpen"/>:
/// b1.8.1 door pairs keep the open bit set on the shut mirrored half), and every right-click is verified by waiting for
/// the server's block change (0x35): unchanged metadata means "locked" (protection plugin) or "iron door" (redstone
/// only), and the caller re-plans instead of walking into it. Iron doors use a learned mechanism link first, then a
/// button/lever/plate next to them; unknown triggers near TNT/dispensers are never touched.
/// </summary>
public static class DoorOps
{
    public static bool IsDoorish(int id) => Openables.IsOpenable(id);

    /// <summary>Nearest openable (optionally of one kind) within <paramref name="radius"/>.</summary>
    public static BlockPos? Nearest(SkillContext ctx, string? kind, double radius = 8)
    {
        int? only = kind?.ToLowerInvariant() switch
        {
            "door" => Openables.WoodDoor, "iron_door" or "iron door" => Openables.IronDoor,
            "gate" or "fence_gate" => Openables.FenceGate, "trapdoor" or "hatch" => Openables.Trapdoor, _ => null,
        };
        var hit = ctx.Find((id, meta) => Openables.IsOpenable(id) && (only is null || id == only) && !Openables.IsUpperHalf(id, meta), radius, 1);
        return hit.Count > 0 ? hit[0].Pos : null;
    }

    /// <summary>A learned trigger of the door (the nearest to the bot), else a lever/button within 2 blocks of it (the one
    /// nearest the bot) or a plate next to it — never one with TNT/dispensers near.</summary>
    public static BlockPos? FindActuator(SkillContext ctx, BlockPos door)
    {
        BlockPos? best = null;
        double bestD = double.MaxValue;
        var eye = ctx.World.Player.EyePosition;
        foreach (var l in MechanismOps.Openers(ctx, door).Where(l => !l.Dangerous && l.TriggerPresent(ctx.World)))
        {
            double d = l.Trigger.Center.DistanceTo(eye);
            if (d < bestD) { bestD = d; best = l.Trigger; }
        }
        if (best is not null) return best;
        for (int dx = -2; dx <= 2; dx++)
        for (int dz = -2; dz <= 2; dz++)
        for (int dy = -1; dy <= 2; dy++)
        {
            var p = door.Offset(dx, dy, dz);
            int id = ctx.Block(p);
            if (!Openables.IsActuator(id) || Mechanisms.ExplosivesNear(ctx.World, p)) continue;
            double d = p.Center.DistanceTo(eye) + (Openables.IsPlate(id) ? 0.5 : 0);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    /// <summary>
    /// Brings the openable at <paramref name="pos"/> to <paramref name="open"/> (null = toggle). Idempotent: already in
    /// the wanted state = success. Iron doors use a nearby button/lever/plate; without one they're reported as obstacles.
    /// </summary>
    public static async Task<SkillResult> SetAsync(SkillContext ctx, BlockPos pos, bool? open, CancellationToken ct)
    {
        var s = Openables.StateBlock(ctx.World, pos);
        int id = ctx.Block(s);
        if (!Openables.IsOpenable(id)) return SkillResult.Fail($"no door/gate/trapdoor at {SkillJson.Fmt(pos)} ({ctx.BlockName(pos)})");
        string what = $"{Openables.Name(id)} at {SkillJson.Fmt(s)}";
        // No protection check: a protected build's door is still a door (protection forbids breaking it, not using it).
        bool now = Openables.LooksOpen(ctx.World, s);
        bool want = open ?? !now;
        int before = ctx.Meta(s);
        if (now == want) return SkillResult.Success($"{what} already {(want ? "open" : "closed")}");

        // Never pull the floor out from under myself (or open a hatch over the void someone might be standing on).
        if (id == Openables.Trapdoor && want && StandingOn(ctx, s))
            return SkillResult.Fail($"won't open the {what}: I'm standing on it");

        if (id == Openables.IronDoor) return await IronAsync(ctx, s, want, what, ct).ConfigureAwait(false);

        var reach = await ctx.ReachAsync(s, ct).ConfigureAwait(false);
        if (!reach.Ok) return SkillResult.Fail($"can't reach {what}: {reach.Message}");
        var r = await ctx.Game.ActivateAsync(s, ct).ConfigureAwait(false);
        if (!r.Ok) return SkillResult.Fail($"couldn't use {what}: {r.Message}");
        if (!await ctx.WaitBlockAsync(s, (bid, meta) => bid != id || ((meta ^ before) & Openables.OpenBit) != 0, ct).ConfigureAwait(false))
            return SkillResult.Fail($"{what} didn't {(want ? "open" : "close")}: no block change from the server (locked or claimed?)");
        if (ctx.Block(s) != id) return SkillResult.Fail($"{what} is gone");
        return SkillResult.Success($"{what} {(want ? "opened" : "closed")}");
    }

    private static async Task<SkillResult> IronAsync(SkillContext ctx, BlockPos s, bool want, string what, CancellationToken ct)
    {
        if (FindActuator(ctx, s) is not { } act)
            return SkillResult.Fail($"{what} only opens with redstone and I don't know its button/lever/plate; " + MechanismOps.Ask(ctx, s, "iron door"));
        int aid = ctx.Block(act);
        int before = ctx.Meta(s);
        if (Openables.IsPlate(aid))
        {
            if (!want) return SkillResult.Success($"{what} closes when nobody stands on the plate at {SkillJson.Fmt(act)}");
            var g = await ctx.Game.GotoAsync(new GoalBlock(act), null, ct).ConfigureAwait(false);
            if (!g.Ok) return SkillResult.Fail($"can't reach the plate at {SkillJson.Fmt(act)}: {g.Message}");
        }
        else
        {
            if (aid == Openables.StoneButton && !want) return SkillResult.Success($"{what} closes by itself (button)");
            var reach = await ctx.ReachAsync(act, ct).ConfigureAwait(false);
            if (!reach.Ok) return SkillResult.Fail($"can't reach the {Openables.Name(aid)} for {what}: {reach.Message}");
            var r = await ctx.Game.ActivateAsync(act, ct).ConfigureAwait(false);
            if (!r.Ok) return SkillResult.Fail($"couldn't press the {Openables.Name(aid)}: {r.Message}");
        }
        if (!await ctx.WaitBlockAsync(s, (_, meta) => ((meta ^ before) & Openables.OpenBit) != 0, ct).ConfigureAwait(false))
            return SkillResult.Fail($"{what} didn't react to the {Openables.Name(aid)} at {SkillJson.Fmt(act)} (not wired to it?)");
        return SkillResult.Success($"{what} {(want ? "opened" : "closed")} via {Openables.Name(aid)}" + (aid == Openables.StoneButton ? " (shuts in ~1s)" : ""));
    }

    private static bool StandingOn(SkillContext ctx, BlockPos p)
    {
        var feet = ctx.Feet;
        return Safety.SupportBlocks(ctx.World, feet).Contains(p)
               || (Math.Floor(feet.Y + 1e-6) == p.Y && Math.Abs(feet.X - (p.X + 0.5)) < 0.8 && Math.Abs(feet.Z - (p.Z + 0.5)) < 0.8);
    }
}

[Skill]
public sealed class OpenDoorSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "open_door", Category = SkillCategory.Doors, Summary = "open door/gate/trapdoor, verified", Args = "pos?,kind?",
        Schema = SkillJson.Schema("pos:pos?,kind:str?"), Estimate = TimeSpan.FromSeconds(3),
        Keywords = "door gate fence trapdoor hatch iron button lever let in entrance unlock",
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct) =>
        PosArg(args, () => DoorOps.Nearest(ctx, args.Str("kind"))) is { } p
            ? DoorOps.SetAsync(ctx, p, true, ct)
            : Task.FromResult(Fail("no door/gate/trapdoor nearby"));
}

[Skill]
public sealed class CloseDoorSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "close_door", Category = SkillCategory.Doors, Summary = "close door/gate/trapdoor, verified", Args = "pos?,kind?",
        Schema = SkillJson.Schema("pos:pos?,kind:str?"), Estimate = TimeSpan.FromSeconds(3),
        Keywords = "door gate fence trapdoor hatch shut keep mobs out",
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct) =>
        PosArg(args, () => DoorOps.Nearest(ctx, args.Str("kind"))) is { } p
            ? DoorOps.SetAsync(ctx, p, false, ct)
            : Task.FromResult(Fail("no door/gate/trapdoor nearby"));
}

[Skill]
public sealed class ToggleDoorSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "toggle_door", Category = SkillCategory.Doors, Summary = "flip a door/gate/trapdoor, verified", Args = "pos?,kind?",
        Schema = SkillJson.Schema("pos:pos?,kind:str?"), Estimate = TimeSpan.FromSeconds(3),
        Keywords = "door gate trapdoor hatch flip switch",
    };

    public override Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct) =>
        PosArg(args, () => DoorOps.Nearest(ctx, args.Str("kind"))) is { } p
            ? DoorOps.SetAsync(ctx, p, null, ct)
            : Task.FromResult(Fail("no door/gate/trapdoor nearby"));
}

[Skill]
public sealed class UseLeverSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "use_lever", Category = SkillCategory.Redstone, Summary = "set a lever on/off, verified", Args = "pos?,on?,confirm?",
        Schema = SkillJson.Schema("pos:pos?,on:bool?,confirm:bool?"), Estimate = TimeSpan.FromSeconds(3),
        Keywords = "lever switch redstone power toggle on off",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var p = args.Pos() ?? (ctx.Find(Openables.Lever, 8, 1) is [var f, ..] ? f.Pos : null);
        if (p is not { } pos || ctx.Block(pos) != Openables.Lever) return Fail("no lever there");
        if (MechanismOps.TriggerVeto(ctx, pos, args.Flag("confirm")) is { } veto) return Fail(veto);
        // A lever in someone's build with an unknown effect: only once we've seen what it does (or were told to).
        if (ctx.Protected(pos) && MechanismOps.View(ctx).ForTrigger(pos) is null && !args.Flag("confirm"))
            return Fail($"lever at {P(pos)} is part of a build and I don't know what it does (confirm to flip it)");
        bool now = Openables.IsActuatorOn(Openables.Lever, ctx.Meta(pos));
        bool want = args.Bool("on") ?? !now;
        if (now == want) return Ok($"lever at {P(pos)} already {(want ? "on" : "off")}");
        var reach = await ctx.ReachAsync(pos, ct);
        if (!reach.Ok) return Fail($"can't reach lever: {reach.Message}");
        var r = await ctx.Game.ActivateAsync(pos, ct);
        if (!r.Ok) return SkillResult.From(r);
        return await ctx.WaitBlockAsync(pos, (_, m) => Openables.IsActuatorOn(Openables.Lever, m) == want, ct)
            ? Ok($"lever at {P(pos)} {(want ? "on" : "off")}")
            : Fail($"lever at {P(pos)} didn't move (protected?)");
    }
}

[Skill]
public sealed class PressButtonSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "press_button", Category = SkillCategory.Redstone, Summary = "press a stone button, verified", Args = "pos?,confirm?",
        Schema = SkillJson.Schema("pos:pos?,confirm:bool?"), Estimate = TimeSpan.FromSeconds(3),
        Keywords = "button press push redstone iron door",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        var p = args.Pos() ?? (ctx.Find(Openables.StoneButton, 8, 1) is [var f, ..] ? f.Pos : null);
        if (p is not { } pos || ctx.Block(pos) != Openables.StoneButton) return Fail("no button there");
        if (MechanismOps.TriggerVeto(ctx, pos, args.Flag("confirm")) is { } veto) return Fail(veto);
        var reach = await ctx.ReachAsync(pos, ct);
        if (!reach.Ok) return Fail($"can't reach button: {reach.Message}");
        if (Openables.IsActuatorOn(Openables.StoneButton, ctx.Meta(pos))) return Ok($"button at {P(pos)} already pressed");
        var r = await ctx.Game.ActivateAsync(pos, ct);
        if (!r.Ok) return SkillResult.From(r);
        return await ctx.WaitBlockAsync(pos, (_, m) => Openables.IsActuatorOn(Openables.StoneButton, m), ct)
            ? Ok($"pressed button at {P(pos)}")
            : Fail($"button at {P(pos)} didn't go down (protected?)");
    }
}

/// <summary>
/// Goto that walks through doors: plans with <see cref="PathOptions.AllowOpenDoors"/>, and at each door step stops on
/// the near side, opens (verified), steps through, and puts the door back the way it found it. Uses only public client
/// calls, so it works before the client's path follower learns <see cref="FollowerActionKind.Activate"/>.
/// </summary>
[Skill]
public sealed class GotoViaDoorsSkill : Skill
{
    public override SkillSpec Spec { get; } = new()
    {
        Name = "goto_via_doors", Category = SkillCategory.Movement, Summary = "walk to pos opening+reclosing doors/gates", Args = "pos,leave_open?,one_way?",
        Schema = SkillJson.Schema("pos:pos,leave_open:bool?,one_way:bool?"), Estimate = TimeSpan.FromSeconds(30),
        Keywords = "walk go through door doors gate inside house room enter building pass",
    };

    public override async Task<SkillResult> ExecuteAsync(SkillContext ctx, JsonElement args, CancellationToken ct)
    {
        if (args.Pos() is not { } target) return Fail("pos required");
        bool restore = ctx.Options.RestoreDoors && !args.Flag("leave_open");
        var goal = new GoalBlock(target);
        int doors = 0, restored = 0;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var plan = Pathfinder.FindPath(ctx.World, ctx.Feet, goal, PathOptions.Default with
            {
                AllowOpenDoors = true, Mechanisms = MechanismOps.View(ctx), AllowOneWay = args.Flag("one_way"),
            }, ct);
            if (plan.Status is PathStatus.NoPath or PathStatus.InvalidStart)
            {
                // Blocked by an iron door / piston wall nobody told me about: ask gently, default = go around.
                string why = $"no path to {P(target)} ({plan.Reason ?? plan.Status.ToString()})";
                return MechanismOps.Unknowns(ctx, 12).FirstOrDefault() is { What: { } w } u ? Fail(why + "; " + MechanismOps.Ask(ctx, u.Pos, w)) : Fail(why);
            }
            int idx = -1;
            for (int i = 0; i < plan.Steps.Count; i++) if (plan.Steps[i].Opens.Count > 0) { idx = i; break; }
            // Mechanism (Trigger) steps are timing-critical: the client's goto runs them (it plans with the same graph).
            if (idx < 0 || plan.Steps.Take(idx).Any(st => st.Trigger is not null))
            {
                var g = await ctx.Game.GotoAsync(goal, null, ct);
                return g.Ok ? Ok($"at {P(target)}" + (doors > 0 ? $" through {doors} door(s){(restore ? $", {restored} re-closed" : "")}" : "")) : SkillResult.From(g);
            }
            var step = plan.Steps[idx];
            var before = idx > 0 ? plan.Steps[idx - 1].Pos : ctx.Me;
            if (before != ctx.Me)
            {
                var g = await ctx.Game.GotoAsync(new GoalBlock(before), null, ct);
                if (!g.Ok) return Fail($"couldn't reach the door at {P(step.Opens[0].Pos)}: {g.Message}");
            }
            foreach (var tg in step.Opens)
            {
                var r = await DoorOps.SetAsync(ctx, tg.Pos, !Openables.IsOpen(tg.FromMeta), ct);
                if (!r.Ok) return Fail($"stuck at {Openables.Name(tg.Id)}: {r.Text}");
            }
            doors++;
            // Step through (and one more cell when there is one, so the doorway is clear for closing).
            var through = idx + 1 < plan.Steps.Count ? plan.Steps[idx + 1].Pos : step.Pos;
            var pass = await ctx.Game.GotoAsync(new GoalBlock(through), null, ct);
            if (!pass.Ok) return Fail($"opened {Openables.Name(step.Opens[0].Id)} but couldn't pass: {pass.Message}");
            if (restore)
                foreach (var tg in step.Opens.Where(t => t.CanRestore(ctx.World) && !t.Restored(ctx.World)))
                {
                    var r = await DoorOps.SetAsync(ctx, tg.Pos, Openables.IsOpen(tg.FromMeta), ct);
                    if (r.Ok) restored++;
                }
        }
        return Fail($"too many doors on the way to {P(target)}; now at {P(ctx.Me)}");
    }
}
