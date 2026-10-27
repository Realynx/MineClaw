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
    /// <summary>Health at or below <see cref="ClientOptions.LowHealthThreshold"/>.</summary>
    LowHealth,
    /// <summary>Standing in lava or on fire.</summary>
    Lava,
    /// <summary>A hostile mob came within <see cref="ClientOptions.HostileAlertRadius"/>.</summary>
    HostileNearby,
    /// <summary>Food is at 6 or below (can't sprint, no regeneration).</summary>
    Hunger,
    /// <summary>After a teleport/respawn the ground under the bot is missing or not loaded: movement is frozen.</summary>
    UnsafePosition,
    /// <summary>A previous critical danger is over (e.g. landed safely after a void clutch).</summary>
    Recovered,
}

/// <summary>A danger signal for the Agent (fires at most once per episode, not every tick).
/// <see cref="Critical"/> = life or inventory at stake right now (the running action has been cancelled).</summary>
public sealed record DangerEvent(DangerKind Kind, string Message)
{
    public bool Critical { get; init; }
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

    public static GotoOptions Default { get; } = new();
}
