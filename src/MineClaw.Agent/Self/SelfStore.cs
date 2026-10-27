using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Self;

/// <summary>A decayed aggregate value: the exponentially-decayed sum of its signals' effects at a point in time.</summary>
public readonly record struct AccValue(string Key, string Dim, double Sum, int Count);

/// <summary>One signal's current share of an aggregate (for explanations).</summary>
public readonly record struct Contribution(long SignalId, string Kind, string? Player, string? Note, double Amount, DateTimeOffset At);

/// <summary>
/// SQLite persistence of signals (<c>self_signals</c>) and their incrementally-maintained aggregates (<c>self_acc</c>:
/// decayed sums, exact for exponential decay: S(t) = S(t0)·½^((t−t0)/halflife) + new effects). Writes are short
/// IMMEDIATE transactions, so several hive processes can share the db file (WAL + busy timeout).
/// </summary>
public sealed class SelfStore
{
    private readonly AgentDb _db;
    private readonly SelfOptions _o;

    public SelfStore(AgentDb db, SelfOptions options)
    {
        _db = db;
        _o = options;
        SelfSchema.Ensure(db);
    }

    public AgentDb Db => _db;
    public SelfOptions Options => _o;

    public TimeSpan HalfLife(string family, string dim) => family switch
    {
        SignalEffects.Mood => TimeSpan.FromHours(Math.Max(0.1, _o.MoodHalfLifeHours)),
        SignalEffects.Rel when dim == "trust" => TimeSpan.FromDays(Math.Max(0.1, _o.TrustHalfLifeDays)),
        SignalEffects.Rel => TimeSpan.FromDays(Math.Max(0.1, _o.RelationshipHalfLifeDays)),
        _ => TimeSpan.FromDays(Math.Max(0.1, _o.PreferenceHalfLifeDays)),
    };

    public double DecayFactor(string family, string dim, TimeSpan age) =>
        age <= TimeSpan.Zero ? 1 : Math.Pow(0.5, age / HalfLife(family, dim));

    // ---------------- signals ----------------

    /// <summary>Stores signals and folds their effects into the aggregates, in one transaction. Returns them with ids.</summary>
    public List<SelfSignal> Record(IReadOnlyList<SelfSignal> signals)
    {
        if (signals.Count == 0) return [];
        return _db.Tx(() =>
        {
            var stored = new List<SelfSignal>(signals.Count);
            foreach (var s in signals)
            {
                _db.Exec("""
                    INSERT INTO self_signals(at, scope, clone, player, kind, subject, valence, intensity, weight, episode, note, source)
                    VALUES ($a, $sc, $c, $p, $k, $su, $v, $i, $w, $e, $n, $src)
                    """, ("$a", AgentDb.ToUnixMs(s.At)), ("$sc", s.Scope), ("$c", s.CloneId), ("$p", s.Player), ("$k", s.Kind), ("$su", s.Subject),
                    ("$v", Math.Clamp(s.Valence, -1, 1)), ("$i", Math.Clamp(s.Intensity, 0, 1)), ("$w", Math.Clamp(s.Weight, 0, 1)),
                    ("$e", s.Episode), ("$n", s.Note), ("$src", s.Source));
                var withId = s with { Id = _db.ScalarLong("SELECT last_insert_rowid()") };
                foreach (var fx in SignalEffects.Of(withId)) Accumulate(s.Scope, fx, s.At);
                stored.Add(withId);
            }
            return stored;
        });
    }

