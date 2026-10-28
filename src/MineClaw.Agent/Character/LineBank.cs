using System.Text.RegularExpressions;
using MineClaw.Agent.Brainstem;

namespace MineClaw.Agent.Character;

/// <summary>One prepared in-character line: the moment it fits, the player it's for (null = anyone), the context it was written in.</summary>
public sealed record PreparedLine(string Moment, string? Player, string Text, string Context, DateTimeOffset At);

/// <summary>
/// What the character's situation is right now, as far as wording goes: what the body is doing, who is around and talking,
/// time of day, mood words and hp band. <see cref="Signature"/> is the tag prepared lines carry; a line written for another
/// signature is stale ("chopping wood" while mining) and dropped.
/// </summary>
public sealed record CharacterContext(string Activity, IReadOnlyList<string> Players, string TimeOfDay, string Mood, string Health,
    string? Partner = null, string? Here = null)
{
    public static readonly CharacterContext Idle = new("idle", [], "day", "", "ok");

    public string Signature =>
        $"{Activity}|{string.Join(",", Players.Select(p => p.ToLowerInvariant()).Order(StringComparer.Ordinal))}|{TimeOfDay}|{Mood}|{Health}";

    /// <summary>The context line the prefetch prompt reads.</summary>
    public string Describe() =>
        $"doing: {Activity}; players on: {(Players.Count == 0 ? "none" : string.Join(", ", Players))}; {TimeOfDay}; " +
        $"mood: {(Mood.Length == 0 ? "neutral" : Mood)}; health: {Health}" + (Partner != null ? $"; talking with {Partner}" : "") +
        (Here is { Length: > 0 } h ? "; " + h : "");
}

/// <summary>
/// The prefetched lines (zero tokens to read): tagged by moment, player and context, expired when the context changes or they
/// age out, and never repeated — every line the bot says (prefetched or the brain's own) goes into the phrase memory, and a
/// line too close to one said within <see cref="CharacterOptions.RepeatMinutes"/> is skipped. Thread-safe.
/// </summary>
public sealed class LineBank
{
    private readonly TimeProvider _time;
    private readonly CharacterOptions _o;
    private readonly object _lock = new();
    private readonly List<PreparedLine> _lines = new();
    private readonly LinkedList<(string Norm, DateTimeOffset At)> _said = new();
    private readonly Random _rng;
    private string _context = "";

    public LineBank(CharacterOptions? options = null, TimeProvider? time = null, int? seed = null)
    {
        _o = options ?? new CharacterOptions();
        _time = time ?? TimeProvider.System;
        _rng = seed is { } s ? new Random(s) : new Random();
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    /// <summary>The context signature lines are being kept for.</summary>
    public string Context { get { lock (_lock) return _context; } }

    /// <summary>Lines expired by context changes and age (telemetry).</summary>
    public int Expired { get; private set; }

    /// <summary>The situation changed: every line written for another context is dropped. Returns how many went.</summary>
    public int SetContext(string signature)
    {
        lock (_lock)
        {
            if (signature == _context) return 0;
            _context = signature;
            int n = _lines.RemoveAll(l => l.Context != signature);
            Expired += n;
            return n;
        }
    }

    public void Add(string moment, string? player, string text, string context)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return;
        lock (_lock)
        {
            if (context != _context) return; // written for a context that already changed
            if (_lines.Any(l => Norm(l.Text) == Norm(t))) return;
            _lines.Add(new PreparedLine(moment.ToLowerInvariant(), player, t, context, Now));
            while (_lines.Count > 80) _lines.RemoveAt(0);
        }
    }

    /// <summary>
    /// A fresh line for <paramref name="moment"/> (the player's own first, then generic), not said recently; it leaves the bank.
    /// Null when none fits (the caller stays quiet or the brain speaks).
    /// </summary>
    public string? Take(string moment, string? player = null)
    {
        var m = (moment ?? "").ToLowerInvariant();
        lock (_lock)
        {
            Prune();
            var fits = _lines.Where(l => l.Moment == m && (l.Player == null || player == null || l.Player.Equals(player, StringComparison.OrdinalIgnoreCase))
                && !RecentlySaidLocked(l.Text)).ToList();
            if (fits.Count == 0) return null;
            var own = fits.Where(l => l.Player != null).ToList();
            var pool = own.Count > 0 ? own : fits;
            var pick = pool[_rng.Next(pool.Count)];
            _lines.Remove(pick);
            return pick.Text;
        }
    }

