namespace MineClaw.Client;

/// <summary>
/// Outcome of a client action: terse and LLM-friendly. <see cref="ToString"/> gives "ok: ..." / "fail: ...".
/// <see cref="Count"/> carries the main quantity when there is one (items mined, crafted, moved...).
/// </summary>
public sealed record ActionResult(bool Ok, string Message, int Count = 0)
{
    public static ActionResult Success(string message, int count = 0) => new(true, message, count);
    public static ActionResult Fail(string reason, int count = 0) => new(false, reason, count);

    public override string ToString() => (Ok ? "ok: " : "fail: ") + Message;
}

/// <summary>What a danger signal is about.</summary>
public enum DangerKind
{
    /// <summary>Falling with nothing below (death + item loss imminent).</summary>
    FallingIntoVoid,
    /// <summary>Health went down.</summary>
    Damage,
    /// <summary>Health at or below <see cref="Survival.SurvivalOptions.LowHealth"/> (critical at CriticalHealth).</summary>
    LowHealth,
    /// <summary>Standing in lava or on fire.</summary>
    Lava,
    /// <summary>A hostile mob came within <see cref="ClientOptions.HostileAlertRadius"/>.</summary>
    HostileNearby,
    /// <summary>Hungry with nothing (safe) to eat in the inventory; <see cref="DangerEvent.Level"/> 1 hungry, 2 starving
    /// (food ≤ 6: no sprint), 3 food 0 (starvation damage). Re-sent only when the level rises.</summary>
    Hunger,
    /// <summary>After a teleport/respawn the ground under the bot is missing or not loaded: movement is frozen.</summary>
    UnsafePosition,
    /// <summary>A previous critical danger is over (e.g. landed safely after a void clutch).</summary>
    Recovered,
    /// <summary>Falling farther than a safe drop onto ground that IS there (not the void): fall damage, not item loss.
    /// Live 10-27: clone 67 fell 6 blocks off a tree crown and was told "falling into the void".</summary>
    HighFall,
}

/// <summary>A danger signal for the Agent (fires at most once per episode, not every tick).
/// <see cref="Critical"/> = life or inventory at stake right now (the running action has been cancelled).</summary>
public sealed record DangerEvent(DangerKind Kind, string Message)
{
    public bool Critical { get; init; }
    /// <summary>Severity within the kind (e.g. hunger level 1..3); 0 when not graded.</summary>
    public int Level { get; init; }
}

/// <summary>Which gap jumps a goto may plan.</summary>
public enum ParkourMode
{
    /// <summary>No gap jumps.</summary>
    Off,
    /// <summary>Gaps where a miss is a fall within the safe height.</summary>
    Ground,
    /// <summary>Also gaps over the void (explicitly requested; see <see cref="GotoOptions.Parkour"/>).</summary>
    Void,
}

/// <summary>Connection lifecycle state.</summary>
public enum ClientState { Disconnected, Connecting, Playing, Reconnecting }

/// <summary>Movement options for <see cref="MinecraftClient.GotoAsync(MineClaw.World.Goal, GotoOptions?, CancellationToken)"/>.</summary>
public sealed record GotoOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Re-plans allowed after "stuck"/"off path" failures.</summary>
    public int MaxReplans { get; init; } = 4;
    /// <summary>Mine blocks in the way (respects the protected predicate). Off by default: other people's builds.</summary>
    public bool AllowBreak { get; init; }
    /// <summary>Place scaffold blocks (cobblestone/dirt/planks from the inventory) to bridge or pillar.</summary>
    public bool AllowPlace { get; init; }
    public int ScaffoldBudget { get; init; } = 16;
    public bool AllowSprint { get; init; }
    /// <summary>Max path search time per plan.</summary>
    public TimeSpan PlanTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Walk toward the closest reachable point when the goal itself can't be reached.</summary>
    public bool AllowPartial { get; init; } = true;
    /// <summary>Plan through closed wooden doors, fence gates and trapdoor hatches (iron doors only with a known
    /// button/lever/plate on the near side): stop, face it, right-click, wait for the open bit, pass. Doors of protected
    /// builds are used too (protection forbids breaking, not opening; they're put back); doors that didn't open (locked)
    /// are never planned through again.</summary>
    public bool AllowOpenDoors { get; init; } = true;
    /// <summary>Put opened doors/gates/hatches back the way they were once past them.</summary>
    public bool RestoreDoors { get; init; } = true;
    /// <summary>Gap jumps (sprint-jump / plain gap jump): <see cref="ParkourMode.Ground"/> (default) only where a miss is a
    /// small fall; <see cref="ParkourMode.Void"/> also over the void — honoured only with
    /// <see cref="ClientOptions.AllowVoidParkour"/> or a trusted player's chat in the last few minutes, and a clutch block
    /// in the hotbar.</summary>
    public ParkourMode Parkour { get; init; } = ParkourMode.Ground;

    public static GotoOptions Default { get; } = new();
}
