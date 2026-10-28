using MineClaw.Agent.Chat;
using MineClaw.Agent.Events;
using MineClaw.Agent.Llm;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent;

/// <summary>
/// Joins without a model call (live 10-28: robinchan, Robin's own bot, reconnected every few minutes and each join cost an
/// Opus turn for "wb robinchan~ maid-chan missed u fr"). A human regular joining gets ONE warm templated hi from main (the
/// bot's own "hi" line plays the wave gesture), debounced across reconnects; other bots and hive clones get nothing. Only
/// pending news for that player (<see cref="HasNewsFor"/>) still wakes the brain.
/// </summary>
public sealed partial class BotAgent
{
    private readonly Dictionary<string, DateTimeOffset> _greetedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _leftAt = new(StringComparer.OrdinalIgnoreCase);
    private int _greetIndex;

    /// <summary>Something waits to be told to this player (a finished job they asked for, a hand-over...): their join then
    /// still wakes the brain, which can say it. Null = nothing ever counts as news.</summary>
    public Func<string, bool>? HasNewsFor { get; set; }

    private void NoteJoinLeave(GameEvent e)
    {
        if (e.Kind != EventKind.PlayerLeft || e.Player is not { } p) return;
        lock (_greetedAt) _leftAt[p] = _time.GetUtcNow();
    }

    /// <summary>
    /// A batch whose only waking events are joins: greet the regulars that are due one, ignore the rest, and skip the model.
    /// Null = not such a batch (or there's news): the normal turn runs.
    /// </summary>
    private async Task<EpisodeOutcome?> TryHandleJoinsAsync(EventBatch batch, CancellationToken ct)
    {
        var o = _options.Chat.JoinGreeting;
        if (!o.Enabled) return null;
        var waking = batch.Waking.ToList();
        if (waking.Count == 0 || batch.HasDanger || waking.Any(e => e.Kind != EventKind.PlayerJoined || e.Player == null)) return null;
        if (waking.Any(e => HasNewsFor?.Invoke(e.Player!) == true)) return null;

        bool isClone = _options.Hive.CloneFor(_chat.Username) != null;
        var now = _time.GetUtcNow();
        var window = TimeSpan.FromMinutes(Math.Max(0, o.DebounceMinutes));
        var said = new List<string>();
        foreach (var p in waking.Select(e => e.Player!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string why;
            if (_chat.IsHiveBot?.Invoke(p) == true || _chat.IsOtherBot(p)) why = "a bot";
            else if (isClone) why = "main greets";
            else if (_chat.IsRegular?.Invoke(p) != true) why = "not a regular";
            else
            {
                DateTimeOffset? greeted, left;
                lock (_greetedAt)
                {
                    greeted = _greetedAt.TryGetValue(p, out var g) ? g : null;
                    left = _leftAt.TryGetValue(p, out var l) ? l : null;
                }
                if (greeted is { } gg && now - gg < window) why = $"greeted {(int)(now - gg).TotalSeconds}s ago";
                else if (left is { } ll && now - ll < window) why = $"reconnect ({(int)(now - ll).TotalSeconds}s away)";
                else
                {
                    var line = GreetingFor(p, o);
                    lock (_greetedAt) _greetedAt[p] = now;
                    try
                    {
                        var sent = await _chat.SayAsync(line, ct: ct).ConfigureAwait(false);
                        said.AddRange(sent);
                        _log.LogInformation("join: greeted {Player} (template, no model call): {Line}", p, line);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "join greeting failed"); }
                    continue;
                }
            }
            _log.LogInformation("join: {Player} not greeted ({Why}), no model call", p, why);
        }
        return new EpisodeOutcome(null, 0, LlmUsage.Zero, 0, said, false, "join handled");
    }

    private string GreetingFor(string player, JoinGreetingOptions o)
    {
        var templates = o.Templates is { Length: > 0 } t ? t : JoinGreetingOptions.DefaultTemplates;
        string name = _options.Chat.PlayerNicknames.TryGetValue(player, out var nicks)
                      && nicks.FirstOrDefault(n => !n.Equals("master", StringComparison.OrdinalIgnoreCase)) is { Length: > 0 } nick
            ? nick : player;
        var line = templates[(int)((uint)(Interlocked.Increment(ref _greetIndex) - 1) % (uint)templates.Length)];
        return line.Replace("{name}", name, StringComparison.Ordinal);
    }
}
