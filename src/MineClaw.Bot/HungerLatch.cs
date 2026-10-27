using System.Text.Json;
using System.Text.RegularExpressions;
using MineClaw.Client;

namespace MineClaw.Bot;

/// <summary>
/// "Hungry, no food" is reported to the brain ONCE (2026-10-27: the bot begged for food ten times after "dw about food for
/// now", and every restart re-fired "danger Hunger: starving" plus a begging turn). After the first report the signal is
/// latched: it wakes the brain again only when it gets worse and the player hasn't acknowledged it, or when health crosses
/// the low line (hp ≤ 6). Food in the inventory (or food back above the eat line) clears it. The latch survives restarts
/// (a small json file next to the bot's data).
/// </summary>
public sealed class HungerLatch
{
    /// <summary>Health at or below this re-raises a latched hunger signal once.</summary>
    public const int LowHealth = 6;

    private static readonly Regex Ack = new(
        @"\b(?:dw|don'?t worry|no worries|never ?mind|nvm|forget)\b[^.!?]{0,30}\bfood\b|\bfood\b[^.!?]{0,30}\b(?:for now|later|soon|on it|handled|sorted)\b" +
        @"|\bworking on (?:the |your |some )?food\b|\bi'?ll (?:get|bring|find|make|grab) (?:you |u )?(?:some )?(?:food|snacks?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private State? _state;

    private sealed record State(int Level, int Health, bool Acknowledged, DateTimeOffset At);

    public HungerLatch(string? path = null, TimeProvider? time = null)
    {
        _path = path;
        _time = time ?? TimeProvider.System;
        try
        {
            if (_path != null && File.Exists(_path)) _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
        }
        catch (Exception) { _state = null; }
    }

    /// <summary>How long a latch holds without any new signal before a report counts as new again.</summary>
    public TimeSpan Expiry { get; init; } = TimeSpan.FromHours(6);

    public int Level { get { lock (_lock) return _state?.Level ?? 0; } }
    public bool Acknowledged { get { lock (_lock) return _state?.Acknowledged == true; } }

    /// <summary>
    /// A hunger danger arrived: true = wake the brain (report it), false = context only. Latches what was reported.
    /// </summary>
    public bool ShouldWake(DangerEvent d, int health)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var s = _state;
            if (s != null && now - s.At > Expiry) s = null;
            bool wake = s == null
                || (!s.Acknowledged && d.Level > s.Level)
                || (health <= LowHealth && s.Health > LowHealth);
            _state = new State(Math.Max(d.Level, s?.Level ?? 0), health, s?.Acknowledged == true, wake || s == null ? now : s.At);
            Save();
            return wake;
        }
    }

    /// <summary>A player's line: "dw about food for now" / "I'm working on the food situation" acknowledges the signal.</summary>
    public bool OnChat(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Ack.IsMatch(text)) return false;
        lock (_lock)
        {
            var s = _state ?? new State(0, 20, false, _time.GetUtcNow());
            _state = s with { Acknowledged = true, At = _time.GetUtcNow() };
            Save();
        }
        return true;
    }

    /// <summary>The bot has food again (or isn't hungry): the next out-of-food signal is news.</summary>
    public void Observe(int food, bool hasFood, int eatAtFood)
    {
        if (!hasFood && food <= eatAtFood) return;
        lock (_lock)
        {
            if (_state == null) return;
            _state = null;
            Save();
        }
    }

    // caller holds _lock
    private void Save()
    {
        if (_path == null) return;
        try
        {
            if (_state == null) { if (File.Exists(_path)) File.Delete(_path); return; }
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_state));
        }
        catch (Exception) { /* best effort */ }
    }
}