    /// <summary>How many usable lines a moment has (context-fresh, not said recently).</summary>
    public int Count(string moment, string? player = null)
    {
        var m = moment.ToLowerInvariant();
        lock (_lock)
        {
            Prune();
            return _lines.Count(l => l.Moment == m && (player == null || l.Player == null || l.Player.Equals(player, StringComparison.OrdinalIgnoreCase)) && !RecentlySaidLocked(l.Text));
        }
    }

    public IReadOnlyList<PreparedLine> All() { lock (_lock) { Prune(); return _lines.ToList(); } }

    /// <summary>Phrase memory: the bot said this (any source).</summary>
    public void NoteSaid(string text)
    {
        var n = Norm(text);
        if (n.Length == 0) return;
        lock (_lock)
        {
            _said.AddLast((n, Now));
            while (_said.Count > 200) _said.RemoveFirst();
        }
    }

    /// <summary>True when a line this close was said within the repeat window.</summary>
    public bool RecentlySaid(string text) { lock (_lock) return RecentlySaidLocked(text); }

    /// <summary>The bot's recent lines (the prefetch prompt avoids them), newest last.</summary>
    public IReadOnlyList<string> RecentSaid(int max = 12)
    {
        lock (_lock)
        {
            var cut = Now - TimeSpan.FromMinutes(Math.Max(1, _o.RepeatMinutes));
            return _said.Where(s => s.At >= cut).Select(s => s.Norm).TakeLast(max).ToList();
        }
    }

    private bool RecentlySaidLocked(string text)
    {
        var n = Norm(text);
        var cut = Now - TimeSpan.FromMinutes(Math.Max(1, _o.RepeatMinutes));
        foreach (var (s, at) in _said)
            if (at >= cut && Similar(s, n)) return true;
        return false;
    }

    private void Prune()
    {
        var cut = Now - TimeSpan.FromMinutes(Math.Max(1, _o.LineMaxAgeMinutes));
        Expired += _lines.RemoveAll(l => l.At < cut);
    }

    /// <summary>Letters/digits only, lower case, single spaces ("Omw, Foxy~!" → "omw foxy").</summary>
    public static string Norm(string s) => Regex.Replace(Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9 ]+", " "), @"\s+", " ").Trim();

    /// <summary>The same line, or most of its words (Jaccard ≥ 0.7 over 3+ word lines): "omw foxy" ≈ "omw foxy!!".</summary>
    public static bool Similar(string a, string b)
    {
        a = Norm(a); b = Norm(b);
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        var wa = a.Split(' ').ToHashSet();
        var wb = b.Split(' ').ToHashSet();
        if (wa.Count < 3 || wb.Count < 3) return false;
        double inter = wa.Intersect(wb).Count(), union = wa.Union(wb).Count();
        return inter / union >= 0.7;
    }

    /// <summary>The moment a chat intent maps to (greeting → greet, status → status, ...), or null when there's none.</summary>
    public static string? MomentOf(string intent) => intent switch
    {
        "greeting" => "greet", "farewell" => "bye", "thanks" => "thanks", "praise" => "praise", "insult" => "tease",
        "status" => "status", "come" => "come", "follow" => "follow", "stop" => "stop", _ => null,
    };
}

/// <summary>Human-like typing: think time + a delay per character, jittered ±20 %, capped low (an instant line still lands in &lt;1 s).</summary>
public static class HumanTiming
{
    public static TimeSpan TypingDelay(string text, CharacterOptions? o = null, Random? rng = null)
    {
        o ??= new CharacterOptions();
        double ms = o.ThinkMs + o.PerCharMs * (text ?? "").Length;
        double jitter = 0.8 + (rng ?? Random.Shared).NextDouble() * 0.4;
        return TimeSpan.FromMilliseconds(Math.Clamp(ms * jitter, 60, Math.Max(60, o.MaxTypingMs)));
    }
}