    private void Accumulate(string scope, SignalEffect fx, DateTimeOffset at)
    {
        var row = _db.Query("SELECT sum, n, updated FROM self_acc WHERE scope = $s AND family = $f AND key = $k AND dim = $d",
            r => (Sum: r.GetDouble(0), N: (int)r.GetInt64(1), Updated: AgentDb.FromUnixMs(r.GetInt64(2))),
            ("$s", scope), ("$f", fx.Family), ("$k", fx.Key), ("$d", fx.Dim)).FirstOrDefault();
        double sum = fx.Amount;
        int n = 1;
        var updated = at;
        if (row.N > 0)
        {
            if (at >= row.Updated) sum = row.Sum * DecayFactor(fx.Family, fx.Dim, at - row.Updated) + fx.Amount;
            else { sum = row.Sum + fx.Amount * DecayFactor(fx.Family, fx.Dim, row.Updated - at); updated = row.Updated; }
            n = row.N + 1;
        }
        _db.Exec("""
            INSERT INTO self_acc(scope, family, key, dim, sum, n, updated) VALUES ($s, $f, $k, $d, $v, $n, $u)
            ON CONFLICT(scope, family, key, dim) DO UPDATE SET sum = excluded.sum, n = excluded.n, updated = excluded.updated
            """, ("$s", scope), ("$f", fx.Family), ("$k", fx.Key), ("$d", fx.Dim), ("$v", sum), ("$n", n), ("$u", AgentDb.ToUnixMs(updated)));
    }

    private const string SignalColumns = "id, at, scope, clone, player, kind, subject, valence, intensity, weight, episode, note, source";

