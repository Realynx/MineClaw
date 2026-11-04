using MineClaw.Data;

namespace MineClaw.Bot.Ledger;

/// <summary>One net inventory change of one item kind, with what caused it.</summary>
/// <param name="Delta">Positive = gained, negative = gone from the inventory.</param>
/// <param name="Cause">"crafted", "tossed to PoofimaFox", "deposited", "picked up", "lost when I died", ...</param>
/// <param name="Detail">Follow-up facts ("PoofimaFox picked up 12 at 07:34:18").</param>
public sealed record LedgerEntry(DateTimeOffset At, int ItemId, int Damage, int Delta, string Cause, string? Detail = null)
{
    public string ItemName => Items.DisplayName(ItemId, Damage);

    public override string ToString() =>
        $"{At.ToLocalTime():HH:mm:ss} {(Delta > 0 ? "+" : "")}{Delta} {ItemName}: {Cause}" + (Detail is null ? "" : $" ({Detail})");
}

/// <summary>
/// Inventory ledger: every gain and loss of every item, attributed (toss/give, drop, deposit, craft, place, eat, pickup,
/// death, tool wear, server correction). Items never vanish silently: whatever leaves the inventory gets an entry, and
/// "where did the slabs go" is answered from here. Fed with full count snapshots (diffed here), cause scopes from the
/// running tool/skill, and pickup/death notes; changes settle briefly before attribution because the pickup packet and the
/// slot update arrive in either order. Thread-safe; bounded ring. Zero tokens.
/// </summary>
public sealed class InventoryLedger(TimeProvider? time = null, int capacity = 600)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _lock = new();
    private readonly List<LedgerEntry> _entries = new();
    private readonly List<(DateTimeOffset At, int Id, int Damage, int Delta, string? Scope, string? Action, string? Container)> _pending = new();
    private readonly List<(DateTimeOffset At, int Id, int Count)> _pickups = new();
    private readonly List<(DateTimeOffset At, int Id, bool Auto)> _meals = new();
    private readonly List<(long Token, string Cause)> _scopes = new();
    private Dictionary<(int Id, int Damage), int>? _last;
    private DateTimeOffset? _diedAt;
    private string? _diedWhere;
    private DateTimeOffset? _resyncUntil;
    private const string Correction = "server correction";
    private static bool IsBucket(int id) => id is Items.Bucket or Items.WaterBucket or Items.LavaBucket or Items.MilkBucket;
    private long _nextToken;

    /// <summary>How long a change waits for its pickup/death note before it is attributed.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(300);
    /// <summary>Same item, same cause, same direction within this window = one entry.</summary>
    public TimeSpan MergeWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The client's running action ("craft", "deposit", "place", "goto", "click"...), the fallback cause.</summary>
    public Func<string?>? CurrentAction { get; set; }

    /// <summary>The host's flush timer (kept alive with the ledger).</summary>
    public IDisposable? Pump { get; set; }

    /// <summary>Raised for every new (or grown) entry: the host logs it.</summary>
    public event Action<LedgerEntry>? Recorded;

    // ------------------------------------------------------------------ inputs

    /// <summary>A full snapshot of the inventory's counts (player slots + cursor). The first one is the baseline.</summary>
    public void Observe(IReadOnlyDictionary<(int Id, int Damage), int> counts)
    {
        var now = _time.GetUtcNow();
        string? action = SafeAction();
        string? container = action == "deposit" ? SafeContainer() : null;
        lock (_lock)
        {
            if (_last is null) { _last = new(counts); return; }
            bool correction = _resyncUntil is { } until && now <= until;
            _resyncUntil = null;
            string? scope = correction ? Correction : _scopes.Count > 0 ? _scopes[^1].Cause : null;
            // 1.12.2 answers every item use with the whole inventory (PlayerInteractionManager.processRightClick →
            // sendContainerToPlayer): a bucket that filled or emptied arrives as a resync. An empty and a full bucket trading places
            // there is that use, not a correction of a wrong model (live 11-04: "server correction" for every pour and fill).
            var keys = _last.Keys.Union(counts.Keys).ToList();
            int Delta((int Id, int Damage) k) => counts.GetValueOrDefault(k) - _last.GetValueOrDefault(k);
            bool bucketUse = correction && keys.Any(k => IsBucket(k.Id) && Delta(k) != 0) && keys.Where(k => IsBucket(k.Id)).Sum(Delta) == 0;
            foreach (var key in keys)
            {
                int d = Delta(key);
                if (d == 0) continue;
                var why = bucketUse && IsBucket(key.Id) ? (d < 0 ? "used (a bucket filled or emptied)" : "a bucket filled or emptied") : scope;
                _pending.Add((now, key.Id, key.Damage, d, why, action, container));
            }
            _last = new(counts);
        }
    }

    /// <summary>Changes until disposal are caused by <paramref name="cause"/> (innermost scope wins).</summary>
    public IDisposable Scope(string cause)
    {
        long token;
        lock (_lock) { token = ++_nextToken; _scopes.Add((token, cause)); }
        return new ScopeHandle(this, token);
    }

    /// <summary>The bot itself picked up an item entity (0x16 collect).</summary>
    public void NotePickup(int itemId, int count)
    {
        lock (_lock) _pickups.Add((_time.GetUtcNow(), itemId, count));
    }

    /// <summary>The bot started eating <paramref name="itemId"/> (<paramref name="auto"/> = the survival reflex): the next loss of
    /// one of it is "ate (reflex)" / "eaten", even when the slot update lands after the eat action ended.</summary>
    public void NoteEat(int itemId, bool auto)
    {
        lock (_lock) _meals.Add((_time.GetUtcNow(), itemId, auto));
    }

    /// <summary>Another player picked up an item entity: credited to a recent toss/drop of that item.</summary>
    public void NoteOtherPickup(string player, int itemId, int count)
    {
        var now = _time.GetUtcNow();
        LedgerEntry? updated = null;
        lock (_lock)
        {
            Flush(now, force: true);
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (now - e.At > TimeSpan.FromMinutes(5)) break;
                if (e.ItemId != itemId || e.Delta >= 0 || !(e.Cause.StartsWith("tossed", StringComparison.Ordinal) || e.Cause.StartsWith("dropped", StringComparison.Ordinal))) continue;
                string note = $"{player} picked up {count} at {now.ToLocalTime():HH:mm:ss}";
                updated = _entries[i] = e with { Detail = e.Detail is null ? note : e.Detail + "; " + note };
                break;
            }
        }
        if (updated is not null) Recorded?.Invoke(updated);
    }

    /// <summary>The bot died: everything that leaves the inventory now was dropped where it died.</summary>
    public void NoteDeath(string? where = null)
    {
        lock (_lock) { _diedAt = _time.GetUtcNow(); _diedWhere = where; }
    }

    /// <summary>The server is about to replace the inventory model (0x68 window contents): the very next snapshot's
    /// changes are a correction of a wrong local model (predicted or stale stacks), not a real gain/loss.</summary>
    public void NoteResync()
    {
        lock (_lock) _resyncUntil = _time.GetUtcNow() + TimeSpan.FromMilliseconds(250);
    }

    // ------------------------------------------------------------------ attribution

    /// <summary>Attributes settled changes (called by a timer and before every query).</summary>
    public void Flush()
    {
        lock (_lock) Flush(_time.GetUtcNow(), force: false);
    }

    private void Flush(DateTimeOffset now, bool force)
    {
        var raised = new List<LedgerEntry>();
        for (int i = 0; i < _pending.Count;)
        {
            var p = _pending[i];
            if (!force && now - p.At < Settle) { i++; continue; }
            _pending.RemoveAt(i);
            if (p.Scope != Correction && CounterMove(p.At, p.Id, p.Damage, p.Delta, p.Action) is { } net)
            {
                if (net.Delta != 0) raised.Add(net);
                continue;
            }
            var e = Record(new LedgerEntry(p.At, p.Id, p.Damage, p.Delta, CauseOf(p.At, p.Id, p.Delta, p.Scope, p.Action, p.Container)));
            raised.Add(e);
        }
        _pickups.RemoveAll(x => now - x.At > TimeSpan.FromSeconds(10));
        _meals.RemoveAll(x => now - x.At > TimeSpan.FromSeconds(15));
        if (raised.Count > 0 && Recorded is { } h)
            // A work item's exception ends the process (every in-process hive with it): a subscriber's failure stays its own.
            ThreadPool.QueueUserWorkItem(_ => { foreach (var e in raised) { try { h(e); } catch (Exception) { /* next entry */ } } });
    }

    private string CauseOf(DateTimeOffset at, int id, int delta, string? scope, string? action, string? container = null)
    {
        if (scope == Correction)
            return delta < 0
                ? "server correction: my inventory model listed them but the server didn't have them (gone earlier, unrecorded)"
                : "server correction: the server had more than my model showed";
        if (delta > 0)
        {
            int k = _pickups.FindIndex(x => x.Id == id && (at - x.At).Duration() <= TimeSpan.FromSeconds(1.5));
            if (k >= 0)
            {
                _pickups.RemoveAt(k);
                return scope is null && action is null ? "picked up" : $"picked up ({scope ?? action})";
            }
        }
        if (delta < 0 && scope != Correction)
        {
            // Eating takes 32 ticks (1.6 s) after it starts; the slot update may come a little later still.
            int m = _meals.FindIndex(x => x.Id == id && at >= x.At - TimeSpan.FromSeconds(0.5) && at - x.At <= TimeSpan.FromSeconds(6));
            if (m >= 0)
            {
                bool auto = _meals[m].Auto;
                _meals.RemoveAt(m);
                return auto ? "ate (reflex)" : "eaten";
            }
        }
        if (delta < 0 && _diedAt is { } died && (at - died).Duration() <= TimeSpan.FromSeconds(3))
            return "lost when I died" + (_diedWhere is { } w ? $" at {w}" : "") + " (dropped there, despawns after 5 min)";
        // A deposit inside a bigger job (the gen loop storing its haul) is a deposit, not "harvest_generator" (live 10-27: "-64
        // cobblestone: harvest_generator -62/-63/-64" while the haul went into chest 14,64,-5).
        if (action == "deposit" && (scope is null || !scope.StartsWith("deposited", StringComparison.Ordinal)))
            return delta < 0 ? $"deposited ({container ?? "chest"})" : $"gained during a deposit ({container ?? "chest"})";
        if (scope is not null) return scope;
        if (delta < 0 && Items.Get(id) is { MaxDamage: > 0 } && action is "dig" or "mine" or "chop" or "harvest" or "attack" or "spar" or "collect")
            return $"wore out ({action})";
        if (action is not null && action != "click") return ActionCause(action, delta);
        return action == "click"
            ? (delta < 0 ? "moved out of the inventory by a window click" : "moved into the inventory by a window click")
            : (delta < 0 ? "vanished without an action (server correction of my inventory model?)" : "appeared without an action (server correction?)");
    }

    private static string ActionCause(string action, int delta) => (action, delta < 0) switch
    {
        ("craft", true) => "used in crafting",
        ("craft", false) => "crafted",
        ("deposit", true) => "deposited into a chest",
        ("withdraw", false) => "taken from a chest",
        ("withdraw", true) => "put back into the chest (the rest of a stack I picked up)",
        ("place", true) or ("build", true) => "placed as a block",
        ("goto", true) or ("follow", true) => "placed while moving (bridge/pillar block)",
        ("eat", true) => "eaten",
        ("smelt", true) => "put in a furnace",
        ("collect_furnace", false) => "taken from a furnace",
        ("chop", true) => "placed by chop_tree (pillar/replant)",
        _ => $"{(delta < 0 ? "used" : "gained")} during {action}",
    };

    /// <summary>
    /// A withdraw that picks a whole stack onto the cursor and puts the rest back (or a deposit that returns part of one) moves
    /// the counted items twice: live 10-28 06:49 "+64 cobblestone: taken from a chest" then "-58 cobblestone: used during
    /// withdraw" for a take of 6 (no slab was crafted: no slab gain was booked). The return nets into the take it belongs to
    /// ("+6 taken from a chest"); null = not such a move (or nothing to net against).
    /// </summary>
    private LedgerEntry? CounterMove(DateTimeOffset at, int id, int damage, int delta, string? action)
    {
        bool withdrawBack = action == "withdraw" && delta < 0, depositBack = action == "deposit" && delta > 0;
        if (!withdrawBack && !depositBack) return null;
        for (int i = _entries.Count - 1; i >= 0 && at - _entries[i].At <= MergeWindow; i--)
        {
            var x = _entries[i];
            if (x.ItemId != id || x.Damage != damage || Math.Sign(x.Delta) == Math.Sign(delta)) continue;
            if (depositBack && !x.Cause.StartsWith("deposited", StringComparison.Ordinal)) continue;
            int left = x.Delta + delta;
            if (Math.Sign(left) != Math.Sign(x.Delta)) return null; // more came back than went: not a partial return
            if (left == 0) { _entries.RemoveAt(i); return x with { Delta = 0 }; }
            return _entries[i] = x with { Delta = left };
        }
        return null;
    }

    private LedgerEntry Record(LedgerEntry e)
    {
        if (_entries.Count > 0)
        {
            for (int i = _entries.Count - 1; i >= 0 && e.At - _entries[i].At <= MergeWindow; i--)
            {
                var x = _entries[i];
                if (x.ItemId == e.ItemId && x.Damage == e.Damage && x.Cause == e.Cause && Math.Sign(x.Delta) == Math.Sign(e.Delta))
                    return _entries[i] = x with { Delta = x.Delta + e.Delta };
            }
        }
        _entries.Add(e);
        if (_entries.Count > capacity) _entries.RemoveRange(0, _entries.Count - capacity);
        return e;
    }

    /// <summary>"chest x,y,z" of the container the current action opened (null = unknown).</summary>
    public Func<string?>? CurrentContainer { get; set; }

    private string? SafeContainer()
    {
        try { return CurrentContainer?.Invoke(); } catch (Exception) { return null; }
    }

    private string? SafeAction()
    {
        try { return CurrentAction?.Invoke(); } catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ queries

    /// <summary>
    /// What the bot gathered since <paramref name="since"/> (pickups, crafts) and where it stored things ("deposited (chest X)"):
    /// "32 cobblestone; stored 30 cobblestone in chest 14,64,-5". Null when nothing was gained.
    /// </summary>
    public string? GainsSince(DateTimeOffset since)
    {
        Flush();
        var recent = Entries.Where(e => e.At >= since).ToList();
        var got = recent.Where(e => e.Delta > 0 && (e.Cause.StartsWith("picked up", StringComparison.Ordinal) || e.Cause == "crafted"))
            .GroupBy(e => e.ItemName).Select(g => (Name: g.Key, N: g.Sum(e => e.Delta))).Where(t => t.N > 0).OrderByDescending(t => t.N).ToList();
        if (got.Count == 0) return null;
        var stored = recent.Where(e => e.Delta < 0 && e.Cause.StartsWith("deposited (", StringComparison.Ordinal))
            .GroupBy(e => (e.ItemName, Where: e.Cause["deposited (".Length..].TrimEnd(')')))
            .Select(g => $"{-g.Sum(e => e.Delta)} {g.Key.ItemName} in {g.Key.Where}").Take(3).ToList();
        return string.Join(", ", got.Take(4).Select(t => $"{t.N} {t.Name}")) + (stored.Count > 0 ? "; stored " + string.Join(", ", stored) : "");
    }

    public IReadOnlyList<LedgerEntry> Entries
    {
        get { lock (_lock) { Flush(_time.GetUtcNow(), force: true); return _entries.ToList(); } }
    }

    /// <summary>Entries for items whose name matches <paramref name="query"/> ("slabs", "cobble slab", "logs"), newest last.</summary>
    public IReadOnlyList<LedgerEntry> History(string query) => Entries.Where(e => Matches(e, query)).ToList();

    /// <summary>
    /// "where did the slabs go": the item's recent movements, terse (≤ <paramref name="maxChars"/>), or null when the
    /// ledger has nothing on it this session.
    /// </summary>
    public string? Explain(string query, int? haveNow = null, int maxChars = 360)
    {
        var h = History(query);
        if (h.Count == 0) return null;
        var lines = h.Select(e => e.ToString()).ToList();
        string head = haveNow is { } n ? $"{h[^1].ItemName}: have {n} now. " : "";
        string body = string.Join("; ", lines);
        while (head.Length + body.Length > maxChars && lines.Count > 1)
        {
            lines.RemoveAt(0);
            body = "...; " + string.Join("; ", lines);
        }
        var text = head + body;
        return text.Length > maxChars ? text[..(maxChars - 3)] + "..." : text;
    }

    public static bool Matches(LedgerEntry e, string query) => Matches(e.ItemName, query);

    /// <summary>Loose item-name match: every query word (singular, "cobble" = cobblestone) appears in the name.</summary>
    public static bool Matches(string itemName, string query)
    {
        var name = itemName.ToLowerInvariant().Replace('_', ' ');
        var words = query.ToLowerInvariant().Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("the" or "my" or "a" or "some" or "of"))
            .Select(w => w == "cobble" ? "cobblestone" : w)
            .Select(w => w.Length > 3 && w.EndsWith("es", StringComparison.Ordinal) && !w.EndsWith("ses", StringComparison.Ordinal) && !name.Contains(w) ? w[..^1] : w)
            .Select(w => w.Length > 2 && w.EndsWith('s') && !name.Contains(w) ? w[..^1] : w)
            .ToList();
        // "cobblestone slab" vs the wire name "stone slab" (b1.8.1 slab damage 3 = cobblestone): accept either material.
        return words.Count > 0 && words.All(w => name.Contains(w) || (w == "cobblestone" && name.Contains("slab")));
    }

    private sealed class ScopeHandle(InventoryLedger ledger, long token) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (ledger._lock)
            {
                // Changes already observed keep the scope they were seen under; flush now so late slot updates still get it.
                int i = ledger._scopes.FindIndex(s => s.Token == token);
                if (i >= 0) ledger._scopes.RemoveAt(i);
            }
        }
    }
}
