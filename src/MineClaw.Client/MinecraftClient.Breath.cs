using MineClaw.Client.Combat;
using MineClaw.Client.Survival;
using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Breath, the drowning reflex and the damage cause (both versions; zero tokens). Live 10-29 01:58-01:59 (1.12.2, the first
/// session): main's chop job ended on the gravel bed of a ravine lake (126,52,295, head under), the body stood there with nothing
/// pressing jump, 2 damage a second from 20 to 0, and the only line was "low health 8/20 …: work goes on". Now:
/// <list type="bullet">
/// <item>The air supply is tracked every tick (<see cref="Breath.Tick"/> from the eye's cell; our own metadata index 1 corrects
/// it) and shown in the status ("air 4/10 bubbles").</item>
/// <item>An idle or job-driven body with its head under water swims up like a player holding jump, unless a deliberate dive
/// (the bucket column) still has air to spare.</item>
/// <item>Low air (≤ <see cref="SurvivalOptions.SurfaceAtAir"/>) or a drowning hit: the reflex stops the job, swims to the nearest
/// breathable cell (straight up when the surface is open, else to an air pocket or the edge), never deeper, and new jobs wait
/// until the air is full again; then one line: "surfaced at …: air was 3/10 bubbles".</item>
/// <item>Every damage event names its likely cause (drowning, lava, fire, suffocation, cactus, a fall, a mob, starvation,
/// poison/wither), so the reflexes and the brain act on the cause, not on "took 2 damage".</item>
/// </list>
/// </summary>
public sealed partial class MinecraftClient
{
    private long _airServerTick = long.MinValue;
    private int _drownHits;
    private long _lastDrownHitTick = -1_000_000;
    /// <summary>The surfacing episode: started at (tick, air then); null when breathing normally.</summary>
    private (long Tick, int Air, BlockPos? Target)? _surfacing;
    /// <summary>The surfacing route (cells, nearest first), searched again every 10 ticks while under.</summary>
    private IReadOnlyList<BlockPos>? _airPath;
    private int _airSteps = -1;
    private long _airSearchTick = -1_000_000;
    private long _breathingSince = long.MinValue;
    private long _lastLandTick = -1_000_000;
    private double _lastLandFall;
    private long _lastKnockbackTick = -1_000_000;
    private long _lastBurnTick = -1_000_000;

    /// <summary>The body is surfacing (the drowning reflex owns it).</summary>
    public bool Surfacing => _surfacing is not null;

    /// <summary>"air 4/10 bubbles (6.2s left)" while the head is under water or the air is short; null when breathing.</summary>
    public string? AirStatus => World.Player.HeadUnderWater || World.Player.Air < Breath.MaxAir ? Breath.Text(World.Player.Air) : null;

    /// <summary>Our own metadata index 1 (both versions send it when it changes).</summary>
    /// <summary>Our own flags metadata: on fire (0x01).</summary>
    private void OnSelfFlags(int flags)
    {
        if ((flags & EntityMeta.FlagBurning) != 0) _lastBurnTick = TickCount;
    }

    private void OnSelfAir(int air)
    {
        World.Player.Air = air;
        _airServerTick = TickCount;
    }

    /// <summary>After the tick's physics: the eye's cell, the air estimate, landings (for the fall cause).</summary>
    private void BreathTick(PhysicsState phys, double fallBefore)
    {
        var p = World.Player;
        bool under = Breath.EyeInWater(World, phys.EyePosition);
        p.HeadUnderWater = under;
        if (_airServerTick != TickCount)
        {
            bool immune = p.GameMode == 1 || p.HasEffect(PotionEffects.WaterBreathing);
            var (air, hit) = Breath.Tick(p.Air, under, immune);
            p.Air = air;
            if (hit) { _lastDrownHitTick = TickCount; _drownHits++; }
        }
        if (phys.OnGround && fallBefore > 0 && phys.FallDistance == 0) { _lastLandFall = fallBefore; _lastLandTick = TickCount; }
        if (!under) { if (_breathingSince == long.MinValue) _breathingSince = TickCount; }
        else _breathingSince = long.MinValue;
        SurfacingTick(phys, under);
    }

    private void NoteKnockbackForCause() => _lastKnockbackTick = TickCount;

    /// <summary>Starts / ends the surfacing episode (tick thread).</summary>
    private void SurfacingTick(PhysicsState phys, bool under)
    {
        var o = Options.Survival;
        if (!o.Enabled) return;
        var p = World.Player;
        if (_surfacing is { } ep)
        {
            EndSurfacingWhenBreathing(phys, under, ep.Air);
            return;
        }
        if (!under) { if (p.Air >= Breath.MaxAir) _drownHits = 0; _airPath = null; _airSteps = -1; return; }
        var feet = FeetCell();
        RefreshAirSearch(feet);
        // Surface once the air left only just covers the swim to air (far from the hole under a ceiling, that is long before
        // the last 5 s), at the fixed low line, or after a drowning hit. A deliberate dive keeps its depth until then.
        if (!DrowningRisk(p.Air, o.SurfaceAtAir)) return;
        StartSurfacing(phys, feet, p.Air);
    }

    /// <summary>Breathing again for half a second with the air back: the episode is over (one line, "surfaced at …").</summary>
    private void EndSurfacingWhenBreathing(PhysicsState phys, bool under, int airThen)
    {
        if (under || World.Player.Air < Breath.MaxAir || _breathingSince == long.MinValue || TickCount - _breathingSince < 10) return;
        _surfacing = null;
        var at = phys.Position;
        string msg = $"surfaced at {(int)Math.Floor(at.X)},{(int)Math.Floor(at.Y)},{(int)Math.Floor(at.Z)}: air was {Breath.Bubbles(airThen)}/10 bubbles"
                     + (_drownHits > 0 ? $", {_drownHits * Breath.DrownDamage} drowning damage taken" : "");
        _drownHits = 0;
        Log("survival: " + msg);
        RaiseDanger(DangerKind.Recovered, msg, abort: false);
    }

    /// <summary>Where is the nearest air, and how long is the swim? (Every 10 ticks while under: the body moves.)</summary>
    private void RefreshAirSearch(BlockPos feet)
    {
        if (TickCount - _airSearchTick < 10) return;
        _airSearchTick = TickCount;
        var found = Breath.NearestBreathable(World, feet);
        _airPath = found?.Path;
        _airSteps = found?.Steps ?? -1;
    }

    private bool DrowningRisk(int air, int surfaceAtAir) =>
        air <= surfaceAtAir || TickCount - _lastDrownHitTick < 40 || _airSteps > 0 && air <= Breath.AirNeeded(_airSteps);

    private void StartSurfacing(PhysicsState phys, BlockPos feet, int air)
    {
        BlockPos? target = _airPath is { Count: > 0 } ap ? ap[^1] : null;
        _surfacing = (TickCount, air, target);
        string where = target is { } t
            ? t.X == feet.X && t.Z == feet.Z ? "straight up" : $"to {Fmt(t)}"
            : "up (no breathable cell found nearby)";
        string why = $"drowning risk: head under water at {Fmt(phys.Position)}, {Breath.Text(air)}: stopped, surfacing {where}";
        Log("survival: " + why);
        RaiseDanger(DangerKind.Drowning, why, abort: true, critical: true);
    }

    /// <summary>
    /// The breath layer of the input (after combat, before the edge guard): surfacing drives the body up/over to air; otherwise a
    /// head under water holds jump (vanilla's swim-up: motionY + 0.04 a tick in water) unless a driver dives on purpose with air
    /// to spare, or a fight owns the body with air to spare.
    /// </summary>
    private MoveInput BreathOverride(PhysicsState phys, MoveInput input, bool driven, bool fighting)
    {
        var p = World.Player;
        if (!Options.Survival.Enabled) return input;
        if (_surfacing is { } ep) return SurfacingInput(phys, input, ep.Target);
        // Floating idle (in water, off the ground): hold jump like a player, so the head stays above the surface instead of
        // bobbing under every few ticks.
        if (!p.HeadUnderWater) return FloatingInput(phys, input, driven, fighting);
        bool spare = p.Air > Options.Survival.SurfaceAtAir + 60;
        if (InWaterHold(phys) && spare) return input;    // the bucket column: the hover holds the depth
        if ((driven || fighting) && spare) return input; // a planned swim step / a fight may dip; the planner caps the time
        return input.Jump ? input : input with { Jump = true };
    }

    private MoveInput FloatingInput(PhysicsState phys, MoveInput input, bool driven, bool fighting) =>
        !driven && !fighting && phys.InWater && !phys.OnGround && !InWaterHold(phys) && !input.Jump ? input with { Jump = true } : input;

    /// <summary>Surfacing: follows the searched route cell by cell (the first cell past the one the body is in), else heads for the
    /// target; jump held all the way.</summary>
    private MoveInput SurfacingInput(PhysicsState phys, MoveInput input, BlockPos? target)
    {
        if (!phys.InWater && !World.Player.HeadUnderWater) return input with { Jump = input.Jump };
        var me = phys.Position;
        int mx = (int)Math.Floor(me.X), mz = (int)Math.Floor(me.Z);
        var next = NextAirStep(mx, mz);
        if (next is null && target is { } t && (t.X != mx || t.Z != mz)) next = t;
        if (next is not { } n) return new MoveInput(Jump: true, Yaw: input.Yaw, Pitch: input.Pitch);
        var c = new Vec3d(n.X + 0.5, me.Y, n.Z + 0.5);
        float yaw = PlayerPhysics.YawPitchTo(me, c).Yaw;
        double dx = c.X - me.X, dz = c.Z - me.Z;
        bool close = dx * dx + dz * dz < 0.15 * 0.15;
        return new MoveInput(Forward: close ? 0 : 1, Jump: true, Yaw: yaw, Pitch: -30);
    }

    /// <summary>The route cell after the column the body is in (the last time the route passes it), or null.</summary>
    private BlockPos? NextAirStep(int mx, int mz)
    {
        if (_airPath is not { Count: > 0 } path) return null;
        int at = -1;
        for (int i = path.Count - 1; i >= 0; i--)
            if (path[i].X == mx && path[i].Z == mz) { at = i; break; }
        for (int i = at + 1; i < path.Count; i++)
            if (path[i].X != mx || path[i].Z != mz) return path[i];
        return null;
    }

    /// <summary>New jobs wait while the body is surfacing (resume only with full air), up to <paramref name="max"/>.</summary>
    private async Task WaitForAirAsync(TimeSpan max, CancellationToken ct)
    {
        if (_surfacing is null) return;
        await WaitUntilAsync(() => _surfacing is null, max, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ damage cause

    /// <summary>
    /// The likely cause of a health drop of <paramref name="dmg"/> (packet thread), from what the body knows right now: drowning
    /// (head under, air out, 2 a hit), lava / fire, suffocation (the eye inside an opaque block), cactus (touching one), a fall
    /// (landed within the last half second after more than 3 blocks), a mob (knocked back just now, or a hostile within reach),
    /// starvation (food 0, 1 a hit), poison / wither; else null.
    /// </summary>
    public string? DamageCause(int dmg)
    {
        try
        {
            return DamageCauseCore(dmg);
        }
        catch (Exception) { return null; }
    }

    private string? DamageCauseCore(int dmg)
    {
        var p = World.Player;
        var phys = p.Physics;
        if (DrowningHit(p, dmg))
            return $"drowning (head under water, {Breath.Text(p.Air)})";
        var (lava, fire, cactus) = TouchingHazards(phys.BoundingBox);
        if (lava) return "lava (standing in it)";
        if (fire) return "fire (standing in it)";
        if (TickCount - _lastBurnTick <= 40) return "burning (on fire)";
        if (EyeInsideBlock(phys.EyePosition)) return "suffocation (head inside a block)";
        if (cactus) return "cactus (touching one)";
        return FallOrMobCause(phys.Position) ?? StatusDamageCause(p, dmg);
    }

    /// <summary>Head under, the air out (or a drowning hit just now), and a drowning-sized hit.</summary>
    private bool DrowningHit(PlayerState p, int dmg) =>
        p.HeadUnderWater && (p.Air <= 0 || TickCount - _lastDrownHitTick < 10) && dmg == Breath.DrownDamage;

    /// <summary>Lava, fire and cactus in the cells the body box (grown 0.01 sideways) touches.</summary>
    private (bool Lava, bool Fire, bool Cactus) TouchingHazards(AABB box)
    {
        bool lava = false, fire = false, cactus = false;
        for (int x = (int)Math.Floor(box.MinX - 0.01); x <= (int)Math.Floor(box.MaxX + 0.01); x++)
        for (int y = (int)Math.Floor(box.MinY); y <= (int)Math.Floor(box.MaxY); y++)
        for (int z = (int)Math.Floor(box.MinZ - 0.01); z <= (int)Math.Floor(box.MaxZ + 0.01); z++)
        {
            int id = World.GetBlock(x, y, z);
            if (BlockShapes.IsLava(id)) lava = true;
            else if (id == Blocks.Fire) fire = true;
            else if (id == Blocks.Cactus) cactus = true;
        }
        return (lava, fire, cactus);
    }

    /// <summary>The eye is inside an opaque full block.</summary>
    private bool EyeInsideBlock(Vec3d eye)
    {
        int ex = (int)Math.Floor(eye.X), ey = (int)Math.Floor(eye.Y), ez = (int)Math.Floor(eye.Z);
        int eyeId = World.GetBlock(ex, ey, ez);
        return eyeId != 0 && !BlockShapes.IsLiquid(eyeId) && BlockShapes.IsFullCube(World, ex, ey, ez) && Blocks.Get(eyeId) is { Transparent: false };
    }

    /// <summary>A fall just now, or a mob (knocked back just now, or a hostile within reach).</summary>
    private string? FallOrMobCause(Vec3d feet)
    {
        if (TickCount - _lastLandTick <= 10 && _lastLandFall > 3) return $"a fall of {_lastLandFall:0.#} blocks";
        var hostile = World.Entities.Nearby(feet, 4.5, CombatRules.IsThreat).OrderBy(e => e.Position.DistanceTo(feet)).FirstOrDefault();
        if (TickCount - _lastKnockbackTick > 10 && hostile is null) return null;
        return hostile is { } h ? $"{h.DisplayName} attacking ({h.Position.DistanceTo(feet):0.#}m away)" : "hit by something (knocked back)";
    }

    private static string? StatusDamageCause(PlayerState p, int dmg)
    {
        if (p.Food <= 0 && dmg == 1) return "starvation (food 0)";
        if (p.HasEffect(PotionEffects.Wither)) return "withering";
        if (p.HasEffect(PotionEffects.Poison)) return "poison";
        if (p.HeadUnderWater && p.Air <= 0) return $"drowning (head under water, {Breath.Text(p.Air)})";
        return null;
    }
}
