namespace MineClaw.Agent.Events;

public enum EventKind
{
    Chat,
    Whisper,
    TaskCompleted,
    TaskFailed,
    TaskProgress,
    LowHealth,
    VoidFall,
    Death,
    PlayerJoined,
    PlayerLeft,
    IdleTick,
    /// <summary>Anything else worth telling the model (server message, budget change...).</summary>
    Notice,
    /// <summary>
    /// Something noticed in the world (another player's activity, a build appearing, a chest emptied...), e.g. from the
    /// WorldIndexer. Default priority Low: wakes the model, never interrupts an activity. Text should be short and may say
    /// "probably" when uncertain.
    /// </summary>
    Activity,
    /// <summary>A player's /me action ("* kiwi pats claudechan"); addressed when it mentions the bot.</summary>
    Emote,
    /// <summary>
    /// A player's body language the bot saw (<c>player.gesture</c>: "[gesture] PoofimaFox nods yes at you"). Background unless
    /// it answers the bot's open question (Normal: a nod is a yes) or is aimed at the bot (Low, rate-limited): Bot/Brainstem/GestureFeed.
    /// </summary>
    Gesture,
}

public enum EventPriority
{
    /// <summary>Context only: rides along with the next waking event, never wakes the model by itself.</summary>
    Background = 0,
    Low = 1,
    Normal = 2,
    High = 3,
    Critical = 4,
}

/// <summary>
/// Something that happened in the game. Created by the client layer (danger, tasks) or the
/// <see cref="Chat.ChatRouter"/> (chat). Only events with <see cref="Wakes"/> set invoke the model.
/// </summary>
public sealed record GameEvent
{
    public GameEvent(EventKind kind, string text, string? player = null, EventPriority? priority = null, bool? wakes = null, DateTimeOffset? at = null)
    {
        Kind = kind;
        Text = text;
        Player = player;
        Priority = priority ?? DefaultPriority(kind);
        Wakes = wakes ?? Priority >= EventPriority.Low;
        At = at ?? DateTimeOffset.UtcNow;
    }

    public EventKind Kind { get; init; }
    public string Text { get; init; }
    public string? Player { get; init; }
    public EventPriority Priority { get; init; }
    /// <summary>True if this event should invoke the model (else it is background context).</summary>
    public bool Wakes { get; init; }
    public DateTimeOffset At { get; init; }
    /// <summary>How many identical events were merged into this one.</summary>
    public int Count { get; init; } = 1;
    /// <summary>
    /// Injected by a test harness (console <c>/as</c>), not said by the real player: logged/indexed with source=test and
    /// never used as evidence for the self-profile.
    /// </summary>
    public bool IsTest { get; init; }

    /// <summary>
    /// Where a player's line came from when it wasn't the game server: <c>"viewer"</c> = the web viewer (a trusted player typing
    /// to one bot, <c>ChatRouter.OnRemoteChat</c>). Real speech (not <see cref="IsTest"/>); chat_log keeps it as <c>viewer_chat</c>.
    /// Null = in-game chat.
    /// </summary>
    public string? Origin { get; init; }

    /// <summary>
    /// A background job failed or stalled (script failure/limit, a queued step that never ran, a walk that can't arrive, a worker
    /// or clone failure, the stall watchdog): it wakes the brain even while a script has it asleep, at its normal priority, so it
    /// never interrupts the body the way <see cref="AgentOptions.InterruptPriority"/> events do.
    /// </summary>
    public bool MustWake { get; init; }

    /// <summary>
    /// Chat only: how sure the router was that this line is for the bot (see <c>AddressReasonExtensions.Confidence</c>);
    /// null = not decided by the router (injected/test events count as sure).
    /// </summary>
    public double? AddressConfidence { get; init; }

    /// <summary>
    /// The body (login name) this event concerns, or null when it concerns the brain itself (chat routing, budget, memory...)
    /// or the main body before bodies stamp their events. One brain, many bodies: identical events from two bodies never
    /// merge (the queue's dedupe keys include it).
    /// </summary>
    public string? Body { get; init; }

    public static EventPriority DefaultPriority(EventKind kind) => kind switch
    {
        EventKind.Death or EventKind.VoidFall => EventPriority.Critical,
        EventKind.LowHealth => EventPriority.High,
        EventKind.Whisper or EventKind.Chat or EventKind.Emote or EventKind.TaskFailed or EventKind.TaskCompleted => EventPriority.Normal,
        EventKind.PlayerJoined or EventKind.IdleTick or EventKind.Notice or EventKind.Activity => EventPriority.Low,
        _ => EventPriority.Background, // TaskProgress, PlayerLeft
    };

