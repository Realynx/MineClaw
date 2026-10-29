using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MineClaw.Protocol.Packets;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>
/// Body language for chatting with players: jump, spin, look at someone, wave (arm swing), crouch, and a consented
/// playful punch. Everything moves through the normal tick pipeline, so the EdgeGuard filters every input (a jump whose
/// landing it can't verify is cancelled, crouching near edges stays clamped).
/// </summary>
public sealed partial class MinecraftClient
{
    /// <summary>A player's explicit "punch me" (chat or <see cref="NotePunchRequest"/>), and how many punches it bought.</summary>
    private sealed record PunchRequest(DateTime At, int Used);
    private readonly ConcurrentDictionary<string, PunchRequest> _punchRequests = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Health estimate per player for punching (worst case: no regeneration within a minute).</summary>
    private readonly ConcurrentDictionary<string, (double Hp, DateTime At, DateTime LastPunch)> _punchHp = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _crouchHold;

    public static readonly TimeSpan PunchConsentWindow = TimeSpan.FromSeconds(30);
    public const int PunchesPerRequest = 3;
    /// <summary>No playful punches once the estimate is at or below this (10 = five hearts).</summary>
    public const double PunchMinHealth = 10;

    /// <summary>Held crouch (<see cref="CrouchAsync"/>); actions still sneak on their own where needed.</summary>
    public bool IsCrouching => _crouchHold;

    /// <summary>Jumps in place <paramref name="times"/> (1-10). Each jump goes through the EdgeGuard; cancelled jumps are
    /// reported, not retried forever.</summary>
    public Task<ActionResult> JumpAsync(int times = 1, CancellationToken ct = default) =>
        RunActionAsync("jump", ct, async c =>
        {
            int want = Math.Clamp(times, 1, 10), done = 0, ticks = 0, groundTicks = 0;
            bool airborne = false;
            await DriveAsync(phys =>
            {
                ticks++;
                if (!phys.OnGround && !phys.InWater) { if (!airborne) { airborne = true; done++; } groundTicks = 0; }
                else { airborne = false; groundTicks++; }
                bool finished = (done >= want && phys.OnGround) || ticks > want * 25 + 20;
                bool jump = !finished && done < want && phys.OnGround && groundTicks >= 2;
                return new DriverStep(new MoveInput(Jump: jump, Yaw: _lookYaw, Pitch: _lookPitch), Done: finished);
            }, c).ConfigureAwait(false);
            return done >= want ? ActionResult.Success($"jumped {done}x", done)
                : ActionResult.Fail($"jumped {done}/{want} (the edge guard held the rest)", done);
        });

    /// <summary>Turns in place <paramref name="turns"/> full circles (1-5), one per second, then faces the start direction.</summary>
    public Task<ActionResult> SpinAsync(int turns = 1, CancellationToken ct = default) =>
        RunActionAsync("spin", ct, async c =>
        {
            int n = Math.Clamp(turns, 1, 5), total = n * 20, t = 0;
            float start = _lookYaw;
            await DriveAsync(_ =>
            {
                t++;
                float yaw = start + 360f * t / 20f;
                return new DriverStep(new MoveInput(Yaw: t >= total ? start : yaw, Pitch: 0), Done: t >= total);
            }, c).ConfigureAwait(false);
            return ActionResult.Success($"spun {n} time(s)");
        });

    /// <summary>Faces a player (eye to eye).</summary>
    public Task<ActionResult> LookAtPlayerAsync(string name, CancellationToken ct = default) =>
        RunActionAsync("look", ct, async c =>
        {
            var p = World.Entities.FindPlayer(name);
            if (p is null) return ActionResult.Fail($"can't see {name}");
            var (yaw, pitch) = PlayerPhysics.YawPitchTo(World.Player.EyePosition, p.Position + new Vec3d(0, PlayerPhysics.EyeHeight, 0));
            await LookCoreAsync(yaw, pitch, c).ConfigureAwait(false);
            return ActionResult.Success($"looking at {p.Name ?? name}");
        });

    // ------------------------------------------------------------------ brainstem: idle head/arm (no action lock)

    /// <summary>
    /// Turns the head toward <paramref name="point"/> on the next tick when nothing drives the body (no action, no fight, no
    /// safety hold): no action lock, no waiting, so it can run the instant chat arrives. False when the body is busy.
    /// </summary>
    public bool FaceIdle(Vec3d point)
    {
        if (!IsPlaying || CurrentAction is not null || _driver is not null || SafetyEngaged) return false;
        var (yaw, pitch) = PlayerPhysics.YawPitchTo(World.Player.EyePosition, point);
        _lookYaw = yaw;
        _lookPitch = Math.Clamp(pitch, -90f, 90f);
        return true;
    }

    /// <summary>One arm swing (a "nod") without the action lock; skipped while an action, a fight or a safety hold runs.</summary>
    public bool SwingIdle()
    {
        if (!IsPlaying || CurrentAction is not null || SafetyEngaged) return false;
        Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
        return true;
    }

