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

    /// <summary>
    /// A working session: someone talked to the bot this recently. Within it a question to them is never "nobody is waiting"
    /// (live 10-30: Fox worked the stair shaft with main for hours with gaps of more than 2 min while it dug; its "can I break the
    /// next column?" questions were kept in the log and the dig sat waiting). Routine reports stay quiet (low chat verbosity).
    /// </summary>
    public static readonly TimeSpan SessionWindow = TimeSpan.FromMinutes(30);

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

    /// <summary>
    /// The batch's player-facing wakes are only dangers (a player's danger Activity, the bot's own low hp / void fall / death):
    /// no chat, whisper, emote, join or notice from anyone.
    /// </summary>
    public static bool IsWarningBatch(EventBatch batch)
    {
        var facing = batch.Waking.Where(IsPlayerFacing).ToList();
        return facing.Count > 0 && facing.All(e => e.Kind is EventKind.Activity or EventKind.LowHealth or EventKind.VoidFall or EventKind.Death);
    }

    /// <summary>
    /// A danger turn's public part: its first line (the warning), once; everything after it is the turn's own narration.
    /// <paramref name="alreadySaid"/> = the warning went out in an earlier round: nothing more is public.
    /// </summary>
    internal static (string Public, string Internal) WarningLine(string text, bool alreadySaid)
    {
        var lines = text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (alreadySaid || lines.Count == 0) return ("", text);
        // One paragraph: the first sentence is the warning.
        if (lines.Count == 1)
        {
            var parts = System.Text.RegularExpressions.Regex.Split(lines[0], @"(?<=[.!?~])\s+(?=[A-Z])");
            return (parts[0].Trim(), string.Join(" ", parts.Skip(1)).Trim());
        }
        return (lines[0], string.Join("\n", lines.Skip(1)));
    }

    /// <summary>
    /// The turn's (quiet, warnOnly) after events joined it mid-turn. A quiet turn joined only by a 'noticed'/danger event (a
    /// player's death or fall) becomes a warning turn: one short line is public, the rest stays internal (live 10-28 03:16: a
    /// task-event turn joined by "Fox fell into the void and died" posted "...so I told him I'd keep the cobble coming... I
    /// rewrote the mining script..." in chat). A player's chat/whisper/emote/join makes it a chat turn again.
    /// </summary>
    internal static (bool Quiet, bool WarnOnly) AfterJoined(bool quiet, bool warnOnly, EventBatch joined)
    {
        var facing = joined.Waking.Where(IsPlayerFacing).ToList();
        if (facing.Count == 0) return (quiet, warnOnly);
        bool warning = facing.All(e => e.Kind is EventKind.Activity or EventKind.LowHealth or EventKind.VoidFall or EventKind.Death);
        return warning ? (false, quiet || warnOnly) : (false, false);
    }

    /// <summary>An event a player is on the other end of: their chat/emote/join, a notice about them (a hand-over), their danger.</summary>
    public static bool IsPlayerFacing(GameEvent e) => e.Kind switch
    {
        EventKind.LowHealth or EventKind.VoidFall or EventKind.Death => true,
        _ => e.Player != null && FacesItsPlayer(e),
    };

    /// <summary>For an event naming a player: whether its kind puts that player on the other end.</summary>
    private static bool FacesItsPlayer(GameEvent e) => e.Kind switch
    {
        EventKind.Chat or EventKind.Whisper or EventKind.Emote or EventKind.PlayerJoined or EventKind.Notice => true,
        // A nod/shake answering our question or a wave at us: a player gestured to the bot (a background one never woke it).
        EventKind.Gesture => e.Wakes,
        // DangerMonitor posts a player's danger as an Activity at Normal/High ("robin is taking damage near the tree"): a
        // safety warning goes out even when the player asked for quiet. Plain world observations stay Low (quiet).
        EventKind.Activity => e.Priority >= EventPriority.Normal,
        _ => false,
    };

    /// <summary>A quiet turn's say may speak: a player addressed the bot recently, a job failed, or someone is in danger.</summary>
    private bool Engaged(IReadOnlyCollection<GameEvent> events, string? text = null)
    {
        if (events.Any(e => e.Wakes && (e.Kind is EventKind.TaskFailed or EventKind.LowHealth or EventKind.VoidFall or EventKind.Death || e.MustWake)))
            return true;
        if (_chat.AddressedWithin(EngagedWindow)) return true;
        if (!_chat.AddressedWithin(SessionWindow)) return false;
        // In a session: a question to the partner goes out (the bot is blocked on it); reports and narration stay quiet.
        return text?.Contains('?') == true;
    }

    private void KeepInternal(string text)
    {
        text = CorrectCounts(text);
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

    private async Task<ToolResult> SayToolAsync(string text, bool quiet, bool warnOnly, List<GameEvent> events, TurnSpeech speech, SpeechCtx cx, CancellationToken ct)
    {
        List<GameEvent> snapshot;
        lock (events) snapshot = events.ToList();
        // A safety warning ("Robin, a zombie is attacking you") goes out even when nobody is engaged or a quiet rule stands.
        if (quiet && !Engaged(snapshot, text) && !_chat.Warnings.Overrides(text, cx.Audience.ToArray()))
        {
            KeepInternal(text);
            return ToolResult.Success("not said: nobody is waiting on this (kept in your log)");
        }
        if (warnOnly)
        {
            // A danger/'noticed' turn: one short in-persona line, once; the rest is the turn's own narration.
            bool saidAny;
            lock (cx.Said) saidAny = cx.Said.Count > 0;
            var (line, rest) = WarningLine(text, saidAny);
            if (!IsSilent(rest)) KeepInternal(rest);
            if (IsSilent(line)) return ToolResult.Success("not said: you already said your one line (kept in your log)");
            text = line;
        }
        var clean = FilterStreamed(text, cx, AckOf(speech));
        if (IsSilent(clean.Trim())) return ToolResult.Success("not said (filtered: not chat)");
        var lines = await SayBrainAsync(speech, cx, clean, ct).ConfigureAwait(false);
        return ToolResult.Success(lines.Count > 0 ? "said" : "not said");
    }
}