    public static GameEvent Chat(string player, string text, bool addressed = true) =>
        new(EventKind.Chat, text, player, addressed ? EventPriority.Normal : EventPriority.Background);
    public static GameEvent Whisper(string player, string text) => new(EventKind.Whisper, text, player);
    /// <summary>/me from a player: <paramref name="action"/> is the text after their name.</summary>
    public static GameEvent Emote(string player, string action, bool addressed = true) =>
        new(EventKind.Emote, action, player, addressed ? EventPriority.Normal : EventPriority.Background);
    /// <summary>Body language: <paramref name="what"/> reads after the name ("nods yes at you (answering your question)").</summary>
    public static GameEvent Gesture(string player, string what, EventPriority priority = EventPriority.Background) =>
        new(EventKind.Gesture, what, player, priority);
    public static GameEvent TaskDone(string summary) => new(EventKind.TaskCompleted, summary);
    public static GameEvent TaskFail(string summary, bool wakes = true) =>
        new(EventKind.TaskFailed, summary, priority: wakes ? EventPriority.Normal : EventPriority.Background);
    public static GameEvent Progress(string text) => new(EventKind.TaskProgress, text);
    public static GameEvent LowHealth(int hp, int max = 20, string? cause = null) =>
        new(EventKind.LowHealth, $"hp {hp}/{max}" + (cause != null ? " " + cause : ""));
    public static GameEvent VoidFall(double y) => new(EventKind.VoidFall, $"falling toward void, y {y:0}");
    public static GameEvent Died(string? cause = null) => new(EventKind.Death, cause ?? "you died");
    public static GameEvent Joined(string player) => new(EventKind.PlayerJoined, "joined", player);
    public static GameEvent Left(string player) => new(EventKind.PlayerLeft, "left", player);
    public static GameEvent Idle(string? note = null) => new(EventKind.IdleTick, note ?? "idle check-in");
    public static GameEvent Notice(string text, EventPriority priority = EventPriority.Low) => new(EventKind.Notice, text, priority: priority);
    /// <summary>World activity worth reacting to ("kiwi probably built a cobble gen by the tree"); <paramref name="player"/> = who did it.</summary>
    public static GameEvent Activity(string text, string? player = null, EventPriority priority = EventPriority.Low) =>
        new(EventKind.Activity, text, player, priority);

    /// <summary>Compact one-line rendering for the model.</summary>
    public string Format()
    {
        string tag = Kind switch
        {
            EventKind.Chat => Priority == EventPriority.Background ? "chat(not to you)" : "chat",
            EventKind.Whisper => "whisper",
            EventKind.TaskCompleted => "task ok",
            EventKind.TaskFailed => "task failed",
            EventKind.TaskProgress => "task",
            EventKind.LowHealth or EventKind.VoidFall => "DANGER",
            EventKind.Death => "DIED",
            EventKind.PlayerJoined or EventKind.PlayerLeft => "player",
            EventKind.IdleTick => "idle",
            EventKind.Activity => "noticed",
            EventKind.Emote => "emote",
            EventKind.Gesture => "gesture",
            _ => "note",
        };
        string body = Player != null ? $"{Player}: {Text}" : Text;
        if (Kind is EventKind.PlayerJoined or EventKind.PlayerLeft or EventKind.Emote or EventKind.Gesture && Player != null) body = $"{Player} {Text}";
        // One brain, many bodies: a non-main body's event says whose it is ("[67 task ok] ...", "[69 DANGER] hp 5/20").
        // Null (hive mode, main, brain-level) renders exactly as before.
        if (BodyTag(Body) is { } who && !Text.StartsWith("[bodies]", StringComparison.Ordinal)) tag = who + " " + tag;
        return $"[{tag}] {body}" + (Count > 1 ? $" (x{Count})" : "");
    }

    /// <summary>The body prefix for <see cref="Format"/>: the short name of a non-main body, else null.</summary>
    public static string? BodyTag(string? body)
    {
        if (body is not { Length: > 0 } b || b.Equals("main", StringComparison.OrdinalIgnoreCase)) return null;
        int i = b.LastIndexOf('_'); // a login ("maid_chan_67") reads as its number
        return i >= 0 && i < b.Length - 1 && b[(i + 1)..].All(char.IsDigit) ? b[(i + 1)..] : b;
    }
}
