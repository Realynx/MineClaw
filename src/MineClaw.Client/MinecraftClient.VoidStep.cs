using MineClaw.Data;
using MineClaw.World;

namespace MineClaw.Client;

/// <summary>A deliberate void step in progress (<see cref="MinecraftClient.StepIntoVoidAsync"/>): why, from where, since when.</summary>
public sealed record IntentionalVoidStep(string Reason, Vec3d From, DateTimeOffset At);

/// <summary>How a deliberate void step ended: died in the void as planned and respawned at <see cref="RespawnedAt"/> with these vitals.</summary>
public sealed record IntentionalResetOutcome(string Reason, Vec3d DiedAt, Vec3d RespawnedAt, int Health, int Food)
{
    public override string ToString() =>
        $"respawned at {RespawnedAt.X:0.#},{RespawnedAt.Y:0.#},{RespawnedAt.Z:0.#} with hp {Health}, food {Food}";
}

/// <summary>
/// The one explicit, opt-in way off the island (user design decision, live 10-27: Fox's "jump off and respawn with full hp and
/// hunger after dumping items" is a strategy the bot learns from memory, so the client only makes it POSSIBLE): a generic
/// primitive with guard rails, no strategy logic. While a step is active (and only then) the EdgeGuard filter, the clutch and the
/// "falling into the void" danger stand aside; every other movement keeps full void protection. A death during the step is an
/// intentional reset, reported through <see cref="IntentionalResetDone"/> once the respawn's health arrives.
/// </summary>
public sealed partial class MinecraftClient
{
    private volatile IntentionalVoidStep? _voidStep;

    /// <summary>How long after the step its death still counts as the intentional reset (a fall to y −64 takes a few seconds).</summary>
    public static readonly TimeSpan VoidStepWindow = TimeSpan.FromSeconds(45);

    /// <summary>The deliberate void step in progress (stepping off, falling, dead, respawning), or null.</summary>
    public IntentionalVoidStep? ActiveVoidStep => _voidStep is { } v && DateTimeOffset.UtcNow - v.At <= VoidStepWindow ? v : null;

    /// <summary>Raised when the bot starts walking off the edge on purpose (announce it once).</summary>
    public event Action<IntentionalVoidStep>? VoidStepStarted;

    /// <summary>Raised after the deliberate death and respawn, once the server's health for the new life arrived.</summary>
    public event Action<IntentionalResetOutcome>? IntentionalResetDone;

    /// <summary>
    /// Walks to the nearest edge and steps off into the void, on purpose. Guard rails: a <paramref name="reason"/> is required;
    /// anything carried is refused ("deposit first: holding X") unless <paramref name="keepItemsOk"/>; the result warns when the
    /// respawn point isn't known to be solid ground. Ok = stepped off (the death and respawn follow; see
    /// <see cref="IntentionalResetDone"/>).
    /// </summary>
    public Task<ActionResult> StepIntoVoidAsync(string reason, bool keepItemsOk = false, CancellationToken ct = default)
    {
        reason = (reason ?? "").Trim();
        if (reason.Length == 0) return Task.FromResult(ActionResult.Fail("into_void needs a reason (a deliberate step off the island)"));
        if (!keepItemsOk && CarriedSummary() is { } held)
            return Task.FromResult(ActionResult.Fail($"deposit first: holding {held} (or keep_items_ok=true to lose them)"));
        return RunActionAsync("into_void", ct, async c =>
        {
            var me = World.Player.Position;
            int py = (int)Math.Floor(me.Y + 1e-6);
            if (World.IsPlayerOverVoid(me) && !World.Player.OnGround)
                return ActionResult.Fail("already over the void");
            if (NearestVoidEdge(me, py) is not { } edge)
                return ActionResult.Fail("no reachable island edge with open void next to it within 10 blocks");
            var (stand, off) = edge;
            if (Math.Abs(stand.X + 0.5 - me.X) > 0.4 || Math.Abs(stand.Z + 0.5 - me.Z) > 0.4 || stand.Y != py)
            {
                var g = await GotoCoreAsync(new GoalBlock(stand), GotoOptions.Default with { AllowBreak = false, AllowPlace = false }, c).ConfigureAwait(false);
                if (!g.Ok) return ActionResult.Fail($"couldn't reach the edge at {Fmt(stand)}: {g.Message}");
            }
            string warn = RespawnWarning();
            var step = new IntentionalVoidStep(reason, World.Player.Position, DateTimeOffset.UtcNow);
            _voidStep = step;
            Log($"into_void: stepping off at {Fmt(stand)} toward {Fmt(off)} on purpose ({reason}){(warn.Length > 0 ? "; " + warn : "")}");
            Raise(() => VoidStepStarted?.Invoke(step));
            var target = new Vec3d(off.X + 0.5, stand.Y, off.Z + 0.5);
            long until = TickCount + 80;
            await DriveAsync(phys =>
            {
                var (yaw, _) = PlayerPhysics.YawPitchTo(phys.Position, target);
                bool gone = phys.Position.Y < stand.Y - 2.5 || TickCount > until;
                return new DriverStep(new MoveInput(Forward: 1, Yaw: yaw, Pitch: 30), Done: gone, AllowDrop: true);
            }, c).ConfigureAwait(false);
            if (World.Player.Position.Y >= stand.Y - 2.5 && !World.Player.IsDead)
            {
                _voidStep = null;
                return ActionResult.Fail($"didn't drop off the edge at {Fmt(stand)} (blocked?)");
            }
            return ActionResult.Success($"stepped off into the void at {Fmt(stand)} on purpose ({reason}); respawn at spawn " +
                                        $"{World.Spawn.X},{World.Spawn.Y},{World.Spawn.Z} expected" + (warn.Length > 0 ? "; " + warn : ""));
        });
    }

