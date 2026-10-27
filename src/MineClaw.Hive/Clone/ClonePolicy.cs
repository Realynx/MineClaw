using System.Text.RegularExpressions;
using MineClaw.Agent;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Hive.Chat;

namespace MineClaw.Hive.Clone;

/// <summary>
/// What a hive clone hears and says — deterministic, in C#, so a clone can't talk unaddressed whatever its model does:
/// <list type="bullet">
/// <item><b>intake</b>: only chat the arbiter gives this clone (named: "67 ...", or its own sticky follow-ups), whispers,
///   its hive tasks, its own task results and its own danger reach the queue. World noticing (other players' deaths/falls,
///   new builds), joins/leaves and idle check-ins are main's job and are dropped, so they never wake (or bill) the clone;</item>
/// <item><b>speech</b>: public chat only in a turn a player addressed it, or one short ack line for a hive task that asks it
///   to say something; never hive-internal status ("my hive task isn't marked done") — that goes to main as progress.</item>
/// <item><b>chat log</b>: main is the single writer of public chat; the clone logs only its own lines and whispers.</item>
/// </list>
/// </summary>
public static class ClonePolicy
{
    /// <summary>Prefix of the notice a clone's brain gets for an IPC task from main.</summary>
    public const string TaskTag = "[hive task]";

    private static readonly Regex AskSpeech = new(@"\b(?:say|tell|greet|ask|announce|ack|acknowledge|reply|answer|chat|thank|wave|hello|hi)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Sentences about hive plumbing the players must never see.</summary>
    private static readonly Regex HiveTalk = new(
        @"[^.!?\n]*\b(?:hive|hive_report|clones?|marked (?:as )?done|report(?:ed|ing)? (?:back )?to (?:the )?main|main bot)\b[^.!?\n]*[.!?]*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Intake filter for the clone's event queue (null = dropped).</summary>
    public static GameEvent? Intake(GameEvent e) => e.Kind switch
    {
        EventKind.Chat or EventKind.Emote => e.Wakes ? e : null,       // only lines handled by this clone
        EventKind.Activity => null,                                     // world noticing / other players' deaths: main's
        EventKind.PlayerJoined or EventKind.PlayerLeft => null,
        EventKind.IdleTick => null,
        _ => e,                                                         // whispers, hive tasks, own tasks, own danger
    };

    public static bool IsHiveTask(GameEvent e) => e.Kind == EventKind.Notice && e.Text.Contains(TaskTag, StringComparison.Ordinal);

    /// <summary>True when the batch holds a player line this clone handles (addressed chat, emote or whisper).</summary>
    public static bool Addressed(EventBatch batch) =>
        batch.Waking.Any(e => e.Kind is EventKind.Chat or EventKind.Whisper or EventKind.Emote && e.Player != null);

    /// <summary>
    /// The clone's speech gate (<see cref="BotAgent.SpeechGate"/>): the text to say publicly, or null to stay silent.
    /// </summary>
    public static string? Speech(EventBatch events, string text, bool alreadySpoke)
    {
        bool addressed = Addressed(events);
        bool ack = !addressed && events.Events.Any(e => IsHiveTask(e) && AskSpeech.IsMatch(TaskText(e.Text)));
        if (!addressed && !ack) return null;
        if (ack && alreadySpoke) return null; // a task ack is one line, once
        var clean = HiveTalk.Replace(text, "").Trim();
        clean = Regex.Replace(clean, @"\s{2,}", " ");
        if (clean.Length == 0 || clean.All(c => !char.IsLetterOrDigit(c))) return null;
        if (ack)
        {
            var first = Regex.Match(clean, @"^[^\n.!?]*[.!?]?").Value.Trim();
            clean = first.Length > 0 ? first : clean;
            if (clean.Length > 90) clean = clean[..90].TrimEnd();
        }
        return clean;
    }

    private static string TaskText(string notice)
    {
        int i = notice.IndexOf(TaskTag, StringComparison.Ordinal);
        var t = i >= 0 ? notice[(i + TaskTag.Length)..] : notice;
        int dash = t.IndexOf(" — take what you need", StringComparison.Ordinal);
        return dash >= 0 ? t[..dash] : t;
    }

    /// <summary>
    /// Wires the policy into one clone's chat router, event queue and (optionally) brain. Production (CloneMode) and the
    /// real-process hive tests (CloneStub) both go through here.
    /// </summary>
    public static void Apply(ChatRouter chat, EventQueue events, ChatArbiter arbiter, BotAgent? agent = null, Action<string>? muted = null)
    {
        arbiter.Bind(chat); // ownership, thread leases, never eating another bot's reply
        chat.ExplicitlyAddressed = arbiter.AddressesSelf;
        chat.DropBackgroundChat = true;
        events.Intake = Intake;
        if (agent == null) return;
        agent.SpeechGate = Speech;
        agent.SpeaksOn = b => b.Events.Any(e => IsHiveTask(e) && AskSpeech.IsMatch(TaskText(e.Text))); // the task ack line
        agent.LogPublicChat = false;
        agent.TurnDigest = null; // other players' activity digest is main's context, not the clone's
        if (muted != null) agent.Muted += muted;
    }
}