    /// <summary>A safety reflex owns the body: a fight/defence, the post-teleport ground hold, or the low-health retreat.</summary>
    public bool SafetyEngaged => (_combatInstance?.Engaged ?? false) || _safetyFreeze || _healRetreat is not null;

    /// <summary>Waves: <paramref name="times"/> arm swings (0x12 animation 1), a few ticks apart.</summary>
    public Task<ActionResult> SwingArmAsync(int times = 3, CancellationToken ct = default) =>
        RunActionAsync("wave", ct, async c =>
        {
            int n = Math.Clamp(times, 1, 10);
            for (int i = 0; i < n; i++)
            {
                Send(new AnimationPacket { EntityId = World.Player.EntityId, Animation = 1 });
                await WaitTicksAsync(4, c).ConfigureAwait(false);
            }
            return ActionResult.Success($"waved ({n} swings)");
        });

    /// <summary>Crouches (held until turned off) or stands up. The tick loop sends the 0x13 crouch/uncrouch.</summary>
    public Task<ActionResult> CrouchAsync(bool on, CancellationToken ct = default) =>
        RunActionAsync("crouch", ct, async c =>
        {
            _crouchHold = on;
            await WaitTicksAsync(2, c).ConfigureAwait(false);
            return ActionResult.Success(on ? "crouching" : "standing up");
        });

    /// <summary>One entry point for the compact <c>act {move, target?, times?}</c> tool: jump | spin | look | wave |
    /// crouch (times 0 = stand up) | uncrouch | punch (target = player).</summary>
    public Task<ActionResult> ActAsync(string move, string? target = null, int? times = null, CancellationToken ct = default) =>
        (move ?? "").Trim().ToLowerInvariant() switch
        {
            "jump" => JumpAsync(times ?? 1, ct),
            "spin" => SpinAsync(times ?? 1, ct),
            "look" => target is { Length: > 0 } t ? LookAtPlayerAsync(t, ct) : Task.FromResult(ActionResult.Fail("look needs a target player")),
            "wave" => SwingArmAsync(times ?? 3, ct),
            "crouch" => CrouchAsync(times is not 0, ct),
            "uncrouch" or "stand" => CrouchAsync(false, ct),
            "punch" => target is { Length: > 0 } p ? PlayfulPunchAsync(p, ct) : Task.FromResult(ActionResult.Fail("punch needs a target player")),
            _ => Task.FromResult(ActionResult.Fail($"unknown move '{move}' (jump|spin|look|wave|crouch|punch)")),
        };

    // ------------------------------------------------------------------ playful punch

    /// <summary>Records that <paramref name="player"/> explicitly asked to be punched (chat "punch me" does this
    /// automatically). Each request allows <see cref="PunchesPerRequest"/> punches within <see cref="PunchConsentWindow"/>.</summary>
    public void NotePunchRequest(string player) => _punchRequests[player] = new PunchRequest(DateTime.UtcNow, 0);

    /// <summary>"punch me", "hit me", "smack me", "bonk me", "poke me" (whole words, any case).</summary>
    public static bool IsPunchRequest(string message) => PunchWords().IsMatch(message);

    [GeneratedRegex(@"\b(punch|hit|smack|bonk|poke|slap)\s+me\b", RegexOptions.IgnoreCase)]
    private static partial Regex PunchWords();

    private void OnChatForBody(string sender, string text)
    {
        if (string.Equals(sender, Options.Username, StringComparison.OrdinalIgnoreCase)) return;
        if (IsPunchRequest(text)) NotePunchRequest(sender);
    }

    /// <summary>Estimated health of <paramref name="player"/> for punching: the combat engine's spar memory as the start,
    /// minus 1 per landed bare-hand punch and the assumed damage of other hurts; reset after a minute (regeneration).</summary>
    public double PunchHealthEstimate(string player)
    {
        if (_punchHp.TryGetValue(player, out var e) && DateTime.UtcNow - e.At <= TimeSpan.FromMinutes(1)) return e.Hp;
        return Combat.SparStartHealth(player);
    }

    /// <summary>A hurt status (0x26 = 2) on a player: our punch (−1, bare hand) if it follows one closely, else another
    /// source (−assumed spar damage).</summary>
    private void OnPlayerHurtForPunch(int entityId)
    {
        if (World.Entities.Get(entityId) is not { Kind: EntityKind.Player, Name: { } name }) return;
        var now = DateTime.UtcNow;
        double hp = PunchHealthEstimate(name);
        bool ours = _punchHp.TryGetValue(name, out var e) && now - e.LastPunch <= TimeSpan.FromMilliseconds(600);
        hp = Math.Max(0, hp - (ours ? 1 : Options.Combat.SparOtherDamageAssumed));
        _punchHp[name] = (hp, now, ours ? DateTime.MinValue : e.LastPunch);
    }