    /// <summary>"cobblestone x13, stone pickaxe" of everything carried (inventory, armor, cursor), or null when empty.</summary>
    private string? CarriedSummary()
    {
        var slots = World.Inventory.PlayerSlots();
        var groups = new Dictionary<int, int>();
        for (int i = 5; i < slots.Length; i++)
            if (!slots[i].IsEmpty) groups[slots[i].Id] = groups.GetValueOrDefault(slots[i].Id) + slots[i].Count;
        if (!World.Inventory.Cursor.IsEmpty) groups[World.Inventory.Cursor.Id] = groups.GetValueOrDefault(World.Inventory.Cursor.Id) + World.Inventory.Cursor.Count;
        if (groups.Count == 0) return null;
        var parts = groups.OrderByDescending(kv => kv.Value).Select(kv => $"{ItemName(kv.Key)} x{kv.Value}").ToList();
        return string.Join(", ", parts.Take(4)) + (parts.Count > 4 ? $" +{parts.Count - 4} more" : "");
    }

    /// <summary>"respawn point unknown/unsafe: …" unless the world spawn is loaded with solid ground under it.</summary>
    private string RespawnWarning()
    {
        var sp = World.Spawn;
        if (sp == default) return "respawn point unknown/unsafe: no spawn position from the server";
        if (!World.IsLoaded(sp.X, sp.Z)) return $"respawn point unknown/unsafe: spawn {sp.X},{sp.Y},{sp.Z} isn't loaded";
        // b1.8.1 respawns at the world spawn moved up to the first free spot above solid ground (WorldProvider / ChunkCoordinates);
        // a void column there is the y≈0 spawn fresh accounts get (live 10-27: -6,0,-9).
        return Safety.SupportBelow(World, sp.X, Math.Min(WorldState.Height - 1, sp.Y + 4), sp.Z) < 0
            ? $"respawn point unknown/unsafe: no ground under spawn {sp.X},{sp.Y},{sp.Z}" : "";
    }

    /// <summary>Nearest standable cell (≤10 blocks, feet y within −2..+1) with a loaded, open void column beside it.</summary>
    private (BlockPos Stand, BlockPos Off)? NearestVoidEdge(Vec3d me, int py)
    {
        (BlockPos, BlockPos)? best = null;
        double bestD = double.MaxValue;
        int cx = (int)Math.Floor(me.X), cz = (int)Math.Floor(me.Z);
        BlockPos[] sides = [new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1)];
        for (int dy = -2; dy <= 1; dy++)
        for (int dx = -10; dx <= 10; dx++)
        for (int dz = -10; dz <= 10; dz++)
        {
            int x = cx + dx, y = py + dy, z = cz + dz;
            if (y < 1 || y >= WorldState.Height - 2 || !World.IsLoaded(x, z) || !Safety.IsStandable(World, x, y, z)) continue;
            double d = Math.Sqrt(dx * dx + dz * dz) + Math.Abs(dy) * 2;
            if (d >= bestD) continue;
            foreach (var s in sides)
            {
                int ox = x + s.X, oz = z + s.Z;
                if (!World.IsLoaded(ox, oz)) continue; // unknown is not "open void"
                if (BlockShapes.HasCollision(World, ox, y, oz) || BlockShapes.HasCollision(World, ox, y + 1, oz)) continue;
                if (!Safety.IsOverVoid(World, ox, y, oz)) continue;
                best = (new BlockPos(x, y, z), new BlockPos(ox, y, oz));
                bestD = d;
                break;
            }
        }
        return best;
    }

    /// <summary>Tick thread: the EdgeGuard pass, except during a deliberate void step (the only time it yields).</summary>
    private GuardDecision GuardOrVoidStep(PhysicsState phys, MoveInput input, bool allowDrop, ParkourCommit? parkour) =>
        ActiveVoidStep is not null ? new GuardDecision(input, GuardActions.None, false) : SafetyFilter(phys, input, allowDrop, parkour);

    /// <summary>Died during a deliberate void step: wait for the respawn's vitals and report the outcome (no alarms).</summary>
    private void OnDiedDuringVoidStep(IntentionalVoidStep step, Vec3d diedAt)
    {
        Log($"into_void: intentional reset ({step.Reason}): died in the void as planned");
        _ = Task.Run(async () =>
        {
            try
            {
                var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                // Respawned (alive again) and the new life's 0x08 health arrived (IsDead false, health > 0).
                // ...and the respawn's 0x0D moved the body away from where it died.
                while (DateTime.UtcNow < until && (World.Player.IsDead || World.Player.Health <= 0 || World.Player.Position.DistanceTo(diedAt) < 3))
                    await Task.Delay(25).ConfigureAwait(false);
                await Task.Delay(100).ConfigureAwait(false); // food rides in the same 0x08; let a follow-up 0x08 land
                if (World.Player.IsDead) { Log("into_void: no respawn within 30 s"); return; }
                var outcome = new IntentionalResetOutcome(step.Reason, diedAt, World.Player.Position, World.Player.Health, World.Player.Food);
                Log($"into_void: intentional reset done: {outcome}");
                if (ReferenceEquals(_voidStep, step)) _voidStep = null;
                Raise(() => IntentionalResetDone?.Invoke(outcome));
            }
            catch (Exception ex) { Log("into_void: outcome failed: " + ex.Message); }
        });
    }
}
