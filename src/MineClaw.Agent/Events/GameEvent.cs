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
            _ => "note",
        };
        string body = Player != null ? $"{Player}: {Text}" : Text;
        if (Kind is EventKind.PlayerJoined or EventKind.PlayerLeft or EventKind.Emote && Player != null) body = $"{Player} {Text}";
        return $"[{tag}] {body}" + (Count > 1 ? $" (x{Count})" : "");
    }
}
