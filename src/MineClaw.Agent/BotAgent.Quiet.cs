using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent;

/// <summary>
/// Quiet turns (live 10-27 23:30-23:37): a turn woken only by task/system events — a standing-order run finishing, a hive
/// notice, an idle check-in, a world observation — has no player to talk to, so the brain's plain text is a private log line
/// (logged, forwarded via <see cref="Muted"/> to the hive board, raised as <see cref="InternalSpeech"/>), never public chat.
/// Only the brain's explicit <c>say</c> call speaks there, and only while a player is engaged: someone addressed the bot within
/// <see cref="EngagedWindow"/>, a job failed (it may need help), or someone is in danger.
/// </summary>
public sealed partial class BotAgent
{
    public const string SayToolName = "say";

    public const string SayDescription =
        "Chat on a turn no player woke (text there is a private log); only if one waits, must help or is in danger.";

    /// <summary>A player who addressed the bot within this long is engaged (a quiet turn's say may reach them).</summary>
    public static readonly TimeSpan EngagedWindow = TimeSpan.FromMinutes(2);

    /// <summary>Raised with a quiet turn's plain text (kept internal: log, hive board, supervisor).</summary>
    public event Action<string>? InternalSpeech;

    /// <summary>
    /// A player-less batch whose text may still go to chat through <see cref="SpeechGate"/> (a hive clone's one-line ack of a
    /// task that asks it to speak). Null = every task/system batch is quiet.
    /// </summary>
    public Func<EventBatch, bool>? SpeaksOn { get; set; }

    private Func<string, CancellationToken, Task<ToolResult>>? _turnSay;
    private volatile bool _turnStatusAsked;

    /// <summary>
    /// True when no waking event comes from a player (chat, whisper, emote, join, a hand-over notice) or puts anyone in danger:
    /// task results, hive/system notices, idle check-ins and world observations only.
    /// </summary>
    public static bool IsQuietBatch(EventBatch batch)
    {
        var waking = batch.Waking.ToList();
        if (waking.Count == 0 || batch.HasDanger) return false;
        return !waking.Any(IsPlayerFacing);
    }

    /// <summary>An event a player is on the other end of: their chat/emote/join, a notice about them (a hand-over), their danger.</summary>
    public static bool IsPlayerFacing(GameEvent e) => e.Kind switch
    {
        EventKind.Chat or EventKind.Whisper or EventKind.Emote or EventKind.PlayerJoined => e.Player != null,
        EventKind.Notice => e.Player != null,
        // DangerMonitor posts a player's danger as an Activity at Normal/High ("robin is taking damage near the tree"): a
        // safety warning goes out even when the player asked for quiet. Plain world observations stay Low (quiet).
        EventKind.Activity => e.Player != null && e.Priority >= EventPriority.Normal,
        EventKind.LowHealth or EventKind.VoidFall or EventKind.Death => true,
        _ => false,
    };

    /// <summary>A quiet turn's say may speak: a player addressed the bot recently, a job failed, or someone is in danger.</summary>
    private bool Engaged(IReadOnlyCollection<GameEvent> events)
    {
        if (events.Any(e => e.Wakes && (e.Kind is EventKind.TaskFailed or EventKind.LowHealth or EventKind.VoidFall or EventKind.Death || e.MustWake)))
            return true;
        return _chat.AddressedWithin(EngagedWindow);
    }

    private void KeepInternal(string text)
    {
        _log.LogInformation("[internal] {Text}", text);
        try { Muted?.Invoke(text); } catch (Exception) { }
        try { InternalSpeech?.Invoke(text); } catch (Exception) { }
    }

    private void RegisterSayTool()
    {
        if (_tools.TryGet(SayToolName, out _)) return;
        _tools.Register(SayToolName, SayDescription, ToolSchema.Create().Str("text").Build(), async (input, ct) =>
        {
            var text = (input.Str("text") ?? "").Trim();
            if (text.Length == 0) return ToolResult.Error("say: text is empty");
            var say = _turnSay;
            if (say == null) return ToolResult.Error("not said: no turn running");
            return await say(text, ct).ConfigureAwait(false);
        }, tiers: ToolTiers.Main);
    }

    private async Task<ToolResult> SayToolAsync(string text, bool quiet, List<GameEvent> events, TurnSpeech speech, SpeechCtx cx, CancellationToken ct)
    {
        List<GameEvent> snapshot;
        lock (events) snapshot = events.ToList();
        if (quiet && !Engaged(snapshot))
        {
            KeepInternal(text);
            return ToolResult.Success("not said: nobody is waiting on this (kept in your log)");
        }
        var clean = FilterStreamed(text, cx, AckOf(speech));
        if (IsSilent(clean.Trim())) return ToolResult.Success("not said (filtered: not chat)");
        var lines = await SayBrainAsync(speech, cx, clean, ct).ConfigureAwait(false);
        return ToolResult.Success(lines.Count > 0 ? "said" : "not said");
    }
}