    private static SelfSignal Read(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), At = AgentDb.FromUnixMs(r.GetInt64(1)), Scope = r.GetString(2), CloneId = r.GetString(3),
        Player = AgentDb.StrOrNull(r, 4), Kind = r.GetString(5), Subject = AgentDb.StrOrNull(r, 6), Valence = r.GetDouble(7),
        Intensity = r.GetDouble(8), Weight = r.GetDouble(9), Episode = AgentDb.StrOrNull(r, 10), Note = AgentDb.StrOrNull(r, 11),
        Source = r.GetString(12),
    };

    public List<SelfSignal> Recent(string scope, DateTimeOffset? since = null, int limit = 200, string? player = null, string? kind = null) =>
        _db.Query($"""
            SELECT {SignalColumns} FROM self_signals
            WHERE scope = $s AND at >= $t AND ($p IS NULL OR player = $p COLLATE NOCASE) AND ($k IS NULL OR kind = $k)
            ORDER BY at DESC, id DESC LIMIT $n
            """, Read, ("$s", scope), ("$t", since is { } t ? AgentDb.ToUnixMs(t) : 0L), ("$p", player), ("$k", kind), ("$n", limit));

    public List<SelfSignal> ByIds(IEnumerable<long> ids)
    {
        var list = ids.Distinct().Take(64).ToList();
        if (list.Count == 0) return [];
        return _db.Query($"SELECT {SignalColumns} FROM self_signals WHERE id IN ({string.Join(",", list)})", Read);
    }

    public bool Exists(string scope, string kind, string? subject, string? player = null) =>
        _db.ScalarLong("""
            SELECT EXISTS(SELECT 1 FROM self_signals WHERE scope = $s AND kind = $k AND ($su IS NULL OR subject = $su)
              AND ($p IS NULL OR player = $p COLLATE NOCASE))
            """, ("$s", scope), ("$k", kind), ("$su", subject), ("$p", player)) != 0;

    /// <summary>Distinct episodes in which <paramref name="player"/> interacted with this scope.</summary>
    public int InteractionEpisodes(string scope, string player) =>
        (int)_db.ScalarLong("SELECT count(DISTINCT episode) FROM self_signals WHERE scope = $s AND player = $p COLLATE NOCASE",
            ("$s", scope), ("$p", player));

    public long LastSignalId(string scope) => _db.ScalarLong("SELECT coalesce(max(id), 0) FROM self_signals WHERE scope = $s", ("$s", scope));

    // ---------------- aggregates ----------------

    /// <summary>All values of one family (optionally one key), decayed to <paramref name="now"/>.</summary>
    public List<AccValue> Values(string scope, string family, DateTimeOffset now, string? key = null) =>
        _db.Query("SELECT key, dim, sum, n, updated FROM self_acc WHERE scope = $s AND family = $f AND ($k IS NULL OR key = $k)",
            r =>
            {
                var dim = r.GetString(1);
                var age = now - AgentDb.FromUnixMs(r.GetInt64(4));
                return new AccValue(r.GetString(0), dim, r.GetDouble(2) * DecayFactor(family, dim, age), (int)r.GetInt64(3));
            }, ("$s", scope), ("$f", family), ("$k", key));

    /// <summary>
    /// The signals behind one aggregate dimension, by current contribution (largest first): recomputed from the same
    /// deterministic mapping, so every value is explainable.
    /// </summary>
    public List<Contribution> Explain(string scope, string family, string key, string dim, DateTimeOffset now, int limit = 5)
    {
        string? player = family == SignalEffects.Rel ? key : null;
        var rows = _db.Query($"""
            SELECT {SignalColumns} FROM self_signals WHERE scope = $s AND ($p IS NULL OR player = $p COLLATE NOCASE)
              AND ($su IS NULL OR subject = $su) ORDER BY id DESC LIMIT 2000
            """, Read, ("$s", scope), ("$p", player), ("$su", family == SignalEffects.Pref ? key : null));
        var list = new List<Contribution>();
        foreach (var s in rows)
            foreach (var fx in SignalEffects.Of(s))
                if (fx.Family == family && fx.Key == key && fx.Dim == dim)
                    list.Add(new Contribution(s.Id, s.Kind, s.Player, s.Note, fx.Amount * DecayFactor(family, dim, now - s.At), s.At));
        return list.Where(c => Math.Abs(c.Amount) > 1e-4).OrderByDescending(c => Math.Abs(c.Amount)).ThenByDescending(c => c.SignalId).Take(limit).ToList();
    }

    // ---------------- phrases (inside-joke / nickname / catchphrase candidates) ----------------

    /// <summary>
    /// Counts a phrase once per episode; returns true exactly once, when it first reaches <paramref name="threshold"/>
    /// distinct episodes (the caller then emits a candidate signal).
    /// </summary>
    public bool NotePhrase(string scope, string player, string kind, string phrase, string episode, DateTimeOffset at, int threshold) =>
        NotePhraseCore(scope, (player ?? "").ToLowerInvariant(), kind, phrase, episode, at, threshold); // same key as Phrases()

    private bool NotePhraseCore(string scope, string player, string kind, string phrase, string episode, DateTimeOffset at, int threshold) =>
        _db.Tx(() =>
        {
            var row = _db.Query("SELECT episodes, last_episode, flagged FROM self_phrases WHERE scope = $s AND player = $p AND kind = $k AND phrase = $ph",
                r => (N: (int)r.GetInt64(0), Last: AgentDb.StrOrNull(r, 1), Flagged: r.GetInt64(2) != 0),
                ("$s", scope), ("$p", player), ("$k", kind), ("$ph", phrase)).FirstOrDefault();
            if (row.Last == episode) return false;
            int n = row.N + 1;
            bool flag = !row.Flagged && n >= threshold;
            _db.Exec("""
                INSERT INTO self_phrases(scope, player, kind, phrase, episodes, last_episode, first_at, last_at, flagged)
                VALUES ($s, $p, $k, $ph, $n, $e, $a, $a, $f)
                ON CONFLICT(scope, player, kind, phrase) DO UPDATE SET episodes = $n, last_episode = $e, last_at = $a, flagged = max(flagged, $f)
                """, ("$s", scope), ("$p", player), ("$k", kind), ("$ph", phrase), ("$n", n), ("$e", episode), ("$a", AgentDb.ToUnixMs(at)), ("$f", flag ? 1 : 0));
            return flag;
        });

    /// <summary>Flagged phrases (candidates) of a kind for a player, most recurrent first.</summary>
    public List<(string Phrase, int Episodes)> Phrases(string scope, string? player, string kind, int limit = 3) =>
        _db.Query("""
            SELECT phrase, episodes FROM self_phrases WHERE scope = $s AND ($p IS NULL OR player = $p) AND kind = $k AND flagged = 1
            ORDER BY episodes DESC, last_at DESC LIMIT $n
            """, r => (r.GetString(0), (int)r.GetInt64(1)), ("$s", scope), ("$p", player?.ToLowerInvariant()), ("$k", kind), ("$n", limit));
}
