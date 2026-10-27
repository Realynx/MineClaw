using MineClaw.Data;

namespace MineClaw.Client.Survival;

/// <summary>
/// Items held for someone else. Live 10-27 15:48: kiwi dropped bread for clone 67, main said "I'll pass it to 67", walked over,
/// picked it up and the survival reflex ate it on the next tick ("Oops, I picked up the bread and auto-ate it"). While a
/// hand-off intent is open (a chat line like "pass this to 67" / "for 67", a give-to-clone job), every item the bot picks up
/// (only the named one, when the intent names an item) is tagged reserved-for that target; auto-eat skips reserved items unless
/// health is critical (≤ <see cref="CriticalHp"/>). A tag ends on delivery (the item leaves the inventory: tossed, given,
/// deposited) or after its timeout. Thread-safe (packet, tick and host threads).
/// </summary>
public sealed class HandoffReservations(TimeProvider? time = null)
{
    /// <summary>At or below this health reserved food may be eaten anyway (staying alive comes first).</summary>
    public const int CriticalHp = 4;

    private sealed record Intent(string For, int? ItemId, DateTimeOffset Until);
    private sealed class Hold(int itemId, int count, string forWhom, DateTimeOffset until)
    {
        public int ItemId { get; } = itemId;
        public int Count { get; set; } = count;
        public string For { get; } = forWhom;
        public DateTimeOffset Until { get; set; } = until;
    }

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _lock = new();
    private readonly List<Hold> _holds = new();
    private Intent? _intent;

    /// <summary>How long an open intent catches pickups.</summary>
    public TimeSpan IntentFor { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How long a picked-up item stays reserved when it isn't delivered.</summary>
    public TimeSpan HoldFor { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Log sink ("reserved bread x1 for 67").</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Open (or refresh) a hand-off intent: pickups until it expires are held for <paramref name="forWhom"/>.</summary>
    /// <param name="itemId">Only this item is caught (null = anything picked up).</param>
    public void Open(string forWhom, int? itemId = null, TimeSpan? ttl = null)
    {
        lock (_lock) _intent = new Intent(forWhom, itemId, _time.GetUtcNow() + (ttl ?? IntentFor));
        Log?.Invoke($"hand-off: holding pickups{(itemId is { } i ? " of " + Items.DisplayName(i, 0) : "")} for {forWhom}");
    }

    /// <summary>The open intent's target (null = none).</summary>
    public string? OpenFor { get { lock (_lock) return _intent is { } i && i.Until > _time.GetUtcNow() ? i.For : null; } }

    public void Close() { lock (_lock) _intent = null; }

    /// <summary>The bot picked up <paramref name="count"/> × <paramref name="itemId"/>: held when an intent is open.</summary>
    public bool OnPickup(int itemId, int count)
    {
        if (itemId <= 0 || count <= 0) return false;
        var now = _time.GetUtcNow();
        string forWhom;
        lock (_lock)
        {
            if (_intent is not { } i || i.Until <= now) return false;
            if (i.ItemId is { } want && want != itemId) return false;
            forWhom = i.For;
            Reserve(itemId, count, forWhom, now);
        }
        Log?.Invoke($"hand-off: reserved {Items.DisplayName(itemId, 0)} x{count} for {forWhom}");
        return true;
    }

    /// <summary>Hold <paramref name="count"/> × <paramref name="itemId"/> for <paramref name="forWhom"/> (a give-to-clone job).</summary>
    public void Reserve(int itemId, int count, string forWhom)
    {
        lock (_lock) Reserve(itemId, count, forWhom, _time.GetUtcNow());
    }

    private void Reserve(int itemId, int count, string forWhom, DateTimeOffset now)
    {
        var h = _holds.FirstOrDefault(x => x.ItemId == itemId && x.For.Equals(forWhom, StringComparison.OrdinalIgnoreCase));
        if (h == null) _holds.Add(new Hold(itemId, count, forWhom, now + HoldFor));
        else { h.Count += count; h.Until = now + HoldFor; }
    }

    /// <summary>
    /// Keeps holds in line with the inventory: an item that left it (delivered, deposited) shrinks or ends its hold; expired
    /// holds end. Returns the ended holds ("bread x1 for 67 delivered").
    /// </summary>
    public IReadOnlyList<string> Reconcile(Func<int, int> countInInventory)
    {
        var now = _time.GetUtcNow();
        var done = new List<string>();
        lock (_lock)
        {
            foreach (var g in _holds.GroupBy(h => h.ItemId).ToList())
            {
                int have = countInInventory(g.Key);
                int held = g.Sum(h => h.Count);
                if (have >= held) continue;
                int gone = held - have;
                foreach (var h in g.OrderBy(h => h.Until))
                {
                    int take = Math.Min(gone, h.Count);
                    h.Count -= take;
                    gone -= take;
                    if (h.Count == 0) done.Add($"{Items.DisplayName(h.ItemId, 0)} for {h.For} handed over");
                    if (gone == 0) break;
                }
            }
            foreach (var h in _holds.Where(h => h.Count > 0 && h.Until <= now)) done.Add($"{Items.DisplayName(h.ItemId, 0)} x{h.Count} for {h.For} no longer held (timeout)");
            _holds.RemoveAll(h => h.Count <= 0 || h.Until <= now);
            if (_intent is { } i && i.Until <= now) _intent = null;
        }
        foreach (var d in done) Log?.Invoke("hand-off: " + d);
        return done;
    }

    /// <summary>
    /// Item ids auto-eat must skip right now: every id whose whole stock in the inventory is held for someone. Empty at
    /// critical health (≤ <see cref="CriticalHp"/>).
    /// </summary>
    public IReadOnlySet<int> Blocked(int health, Func<int, int> countInInventory)
    {
        if (health <= CriticalHp) return EmptySet;
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_holds.Count == 0) return EmptySet;
            var set = new HashSet<int>();
            foreach (var g in _holds.Where(h => h.Until > now).GroupBy(h => h.ItemId))
                if (countInInventory(g.Key) <= g.Sum(h => h.Count)) set.Add(g.Key);
            return set;
        }
    }

    /// <summary>"bread x1 for 67" per hold (header/status), empty = nothing held.</summary>
    public IReadOnlyList<string> Describe()
    {
        var now = _time.GetUtcNow();
        lock (_lock) return _holds.Where(h => h.Until > now && h.Count > 0).Select(h => $"{Items.DisplayName(h.ItemId, 0)} x{h.Count} for {h.For}").ToList();
    }

    public int HeldCount(int itemId)
    {
        var now = _time.GetUtcNow();
        lock (_lock) return _holds.Where(h => h.ItemId == itemId && h.Until > now).Sum(h => h.Count);
    }

    private static readonly IReadOnlySet<int> EmptySet = new HashSet<int>();
}
