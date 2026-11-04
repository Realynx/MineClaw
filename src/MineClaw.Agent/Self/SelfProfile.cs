using System.Text;
using System.Text.Json;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Runtime;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Self;

public enum SelfSection { Trait, Like, Dislike, Opinion, Joke, Nickname, Relationship, Milestone }

public sealed class SelfEntry
{
    public long Id { get; set; }
    public SelfSection Section { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Who it's about (relationship) or who gave it (nickname).</summary>
    public string? Player { get; set; }
    public double Strength { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Reinforced { get; set; }
    public string? Reason { get; set; }
    /// <summary>Times observed (add + reinforcements): a settled trait needs several (<see cref="SelfOptions.StrongTraitObservations"/>).</summary>
    public int Observations { get; set; } = 1;
}

/// <param name="Op">add | reinforce | adjust | remove</param>
/// <param name="Old">For adjust/remove/reinforce without an id: text (or "#id") of the existing entry.</param>
public sealed record SelfChange(string Op, SelfSection Section, string? Text = null, string? Player = null, long? Id = null,
    string? Old = null, string? Reason = null);

public sealed record SelfChangeResult(bool Ok, string Message, long? EntryId = null, int? Version = null);

public sealed record SelfHistoryRow(int Version, DateTimeOffset At, string Op, string? Section, long? EntryId, string? Text,
    string? Reason, string Source, string? Episodes);

/// <summary>
/// The bot's evolving self-profile, versioned in SQLite (<c>self_profile</c> + <c>self_profile_history</c> with a full
/// snapshot per version, so any version can be restored). Entries carry a strength that is reinforced when
/// re-observed and decays slowly when not, so the personality ages gradually instead of flip-flopping. Every
/// change is also indexed as an episode doc ("when did you start saying X?"). Thread-safe.
/// Scoped (<c>main</c> | <c>hive</c> | clone username): each scope has its own entries, versions and rollback; several
/// processes may share the db (every change is one IMMEDIATE transaction; ids are allocated inside it).
/// </summary>
public sealed class SelfProfile
{
    public const string SourceTool = "tool", SourceReflection = "reflection", SourceAdmin = "admin";

    private readonly AgentDb _db;
    private readonly SelfOptions _o;
    private readonly RagIndex? _index;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly string _scope;
    private List<SelfEntry> _entries;
    private string? _refs;

    public SelfProfile(AgentDb db, SelfOptions? options = null, RagIndex? index = null, TimeProvider? time = null, string scope = SelfSchema.MainScope)
    {
        _db = db;
        _o = options ?? new SelfOptions();
        _index = index;
        _time = time ?? TimeProvider.System;
        _scope = string.IsNullOrWhiteSpace(scope) ? SelfSchema.MainScope : scope.Trim().ToLowerInvariant();
        SelfSchema.Ensure(db);
        _entries = LoadEntries();
    }

    public SelfOptions Options => _o;
    public string Scope => _scope;
    public AgentDb Db => _db;

    /// <summary>Re-reads this scope from the db (another process may have changed it, e.g. the shared hive profile).</summary>
    public void Reload() { lock (_lock) _entries = LoadEntries(); }

    /// <summary>Runs <paramref name="body"/> with this profile locked inside one transaction, on freshly loaded entries.</summary>
    public T Atomically<T>(Func<T> body)
    {
        lock (_lock) return _db.Tx(() => { _entries = LoadEntries(); return body(); });
    }

    public bool IsStrong(SelfEntry e) => e.Observations >= Math.Max(1, _o.StrongTraitObservations);
    private DateTimeOffset Now => _time.GetUtcNow();

    public IReadOnlyList<SelfEntry> Entries { get { lock (_lock) return _entries.Select(Clone).ToList(); } }

    public int Version => (int)_db.ScalarLong("SELECT coalesce(max(version), 0) FROM self_profile_history WHERE scope = $sc", ("$sc", _scope));

    public int Cap(SelfSection s) => s switch
    {
        SelfSection.Trait => _o.MaxTraits,
        SelfSection.Like => _o.MaxLikes,
        SelfSection.Dislike => _o.MaxDislikes,
        SelfSection.Opinion => _o.MaxOpinions,
        SelfSection.Joke => _o.MaxJokes,
        SelfSection.Nickname => _o.MaxNicknames,
        SelfSection.Relationship => _o.MaxRelationships,
        _ => _o.MaxMilestones,
    };

    public static string Name(SelfSection s) => s.ToString().ToLowerInvariant();

    /// <summary>Section words the brain and admins use, after trimming, lower-casing and dropping trailing 's'.</summary>
    private static readonly Dictionary<string, SelfSection> SectionWords = new()
    {
        ["quirk"] = SelfSection.Trait, ["trait"] = SelfSection.Trait,
        ["like"] = SelfSection.Like,
        ["dislike"] = SelfSection.Dislike,
        ["opinion"] = SelfSection.Opinion,
        ["joke"] = SelfSection.Joke, ["catchphrase"] = SelfSection.Joke, ["running_joke"] = SelfSection.Joke,
        ["nickname"] = SelfSection.Nickname,
        ["relationship"] = SelfSection.Relationship, ["people"] = SelfSection.Relationship, ["player"] = SelfSection.Relationship,
        ["milestone"] = SelfSection.Milestone, ["memory"] = SelfSection.Milestone, ["memorie"] = SelfSection.Milestone,
    };

    public static bool TryParseSection(string? s, out SelfSection section)
    {
        section = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var k = s.Trim().ToLowerInvariant().TrimEnd('s');
        if (SectionWords.TryGetValue(k, out section)) return true;
        return Enum.TryParse(s, true, out section);
    }

    // ---------------- changes ----------------

    /// <param name="sourceRefs">Evidence refs recorded in history instead of <paramref name="episodes"/> (e.g. "s12,s40").</param>
    public SelfChangeResult Apply(SelfChange c, string source, IReadOnlyList<long>? episodes = null, string? sourceRefs = null)
    {
        lock (_lock)
            return _db.Tx(() =>
            {
                _refs = sourceRefs;
                try { return ApplyCore(c, source, episodes); }
                finally { _refs = null; }
            });
    }

    private SelfChangeResult ApplyCore(SelfChange c, string source, IReadOnlyList<long>? episodes)
    {
        var op = c.Op.Trim().ToLowerInvariant();
        if (CheckChange(c, op, source, out var reason, out var player) is { } refused) return refused;
        return op switch
        {
            "add" => AddEntry(c, player, reason, source, episodes),
            "reinforce" => Find(c) is { } found ? Reinforce(found, reason, source, episodes) : new(false, "no such entry"),
            "adjust" => AdjustEntry(c, player, reason, source, episodes),
            "remove" => RemoveEntry(c, reason, source, episodes),
            _ => new(false, "op must be add, reinforce, adjust or remove"),
        };
    }

    /// <summary>Refuses a change without a reason (unless admin), with a bad player name, or a relationship without a player.</summary>
    private static SelfChangeResult? CheckChange(SelfChange c, string op, string source, out string? reason, out string? player)
    {
        reason = c.Reason is { Length: > 0 } r ? TextUtil.Truncate(TextUtil.OneLine(r), 120) : null;
        player = null;
        if (reason == null && source != SourceAdmin) return new(false, "give a one-line reason");
        player = c.Player != null ? SelfSanitizer.CleanPlayer(c.Player) : null;
        if (c.Player != null && player == null) return new(false, "bad player name");
        if (c.Section == SelfSection.Relationship && player == null && op == "add") return new(false, "relationship needs a player");
        return null;
    }

    private SelfChangeResult AddEntry(SelfChange c, string? player, string? reason, string source, IReadOnlyList<long>? episodes)
    {
        var text = SelfSanitizer.Clean(c.Text, _o.MaxEntryChars, _o.BlockedWords, out var why);
        if (text == null) return new(false, "rejected: " + why);
        var similar = FindSimilar(c.Section, player, text);
        if (similar != null) return Reinforce(similar, reason, source, episodes);

        var inSection = _entries.Where(e => e.Section == c.Section).ToList();
        SelfEntry? evicted = null;
        if (inSection.Count >= Cap(c.Section))
        {
            var weakest = inSection.OrderBy(e => e.Strength).First();
            if (weakest.Strength >= _o.InitialStrength)
                return new(false, $"{Name(c.Section)} full; remove or adjust one (weakest #{weakest.Id} '{weakest.Text}')");
            evicted = weakest;
            _entries.Remove(weakest);
        }
        var now = Now;
        var e = new SelfEntry
        {
            Id = NextId(), Section = c.Section, Text = text, Player = player, Strength = _o.InitialStrength,
            Created = now, Reinforced = now, Reason = reason,
        };
        _entries.Add(e);
        var v = Persist("add", e, reason, source, episodes, evicted != null ? $" (replaced '{evicted.Text}')" : "");
        return new(true, $"added {Name(e.Section)} #{e.Id}: {e.Text}", e.Id, v);
    }

    private SelfChangeResult AdjustEntry(SelfChange c, string? player, string? reason, string source, IReadOnlyList<long>? episodes)
    {
        var e = Find(c);
        if (e == null) return new(false, "no such entry");
        var text = SelfSanitizer.Clean(c.Text, _o.MaxEntryChars, _o.BlockedWords, out var why);
        if (text == null) return new(false, "rejected: " + why);
        var before = e.Text;
        e.Text = text;
        if (player != null) e.Player = player;
        e.Strength = Math.Min(1, e.Strength + (1 - e.Strength) * _o.ReinforceRate * 0.3);
        e.Reinforced = Now;
        e.Reason = reason ?? e.Reason;
        var v = Persist("adjust", e, reason, source, episodes, $" (was '{before}')");
        return new(true, $"adjusted #{e.Id}: {e.Text}", e.Id, v);
    }

    private SelfChangeResult RemoveEntry(SelfChange c, string? reason, string source, IReadOnlyList<long>? episodes)
    {
        var e = Find(c);
        if (e == null) return new(false, "no such entry");
        _entries.Remove(e);
        var v = Persist("remove", e, reason, source, episodes, "");
        return new(true, $"removed #{e.Id}", e.Id, v);
    }

    private SelfChangeResult Reinforce(SelfEntry e, string? reason, string source, IReadOnlyList<long>? episodes)
    {
        e.Strength = Math.Min(1, e.Strength + (1 - e.Strength) * _o.ReinforceRate);
        e.Observations++;
        e.Reinforced = Now;
        var v = Persist("reinforce", e, reason, source, episodes, "");
        return new(true, $"reinforced #{e.Id} ({e.Strength:0.00}): {e.Text}", e.Id, v);
    }

    private SelfEntry? Find(SelfChange c)
    {
        if (c.Id is { } id) return _entries.FirstOrDefault(e => e.Id == id);
        var old = c.Old ?? c.Text;
        if (string.IsNullOrWhiteSpace(old)) return null;
        if (old.Trim().StartsWith('#') && long.TryParse(old.Trim()[1..], out var hid)) return _entries.FirstOrDefault(e => e.Id == hid);
        return _entries.Where(e => e.Section == c.Section).FirstOrDefault(e => Similar(e.Text, old))
               ?? _entries.FirstOrDefault(e => Similar(e.Text, old));
    }

    private SelfEntry? FindSimilar(SelfSection s, string? player, string text) =>
        _entries.FirstOrDefault(e => e.Section == s &&
            (s != SelfSection.Relationship || string.Equals(e.Player, player, StringComparison.OrdinalIgnoreCase)) && Similar(e.Text, text));

    private static HashSet<string> Words(string s) =>
        s.ToLowerInvariant().Split([' ', ',', '.', ';', ':', '!', '?', '\'', '"', '-'], StringSplitOptions.RemoveEmptyEntries)
         .Where(w => w.Length > 2).ToHashSet();

    internal static bool Similar(string a, string b)
    {
        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        var wa = Words(a);
        var wb = Words(b);
        if (wa.Count == 0 || wb.Count == 0) return false;
        double inter = wa.Intersect(wb).Count();
        return inter / wa.Union(wb).Count() >= 0.6;
    }

    /// <summary>Applies slow decay for the time since the last decay run; drops entries that fall below the floor.</summary>
    public int Decay()
    {
        lock (_lock)
        {
            var now = Now;
            var key = SelfSchema.MetaKey("self_last_decay", _scope);
            var lastS = _db.GetMeta(key);
            var last = lastS != null && DateTimeOffset.TryParse(lastS, out var l) ? l : now;
            double days = (now - last).TotalDays;
            if (lastS == null || days < 0.25)
            {
                if (lastS == null) _db.SetMeta(key, now.ToString("O"));
                return 0;
            }
            var dropped = DecayEntries(now, days);
            foreach (var d in dropped) _entries.Remove(d);
            _db.Tx(() =>
            {
                foreach (var e in _entries) _db.Exec("UPDATE self_profile SET strength = $s WHERE id = $id", ("$s", e.Strength), ("$id", e.Id));
                foreach (var d in dropped)
                {
                    _db.Exec("DELETE FROM self_profile WHERE id = $id", ("$id", d.Id));
                    WriteHistory("faded", d, "not reinforced for a long time", "decay", null);
                }
                _db.SetMeta(key, now.ToString("O"));
            });
            return dropped.Count;
        }
    }

    /// <summary>Weakens every entry past its grace day by <paramref name="days"/> of decay (milestones at half rate); returns the faded ones.</summary>
    private List<SelfEntry> DecayEntries(DateTimeOffset now, double days)
    {
        var dropped = new List<SelfEntry>();
        foreach (var e in _entries)
        {
            if ((now - e.Reinforced).TotalDays < 1) continue; // grace period after reinforcement
            double rate = e.Section == SelfSection.Milestone ? _o.DecayPerDay / 2 : _o.DecayPerDay;
            e.Strength *= Math.Pow(1 - rate, days);
            if (e.Strength < _o.MinStrength) dropped.Add(e);
        }
        return dropped;
    }

    // ---------------- admin ----------------

    /// <summary>Undo the most recent change (any source except decay) by restoring the version before it.</summary>
    public SelfChangeResult Undo(string by)
    {
        lock (_lock)
        {
            var last = _db.Query("SELECT version FROM self_profile_history WHERE source <> 'decay' AND scope = $sc ORDER BY version DESC LIMIT 1",
                r => (int)r.GetInt64(0), ("$sc", _scope)).FirstOrDefault();
            if (last == 0) return new(false, "nothing to undo");
            var prev = _db.Query("SELECT version FROM self_profile_history WHERE version < $v AND scope = $sc ORDER BY version DESC LIMIT 1",
                r => (int)r.GetInt64(0), ("$v", last), ("$sc", _scope)).FirstOrDefault();
            return RestoreVersion(prev, "undo", by, $"undid v{last}");
        }
    }

    /// <summary>Restore the profile as it was right after <paramref name="version"/> (0 = empty).</summary>
    public SelfChangeResult Rollback(int version, string by)
    {
        lock (_lock)
        {
            if (version < 0 || version > Version) return new(false, "no such version");
            if (version > 0 && _db.ScalarLong("SELECT count(*) FROM self_profile_history WHERE version = $v AND scope = $sc", ("$v", version), ("$sc", _scope)) == 0)
                return new(false, $"v{version} belongs to another personality scope");
            return RestoreVersion(version, "rollback", by, $"rolled back to v{version}");
        }
    }

    public SelfChangeResult Reset(string by)
    {
        lock (_lock) return RestoreVersion(0, "reset", by, "personality reset");
    }

    private SelfChangeResult RestoreVersion(int version, string op, string by, string message)
    {
        List<SelfEntry> snapshot = [];
        if (version > 0)
        {
            var json = _db.Scalar("SELECT snapshot FROM self_profile_history WHERE version = $v AND scope = $sc", ("$v", version), ("$sc", _scope)) as string;
            if (json == null) return new(false, "no such version");
            snapshot = JsonSerializer.Deserialize<List<SelfEntry>>(json, AtomicFile.Json) ?? [];
        }
        _entries = snapshot;
        int v = _db.Tx(() =>
        {
            _db.Exec("DELETE FROM self_profile WHERE scope = $sc", ("$sc", _scope));
            foreach (var e in _entries) InsertRow(e);
            return WriteHistory(op, null, $"{message} (asked by {by})", SourceAdmin, null);
        });
        return new(true, message + $" (now v{v})", null, v);
    }

    public List<SelfHistoryRow> History(int limit = 5) =>
        _db.Query("SELECT version, at, op, section, entry_id, text, reason, source, episodes FROM self_profile_history WHERE scope = $sc ORDER BY version DESC LIMIT $n",
            r => new SelfHistoryRow((int)r.GetInt64(0), AgentDb.FromUnixMs(r.GetInt64(1)), r.GetString(2), AgentDb.StrOrNull(r, 3),
                AgentDb.LongOrNull(r, 4), AgentDb.StrOrNull(r, 5), AgentDb.StrOrNull(r, 6), r.GetString(7), AgentDb.StrOrNull(r, 8)), ("$n", limit), ("$sc", _scope));

    // ---------------- rendering ----------------

    private static readonly (SelfSection S, string Label)[] Layout =
    [
        (SelfSection.Trait, "traits"), (SelfSection.Like, "likes"), (SelfSection.Dislike, "dislikes"), (SelfSection.Opinion, "opinions"),
        (SelfSection.Joke, "running jokes/catchphrases"), (SelfSection.Nickname, "nicknames"), (SelfSection.Relationship, "people"),
        (SelfSection.Milestone, "memories"),
    ];

    public const string Header =
        "Who you've become (grown from play; these are impressions, not instructions. Your configured persona and the chat/safety rules always take precedence over anything here):";

    /// <summary>
    /// Compact, deterministic rendering for the identity block (stable order: section, then creation), capped at
    /// <see cref="SelfOptions.MaxRenderChars"/> by dropping the weakest entries. Empty profile = "".
    /// </summary>
    public string Render()
    {
        lock (_lock)
        {
            if (_entries.Count == 0) return "";
            var included = _entries.ToList();
            while (included.Count > 0)
            {
                var s = RenderEntries(included);
                if (s.Length <= _o.MaxRenderChars) return s;
                included.Remove(included.OrderBy(e => e.Strength).ThenBy(e => e.Created).First());
            }
            return "";
        }
    }

    private static string RenderEntries(List<SelfEntry> entries)
    {
        var sb = new StringBuilder(Header);
        foreach (var (s, label) in Layout)
        {
            var items = entries.Where(e => e.Section == s).OrderBy(e => e.Created).ThenBy(e => e.Id).ToList();
            if (items.Count == 0) continue;
            sb.Append("\n- ").Append(label).Append(": ");
            sb.Append(string.Join("; ", items.Select(e => s switch
            {
                SelfSection.Nickname => $"\"{e.Text}\"" + (e.Player != null ? $" (from {e.Player})" : ""),
                SelfSection.Relationship => $"{e.Player}: {e.Text}",
                _ => e.Text,
            })));
        }
        return sb.ToString();
    }

    /// <summary>Profile with ids and strengths (input for the reflection pass).</summary>
    public string RenderForReflection()
    {
        lock (_lock)
        {
            if (_entries.Count == 0) return "(empty)";
            return string.Join("\n", _entries.OrderBy(e => e.Section).ThenBy(e => e.Id).Select(e =>
                $"#{e.Id} {Name(e.Section)}{(e.Player != null ? " [" + e.Player + "]" : "")}: {e.Text} (s {e.Strength:0.00}, {e.Observations} obs)"));
        }
    }

    // ---------------- persistence ----------------

    private long NextId() => Math.Max(_entries.Count == 0 ? 0 : _entries.Max(e => e.Id),
        Math.Max(_db.ScalarLong("SELECT coalesce(max(entry_id), 0) FROM self_profile_history"), _db.ScalarLong("SELECT coalesce(max(id), 0) FROM self_profile"))) + 1;

    private int Persist(string op, SelfEntry e, string? reason, string source, IReadOnlyList<long>? episodes, string extra)
    {
        return _db.Tx(() =>
        {
            _db.Exec("DELETE FROM self_profile WHERE scope = $sc", ("$sc", _scope));
            foreach (var x in _entries) InsertRow(x);
            return WriteHistory(op, e, (reason ?? "") + extra, source, episodes);
        });
    }

    private void InsertRow(SelfEntry e) =>
        _db.Exec("""
            INSERT INTO self_profile(id, section, player, text, strength, created, reinforced, reason, scope, observations)
            VALUES ($i, $s, $p, $t, $st, $c, $r, $re, $sc, $ob)
            """,
            ("$i", e.Id), ("$s", Name(e.Section)), ("$p", e.Player), ("$t", e.Text), ("$st", e.Strength),
            ("$c", AgentDb.ToUnixMs(e.Created)), ("$r", AgentDb.ToUnixMs(e.Reinforced)), ("$re", e.Reason), ("$sc", _scope), ("$ob", Math.Max(1, e.Observations)));

    private int WriteHistory(string op, SelfEntry? e, string? reason, string source, IReadOnlyList<long>? episodes)
    {
        var now = Now;
        var snapshot = JsonSerializer.Serialize(_entries, AtomicFile.Json);
        _db.Exec("""
            INSERT INTO self_profile_history(at, op, section, entry_id, text, reason, source, episodes, snapshot, scope)
            VALUES ($a, $o, $s, $e, $t, $r, $src, $ep, $snap, $sc)
            """, ("$a", AgentDb.ToUnixMs(now)), ("$o", op), ("$s", e != null ? Name(e.Section) : null), ("$e", e?.Id), ("$t", e?.Text),
            ("$r", reason), ("$src", source), ("$ep", _refs ?? (episodes is { Count: > 0 } ? string.Join(",", episodes) : null)), ("$snap", snapshot), ("$sc", _scope));
        int version = (int)_db.ScalarLong("SELECT last_insert_rowid()");

        // Searchable: "when did you start saying X?"
        if (_index != null)
        {
            var what = e != null
                ? $"{op} {Name(e.Section)} '{e.Text}'" + (e.Player != null ? $" ({e.Player})" : "")
                : op;
            var who = _scope == SelfSchema.MainScope ? "" : $" [{_scope}]";
            _index.Upsert(new RagDoc($"self:{version}", "episode", "personality change", $"{now:yyyy-MM-dd HH:mm}: personality{who} {what}" +
                (string.IsNullOrWhiteSpace(reason) ? "" : $" because {reason}")) { Source = "self", At = now });
        }
        return version;
    }

    private List<SelfEntry> LoadEntries() =>
        _db.Query("SELECT id, section, player, text, strength, created, reinforced, reason, observations FROM self_profile WHERE scope = $sc ORDER BY id", r => new SelfEntry
        {
            Id = r.GetInt64(0),
            Section = TryParseSection(r.GetString(1), out var s) ? s : SelfSection.Trait,
            Player = AgentDb.StrOrNull(r, 2),
            Text = r.GetString(3),
            Strength = r.GetDouble(4),
            Created = AgentDb.FromUnixMs(r.GetInt64(5)),
            Reinforced = AgentDb.FromUnixMs(r.GetInt64(6)),
            Reason = AgentDb.StrOrNull(r, 7),
            Observations = (int)r.GetInt64(8),
        }, ("$sc", _scope));

    private static SelfEntry Clone(SelfEntry e) => new()
    {
        Id = e.Id, Section = e.Section, Text = e.Text, Player = e.Player, Strength = e.Strength, Created = e.Created, Reinforced = e.Reinforced, Reason = e.Reason,
        Observations = e.Observations,
    };
}
