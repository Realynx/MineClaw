using System.Text.Json.Nodes;
using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Bot.Perception;
using MineClaw.Scripting.Events;

namespace MineClaw.Bot.Brainstem;

/// <summary>
/// The brain's view of other players' body language (<c>player.gesture</c>), cheap by design: one short line per gesture
/// ("[gesture] PoofimaFox nods yes at you (answering your question)"), and a wake only when it plausibly matters.
/// <list type="bullet">
/// <item>A nod / head shake is the universal yes / no: it answers what is pending (<see cref="ChatRouter.AnswerGesture"/>): an
/// ask_player waiting on them gets "yes (nod)" at once (the tool or its follow-up carries it to the brain: nothing posted here),
/// our chat question to them wakes the brain at Normal. The body acknowledges with a small nod first (act first, think after).</item>
/// <item>Aimed at this body (<c>at</c> = me): Low (wakes, never interrupts), at most once per player per <see cref="AimedWakeEvery"/>.</item>
/// <item>A point while we wait on that player answers too (Normal); otherwise everything is Background (folds into the next turn),
/// each player+kind at most once per <see cref="BackgroundEvery"/> (stares: <see cref="StareEvery"/>).</item>
/// </list>
/// One brain, many bodies: every body sees the same nod, so a body handles the gestures aimed at it ("me") and the main body the
/// unaimed ones; a gesture aimed at another bot (a foreign bot or another of our bodies) is that bot's answer, never ours (main
/// keeps it as Background context). A clone process (<c>answersOnly</c>) maps answers only: it never wakes on plain body language.
/// What a wave, a crouch-bob or a jump MEANS is never coded here (learned, not coded): a player teaches it as a binding
/// (<c>script{op:bind, params:{on:'player.gesture', kind:'bob', player:'Fox'}}</c> "when I crouch twice, come here").
/// </summary>
public sealed class GestureFeed
{
    private readonly ChatRouter _chat;
    private readonly EventQueue _events;
    private readonly string? _body;
    private readonly TimeProvider _time;
    private readonly Func<string, string?>? _ack;
    private readonly bool _main, _answersOnly;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan AimedWakeEvery { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan BackgroundEvery { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan StareEvery { get; init; } = TimeSpan.FromSeconds(60);

    /// <param name="body">This body's login for a non-main body (its events read "[67 gesture] ..."); null = main.</param>
    /// <param name="ack">Plays the body's small acknowledging nod toward a player (the brainstem's gestures), or null.</param>
    /// <param name="main">This is the main body (it handles the unaimed gestures).</param>
    /// <param name="answersOnly">Only map nods/shakes/points onto pending questions (a clone process).</param>
    public GestureFeed(ChatRouter chat, EventQueue events, string? body = null, TimeProvider? time = null, Func<string, string?>? ack = null,
        bool main = true, bool answersOnly = false)
    {
        _chat = chat;
        _events = events;
        _body = body;
        _time = time ?? TimeProvider.System;
        _ack = ack;
        _main = main;
        _answersOnly = answersOnly;
    }

    /// <summary>The events this feed posted (tests).</summary>
    public event Action<GameEvent>? Posted;

    private DateTimeOffset Now => _time.GetUtcNow();

    public void OnSignal(GameSignal s)
    {
        if (s.Name != "player.gesture" || s.Player is not { Length: > 0 } player) return;
        try { Handle(player, s.Data); } catch (Exception) { /* perception never breaks the brain */ }
    }

    private void Handle(string player, JsonObject d)
    {
        var kind = EventFilter.Str(d, "kind") ?? "";
        var at = EventFilter.Str(d, "at");
        bool atMe = at == "me";
        string what = Describe(kind, at, d);
        if (!atMe && !_main) return;                 // unaimed: main's; aimed at another body: that body's
        if (at is { Length: > 0 } && !atMe)
        {
            if (!_answersOnly && Due(player, kind + "@" + at, BackgroundEvery)) Post(player, what, EventPriority.Background);
            return;                                  // their nod at another bot answers that bot
        }
        if (kind is "nod" or "shake")
        {
            var answer = _chat.AnswerGesture(player, kind == "nod", atMe);
            if (answer != GestureAnswer.None)
            {
                Ack(player);
                Due(player, "aimed", AimedWakeEvery); // the nodding goes on a moment: no second wake for it
                // A waiter's own path (the ask_player result, its follow-up chat) brings "yes (nod)" to the brain.
                if (answer == GestureAnswer.Question) Post(player, what + " (answering your question)", EventPriority.Normal);
                return;
            }
        }
        else if (kind == "point" && _chat.AwaitsAnswerFrom(player))
        {
            Post(player, what + " (answering you)", EventPriority.Normal);
            return;
        }
        if (_answersOnly) return;
        if (atMe && kind != "stare" && Due(player, "aimed", AimedWakeEvery))
        {
            Post(player, what, EventPriority.Low);
            return;
        }
        if (Due(player, kind, kind == "stare" ? StareEvery : BackgroundEvery)) Post(player, what, EventPriority.Background);
    }

    private void Ack(string player)
    {
        try { _ack?.Invoke(player); } catch (Exception) { }
    }

    private bool Due(string player, string key, TimeSpan every)
    {
        lock (_lock)
        {
            var k = player + "|" + key;
            if (_last.TryGetValue(k, out var t) && Now - t < every) return false;
            _last[k] = Now;
            if (_last.Count > 64) _last.Remove(_last.MinBy(kv => kv.Value).Key);
            return true;
        }
    }

    private void Post(string player, string what, EventPriority priority)
    {
        var e = GameEvent.Gesture(player, what, priority) with { At = Now, Body = _body };
        _events.Post(e);
        try { Posted?.Invoke(e); } catch (Exception) { }
    }

    /// <summary>"nods yes at you", "shakes no", "crouch-bobs at 67", "points at the chest (4,64,-2)": present tense, no pronouns.</summary>
    public static string Describe(string kind, string? at, JsonObject d)
    {
        string verb = kind switch
        {
            "nod" => "nods yes",
            "shake" => "shakes no",
            "bob" => "crouch-bobs",
            "wave" => "waves",
            "jump" => "jumps around",
            "spin" => "spins around",
            "stare" => "stares",
            "point" => "points",
            _ => kind,
        };
        if (kind == "point")
        {
            var name = EventFilter.Str(d, "block") ?? EventFilter.Str(d, "entity") ?? "something";
            string where = d["target"] is JsonObject t ? $" ({EventFilter.Str(t, "x")},{EventFilter.Str(t, "y")},{EventFilter.Str(t, "z")})" : "";
            return $"points at the {name.Replace('_', ' ')}{where}";
        }
        return at switch
        {
            "me" => verb + " at you",
            { Length: > 0 } other => $"{verb} at {GameEvent.BodyTag(other) ?? other}",
            _ => verb,
        };
    }
}
