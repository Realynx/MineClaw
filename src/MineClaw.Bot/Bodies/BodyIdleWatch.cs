using System.Collections.Concurrent;
using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;

namespace MineClaw.Bot.Bodies;

/// <summary>
/// Bodies never stand idle without telling their hive (live 10-29: Fox "I see a lot of standing around agents"; 13 bodies had
/// finished or failed their chop and nobody knew). Polls the online clone bodies: one with no job, no script lane and no safety
/// reflex for <see cref="After"/> is idle. Once per idle stretch (a body is reported again only after it worked in between) the
/// brain gets ONE wake with the count, the names and why each went idle (its last job result: "exhausted: no tree within 24;
/// nearest beyond: …"), at most every <see cref="Cooldown"/>. <see cref="Summary"/> feeds the [bodies] line.
/// </summary>
public sealed class BodyIdleWatch : IDisposable
{
    private readonly BodyRegistry _reg;
    private readonly EventQueue _events;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _since = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _reported = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ActivityManager, string> _watched = new();
    private DateTimeOffset _lastWake = DateTimeOffset.MinValue;
    private ITimer? _timer;

    public BodyIdleWatch(BodyRegistry reg, EventQueue events, TimeProvider time)
    {
        _reg = reg;
        _events = events;
        _time = time;
    }

    /// <summary>How long a body stays without work before it counts as idle.</summary>
    public TimeSpan After { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Least time between two idle wakes.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Polls every <paramref name="period"/> on the brain's clock.</summary>
    public void Start(TimeSpan period) => _timer ??= _time.CreateTimer(_ => { try { Tick(); } catch (Exception) { } }, null, period, period);

    /// <summary>Idle body short names right now (idle at least <see cref="After"/>).</summary>
    public IReadOnlyList<string> Idle()
    {
        var now = _time.GetUtcNow();
        return _since.Where(kv => now - kv.Value >= After).Select(kv => Body.ShortName(kv.Key, false)).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>"idle 7 (101,105,…): exhausted: no tree within 24…" for the [bodies] line, or null.</summary>
    public string? Summary(int maxNames = 6)
    {
        var idle = Idle();
        if (idle.Count == 0) return null;
        return $"idle {idle.Count} ({string.Join(",", idle.Take(maxNames))}{(idle.Count > maxNames ? ",…" : "")})" + (Reason(idle) is { } r ? ": " + Clip(r, 60) : "");
    }

    /// <summary>One poll: refresh idle stretches, and post the wake when new bodies went idle.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        var online = _reg.All.Where(b => !b.IsMain && b.State is BodyState.Online && b.Adapter.IsPlaying).ToList();
        var names = online.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _since.Keys.Where(k => !names.Contains(k)).ToList()) { _since.TryRemove(gone, out _); _reported.TryRemove(gone, out _); }
        foreach (var b in online)
        {
            Watch(b);
            bool busy = b.Activities.IsBusy || b.Scripts?.Current != null || b.Adapter.SafetyEngaged;
            if (busy) { _since.TryRemove(b.Name, out _); _reported.TryRemove(b.Name, out _); }
            else _since.TryAdd(b.Name, now);
        }
        var fresh = _since.Where(kv => now - kv.Value >= After && !_reported.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList();
        if (fresh.Count == 0 || now - _lastWake < Cooldown) return;
        _lastWake = now;
        foreach (var f in fresh) _reported[f] = true;
        var idle = Idle();
        var shorts = fresh.Select(f => Body.ShortName(f, false)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var text = $"[bodies] {idle.Count} of {online.Count} bodies idle {After.TotalSeconds:0}s+ ({string.Join(", ", shorts.Take(10))}{(shorts.Count > 10 ? ", …" : "")})"
                   + (Reason(shorts) is { } why ? $"; last: {Clip(why, 160)}" : "")
                   + "; give them work (a wider area: chop_tree at=<a tree beyond>) or report done";
        _events.Post(GameEvent.Notice(text, EventPriority.Normal) with { MustWake = true });
    }

    /// <summary>The commonest last result among <paramref name="bodies"/> ("exhausted: …" counts first), or null.</summary>
    private string? Reason(IEnumerable<string> bodies)
    {
        var why = bodies.Select(b => _last.TryGetValue(Full(b), out var r) ? r : null).Where(r => r != null).Cast<string>().ToList();
        if (why.Count == 0) return null;
        return why.OrderByDescending(r => r.StartsWith("exhausted", StringComparison.Ordinal) || r.Contains("exhausted:", StringComparison.Ordinal))
            .GroupBy(r => r.Length > 48 ? r[..48] : r).OrderByDescending(g => g.Count()).First().First();
    }

    private string Full(string shortName) =>
        _since.Keys.Concat(_last.Keys).FirstOrDefault(k => Body.ShortName(k, false).Equals(shortName, StringComparison.OrdinalIgnoreCase)) ?? shortName;

    private void Watch(IBody b)
    {
        if (!_watched.TryAdd(b.Activities, b.Name)) return;
        var name = b.Name;
        b.Activities.StepFinished += (tool, _, result, _) =>
        {
            var t = result.Text ?? "";
            int i = t.IndexOf("exhausted:", StringComparison.Ordinal);
            _last[name] = $"{tool}: " + (i >= 0 ? t[i..] : t);
        };
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose() => _timer?.Dispose();
}