    /// <summary>
    /// ONE bare-handed hit (0x12 swing + 0x07 left-click) on <paramref name="player"/>, only when that same player asked
    /// within the last 30 s (at most 3 per request), never when the knockback could push them toward an edge or I'd
    /// have to stand at one, and not once their estimated health is low. Not a fight: the combat engine is not engaged.
    /// </summary>
    public Task<ActionResult> PlayfulPunchAsync(string player, CancellationToken ct = default) =>
        RunActionAsync("punch", ct, async c =>
        {
            if (!_punchRequests.TryGetValue(player, out var req) || DateTime.UtcNow - req.At > PunchConsentWindow)
                return ActionResult.Fail($"only if {player} asks me to (say \"punch me\"), within 30 s");
            if (req.Used >= PunchesPerRequest) return ActionResult.Fail($"that's {PunchesPerRequest} punches for that request; ask again");
            var target = World.Entities.FindPlayer(player);
            if (target is null) return ActionResult.Fail($"can't see {player}");
            double hp = PunchHealthEstimate(player);
            if (hp <= PunchMinHealth) return ActionResult.Fail($"{player} looks hurt (est hp {hp:0}): no punching");

            if (World.Player.EyePosition.DistanceTo(target.Position + new Vec3d(0, 1, 0)) > EntityUseReach)
            {
                var near = GoalFiltered.AwayFromEdge(World, new GoalNearPosition(target.Position, 2));
                await GotoCoreAsync(near, GotoOptions.Default with { Timeout = TimeSpan.FromSeconds(15), MaxReplans = 1, AllowOpenDoors = false }, c)
                    .ConfigureAwait(false);
                target = World.Entities.FindPlayer(player) ?? target;
                if (World.Player.EyePosition.DistanceTo(target.Position + new Vec3d(0, 1, 0)) > EntityUseReach)
                    return ActionResult.Fail($"can't get close to {player} safely");
            }
            if (PunchEdgeVeto(target) is { } veto) return ActionResult.Fail(veto);
            int empty = -1;
            for (int i = 0; i < 9; i++) if (World.Inventory.GetPlayerSlot(36 + i).IsEmpty) { empty = i; break; }
            if (empty < 0) return ActionResult.Fail("my hands are full (no empty hotbar slot for a bare-handed punch)");
            SelectHotbar(empty);
            var (yaw, pitch) = PlayerPhysics.YawPitchTo(World.Player.EyePosition, target.Position + new Vec3d(0, 1.2, 0));
            await LookCoreAsync(yaw, pitch, c).ConfigureAwait(false);

            if (!_punchRequests.TryGetValue(player, out req) || DateTime.UtcNow - req.At > PunchConsentWindow || req.Used >= PunchesPerRequest)
                return ActionResult.Fail($"{player}'s request expired");
            _punchRequests[player] = req with { Used = req.Used + 1 };
            _punchHp[player] = (hp, _punchHp.TryGetValue(player, out var e) ? e.At : DateTime.UtcNow, DateTime.UtcNow);
            if (_session is not { Ended: false } hitSession) throw new ActionAbortedException("not connected");
            SendMeleeHit(hitSession, target.Id);
            await WaitTicksAsync(2, c).ConfigureAwait(false);
            int left = PunchesPerRequest - req.Used - 1;
            return ActionResult.Success($"bonk! punched {player} ({left} left for that request)");
        });

    /// <summary>Why a punch could knock <paramref name="target"/> toward an edge or needs me at one, or null.</summary>
    private string? PunchEdgeVeto(Entity target)
    {
        var me = World.Player.Position;
        var tp = target.Position;
        int mx = (int)Math.Floor(me.X), my = (int)Math.Floor(me.Y + 1e-6), mz = (int)Math.Floor(me.Z);
        if (Safety.EdgeDistance(World, mx, my, mz, 2) < 2) return "I'm too close to an edge to punch";
        var dir = new Vec3d(tp.X - me.X, 0, tp.Z - me.Z);
        double len = dir.HorizontalLength;
        dir = len < 1e-3 ? new Vec3d(1, 0, 0) : dir * (1 / len);
        int ty = (int)Math.Floor(tp.Y + 1e-6);
        if (Safety.EdgeDistance(World, (int)Math.Floor(tp.X), ty, (int)Math.Floor(tp.Z), 2) < 2)
            return $"{target.Name ?? "they"} is too close to an edge to punch";
        // b1.8 knockback ≈ 0.4 horizontal + a hop: nothing deadly within 3 blocks along the push.
        for (double d = 0.5; d <= 3.01; d += 0.5)
        {
            int x = (int)Math.Floor(tp.X + dir.X * d), z = (int)Math.Floor(tp.Z + dir.Z * d);
            if (Safety.IsDeadlyColumn(World, x, ty, z, 3)) return $"{target.Name ?? "they"} would be knocked toward an edge";
        }
        return null;
    }
}
