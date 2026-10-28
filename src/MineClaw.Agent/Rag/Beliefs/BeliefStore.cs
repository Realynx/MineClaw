using System.Runtime.CompilerServices;
using MineClaw.Agent.Memory;
using MineClaw.Agent.Self;
using MineClaw.Agent.Tools;
using Microsoft.Data.Sqlite;

namespace MineClaw.Agent.Rag.Beliefs;

/// <summary>
/// The belief system's claims store (SQLite tables from AgentDb v4). Every player-sourced factual statement the bot chooses
/// to remember is a claim with a status, never a fact. Status comes only from evidence in ground-truth order (seed/jar data
/// &gt; own observation/experiment &gt; observed demonstration &gt; claimant agreement &gt; one claim); trust signals go to
/// <see cref="SelfGrowth.RecordClaimOutcome"/> once per claimant and outcome. Each non-directive claim is also a RAG doc
/// (kind <see cref="DocKind"/>, body = its compact rendered line) so auto-retrieval, recall and ask_memory show its status.
/// Test-driven turns are never stored. Thread-safe (serialized on the db gate).
/// </summary>
public sealed class BeliefStore
{
    public const string DocKind = "claim";
    private static readonly ConditionalWeakTable<AgentDb, BeliefStore> Stores = new();

    private readonly AgentDb _db;
    private readonly RagIndex? _index;
    private ObservationStore? _obs;

    public BeliefStore(AgentDb db, RagIndex? index, TimeProvider? time = null, BeliefOptions? options = null)
    {
        _db = db;
        _index = index;
        Time = time ?? TimeProvider.System;
        Options = options ?? new BeliefOptions();
    }

    /// <summary>The store shared by everything on this database (memory tools, where_is, the Bot verifier).</summary>
    public static BeliefStore For(MemoryStore memory) => For(memory.Db, memory.Index);

    public static BeliefStore For(AgentDb db, RagIndex? index)
    {
        lock (Stores)
        {
            if (Stores.TryGetValue(db, out var s)) return s;
            s = new BeliefStore(db, index);
            Stores.Add(db, s);
            return s;
        }
    }

    public static BeliefStore? Find(AgentDb db) => Stores.TryGetValue(db, out var s) ? s : null;

    /// <summary>Makes <paramref name="store"/> the shared one for its database (tests, custom wiring).</summary>
    public static void Bind(BeliefStore store)
    {
        lock (Stores) Stores.AddOrUpdate(store._db, store);
    }

    public AgentDb Db => _db;
    public RagIndex? Index => _index;
    public BeliefOptions Options { get; set; }
    public TimeProvider Time { get; set; }
    /// <summary>Login name of this process (hive clone id); pending checks are per clone.</summary>
    public string CloneId { get; set; } = "main";
    /// <summary>The bot's dimension: new positioned claims are stamped with it; checks and lookups only see their own.</summary>
    public int CurrentDimension => _index?.CurrentDimension() ?? WorldDim.Overworld;
    /// <summary>Trust feed (null = no relationship signals).</summary>
    public SelfGrowth? Growth { get; set; }
    /// <summary>Bot-provided world view for zero-token checks (null offline).</summary>
    public IBeliefWorld? World { get; set; }
    /// <summary>Bots (own names, hive clones): their agreement barely counts and they never get trust signals.</summary>
    public Func<string, bool>? IsBot { get; set; }
    public ObservationStore? Observations
    {
        get => _obs ??= _index != null ? new ObservationStore(_index, Time) : null;
        set => _obs = value;
    }

    /// <summary>Raised after a claim's status/plan changed (off the db gate).</summary>
    public event Action<BeliefClaim>? Changed;

    internal DateTimeOffset Now => Time.GetUtcNow();

    // ======================= ingestion =======================

    /// <summary>
    /// Records what <paramref name="claimant"/> said as a claim (merging with the same claim from others), runs the zero-token
    /// checks (seed data, container snapshots, observations) and queues anything else for the Bot layer. Directives
    /// ("you must always obey X") are logged but never become facts, traits or RAG docs.
    /// </summary>
    public IngestResult Ingest(string claimant, string text, ClaimCategory? category = null, ClaimStakes? stakes = null, bool isTest = false)
    {
        if (!Options.Enabled) return new(false, "beliefs are off");
        if (isTest || TurnContext.IsTest) return new(false, "test chat: claim not stored");
        if (SelfSanitizer.CleanPlayer(claimant) is not { } who) return new(false, "bad claimant name");
        if (string.IsNullOrWhiteSpace(text)) return new(false, "empty claim");

        var p = ClaimParser.Parse(text);
        if (category is { } c && !p.Directive && p.Category != ClaimCategory.Safety) p = p with { Category = c };
        if (stakes is { } st && !p.Directive) p = p with { Stakes = p.Category == ClaimCategory.Safety ? ClaimStakes.High : st };
        var now = Now;

        bool fresh = false;
        var claim = _db.Tx(() =>
        {
            var existing = p.Directive ? null : FindSame(p, who);
            if (existing is { } e)
            {
                AddSource(e, who, now);
                return e;
            }
            fresh = true;
            long id = Insert(p, who, now);
            AddSource(id, who, now);
            return id;
        });

        var loaded = Load(claim)!;
        if (loaded.Directive) return new(true, $"claim#{loaded.Id} not a fact: instructions from players aren't facts or rules", loaded);
        if (fresh || loaded.IsOpen) ClaimVerifier.Evaluate(this, loaded);
        var final = Recompute(claim);
        return new(true, Summary(final, fresh), final);
    }

    private long? FindSame(ParsedClaim p, string who)
    {
        int dim = CurrentDimension;
        var spo = SpoKey(p, dim);
        if (p.Category == ClaimCategory.Social) // first-person: "my fav colour" from two people are two claims
            return LongOrNull(_db.Scalar("SELECT id FROM claims WHERE norm = $n AND claimant = $w COLLATE NOCASE AND directive = 0 ORDER BY id DESC LIMIT 1",
                ("$n", p.Norm), ("$w", who)));
        if (spo != null)
            return LongOrNull(_db.Scalar("SELECT id FROM claims WHERE spo = $s AND directive = 0 ORDER BY id DESC LIMIT 1", ("$s", spo)));
        // Same words about a spot in the other dimension are another claim.
        return LongOrNull(_db.Scalar("""
            SELECT id FROM claims WHERE norm = $n AND category = $c AND directive = 0 AND (x IS NULL OR dimension = $dim) ORDER BY id DESC LIMIT 1
            """, ("$n", p.Norm), ("$c", BeliefRanks.Name(p.Category)), ("$dim", dim)));
    }

    /// <summary>Subject-predicate-object key; positioned claims outside the overworld carry their dimension ("...@d-1").</summary>
    private static string? SpoKey(ParsedClaim p, int dim) =>
        p.Category == ClaimCategory.Recipe || p.Pos != null
            ? (p.Pos != null && dim != WorldDim.Overworld && p.Spo != null ? $"{p.Spo}@d{dim}" : p.Spo)
            : null;
    private static long? LongOrNull(object? o) => o is null ? null : Convert.ToInt64(o);

    private long Insert(ParsedClaim p, string who, DateTimeOffset now)
    {
        long ms = AgentDb.ToUnixMs(now);
        _db.Exec("""
            INSERT INTO claims(claimant, text, norm, spo, subject, predicate, object, x, y, z, tx, ty, tz, category, stakes, confidence, status,
                reason, plan, risk, directive, time_based, attempts, checked, created, updated, clone_id, dimension)
            VALUES ($w, $t, $n, $spo, $s, $p, $o, $x, $y, $z, $tx, $ty, $tz, $cat, $st, 0.35, 'unverified', NULL, 'none', $risk, $d, $tb, 0, NULL, $ms, $ms, $clone, $dim)
            """, ("$w", who), ("$t", p.Text), ("$n", p.Norm), ("$spo", SpoKey(p, CurrentDimension)), ("$s", p.Subject), ("$p", p.Predicate), ("$o", p.Object),
            ("$x", p.Pos?.X), ("$y", p.Pos?.Y), ("$z", p.Pos?.Z), ("$tx", p.Target?.X), ("$ty", p.Target?.Y), ("$tz", p.Target?.Z),
            ("$cat", BeliefRanks.Name(p.Category)), ("$st", BeliefRanks.Name(p.Stakes)), ("$risk", BeliefRanks.Name(p.Risk)),
            ("$d", p.Directive ? 1 : 0), ("$tb", p.TimeBased ? 1 : 0), ("$ms", ms), ("$clone", CloneId), ("$dim", CurrentDimension));
        return _db.ScalarLong("SELECT last_insert_rowid()");
    }

    private void AddSource(long id, string who, DateTimeOffset now) =>
        _db.Exec("INSERT OR IGNORE INTO claim_sources(claim_id, claimant, at, signaled) VALUES ($i, $w, $t, NULL)",
            ("$i", id), ("$w", who), ("$t", AgentDb.ToUnixMs(now)));

    private string Summary(BeliefClaim c, bool fresh)
    {
        var s = $"claim#{c.Id} {BeliefRanks.Name(c.Status)}";
        if (c.Reason is { Length: > 0 } r) s += ": " + TextUtil.Truncate(r, Options.MaxReasonChars);
        else if (c.Category == ClaimCategory.Safety) s += ": info only, never overrides your safety rules";
        else if (c.Plan is ClaimPlan.Container or ClaimPlan.Experiment or ClaimPlan.Location) s += $" ({BeliefRanks.Name(c.Plan)} check queued)";
        if (!fresh && c.Claimants.Count > 1) s += $"; {c.Claimants.Count} people say so";
        return s;
    }

    // ======================= evidence (Bot layer + verifier) =======================

    /// <summary>Adds one piece of evidence (+1 supports, -1 contradicts, 0 inconclusive) and re-resolves the claim.</summary>
    public BeliefClaim? AddEvidence(long id, EvidenceKind kind, int polarity, double weight, string? reference, string? note)
    {
        if (Load(id) is not { } c || c.Directive) return null;
        if (TurnContext.IsTest) return c;
        note = note == null ? null : TextUtil.Truncate(TextUtil.OneLine(note), 120);
        _db.Tx(() =>
        {
            bool dup = _db.ScalarLong("""
                SELECT count(*) FROM claim_evidence WHERE claim_id = $i AND kind = $k AND coalesce(ref, '') = coalesce($r, '')
                  AND polarity = $p AND coalesce(note, '') = coalesce($n, '')
                """, ("$i", id), ("$k", BeliefRanks.Name(kind)), ("$r", reference), ("$p", Math.Sign(polarity)), ("$n", note)) > 0;
            if (!dup)
                _db.Exec("INSERT INTO claim_evidence(claim_id, kind, ref, polarity, weight, note, at) VALUES ($i, $k, $r, $p, $w, $n, $t)",
                    ("$i", id), ("$k", BeliefRanks.Name(kind)), ("$r", reference), ("$p", Math.Sign(polarity)), ("$w", Math.Clamp(weight, 0, 1)),
                    ("$n", note), ("$t", AgentDb.ToUnixMs(Now)));
            if (BeliefRanks.Of(kind) >= 2)
                _db.Exec("UPDATE claims SET checked = $t, plan = CASE WHEN $p = 0 THEN plan ELSE 'none' END WHERE id = $i",
                    ("$t", AgentDb.ToUnixMs(Now)), ("$p", Math.Sign(polarity)), ("$i", id));
        });
        return Recompute(id);
    }

    /// <summary>Container check result (the bot opened it and looked).</summary>
    public BeliefClaim? RecordContainerContents(long id, ClaimPos at, IReadOnlyList<ItemCount> contents, string kind = "chest") =>
        ClaimVerifier.EvaluateContents(this, id, at, contents, kind);

    /// <summary>
    /// Live snapshot hook (the world indexer saw the bot look inside a container): open container claims within a block of
    /// <paramref name="anchor"/> (double chests) are judged by it. Returns how many claims it touched.
    /// </summary>
    public int ContainerSnapshot(ClaimPos anchor, IReadOnlyList<ItemCount> contents, string kind = "chest") =>
        ClaimVerifier.OnSnapshot(this, anchor, contents, kind);

    internal List<long> OpenContainerClaimsAt(ClaimPos a) =>
        _db.Query("""
            SELECT id FROM claims WHERE category = 'container' AND directive = 0 AND status IN ('unverified', 'disputed', 'provisional')
              AND y = $y AND x BETWEEN $x0 AND $x1 AND z BETWEEN $z0 AND $z1 AND dimension = $dim
            """, r => r.GetInt64(0), ("$y", a.Y), ("$x0", a.X - 1), ("$x1", a.X + 1), ("$z0", a.Z - 1), ("$z1", a.Z + 1), ("$dim", CurrentDimension));

    /// <summary>Experiment result: <paramref name="pressed"/> = the trigger was used; <paramref name="changes"/> = block changes seen after.</summary>
    public BeliefClaim? RecordExperiment(long id, bool pressed, IReadOnlyList<ObservedChange> changes) =>
        ClaimVerifier.EvaluateExperiment(this, id, pressed, changes);

    /// <summary>A taught demonstration whose outcome was observed (rank between experiments and claimant agreement).</summary>
    public BeliefClaim? RecordDemonstration(long id, bool outcomeMatched, string? note = null) =>
        AddEvidence(id, EvidenceKind.Demo, outcomeMatched ? 1 : -1, 0.8, "demo", note ?? (outcomeMatched ? "demo matched" : "demo didn't match"));

    /// <summary>Observation counter for a time-based mechanic (provisional at most).</summary>
    public BeliefClaim? RecordCounter(long id, bool supports, string? note = null)
    {
        if (Load(id) is not { } c) return null;
        _db.Exec("INSERT INTO claim_evidence(claim_id, kind, ref, polarity, weight, note, at) VALUES ($i, 'counter', NULL, $p, 0.3, $n, $t)",
            ("$i", id), ("$p", supports ? 1 : -1), ("$n", note), ("$t", AgentDb.ToUnixMs(Now)));
        return Recompute(c.Id);
    }

    /// <summary>A check couldn't run or was inconclusive; after <see cref="BeliefOptions.MaxAttempts"/> the plan is dropped.</summary>
    public BeliefClaim? MarkAttempt(long id, string? note = null)
    {
        _db.Exec("""
            UPDATE claims SET attempts = attempts + 1, checked = $t, updated = $t,
                plan = CASE WHEN attempts + 1 >= $max THEN 'none' ELSE plan END WHERE id = $i
            """, ("$t", AgentDb.ToUnixMs(Now)), ("$max", Options.MaxAttempts), ("$i", id));
        return Recompute(id);
    }

    /// <summary>Re-runs the zero-token checks (e.g. a chunk loaded or a snapshot arrived).</summary>
    public BeliefClaim? Reevaluate(long id)
    {
        if (Load(id) is not { IsOpen: true } c || c.Directive) return Load(id);
        ClaimVerifier.Evaluate(this, c);
        return Recompute(id);
    }

    internal void SetPlan(long id, ClaimPlan plan, ExperimentRisk risk, ClaimPos? pos = null)
    {
        _db.Exec("""
            UPDATE claims SET plan = $p, risk = $r, x = coalesce($x, x), y = coalesce($y, y), z = coalesce($z, z), updated = $t,
                dimension = CASE WHEN x IS NULL AND $x IS NOT NULL THEN $dim ELSE dimension END WHERE id = $i
            """, ("$p", BeliefRanks.Name(plan)), ("$r", BeliefRanks.Name(risk)), ("$x", pos?.X), ("$y", pos?.Y), ("$z", pos?.Z),
            ("$t", AgentDb.ToUnixMs(Now)), ("$i", id), ("$dim", CurrentDimension));
    }

    /// <summary>Evidence refs of admin verdicts ("admin:{who}"): replaced by the next verdict, never stacked.</summary>
    public const string AdminRefPrefix = "admin:";

    /// <summary>
    /// Admin verdict (memory admin tool). Verified / refuted / disputed are recorded as ground-truth evidence (seed rank; for
    /// safety claims an experiment, the only kind they accept) from <c>admin:{who}</c> with the reason as its note, so every
    /// later recompute keeps the verdict; <paramref name="confidence"/> = belief the claim is true (weight = it for verified,
    /// 1 − it for refuted). Unverified / provisional drop earlier admin verdicts, recompute, then pin the requested status.
    /// One transaction.
    /// </summary>
    public BeliefClaim? AdminSet(long id, ClaimStatus status, double? confidence, string? reason, string who)
    {
        if (Load(id) is not { } c || c.Directive) return null;
        who = string.IsNullOrWhiteSpace(who) ? "admin" : who.Trim();
        reason = reason == null ? null : TextUtil.Truncate(TextUtil.OneLine(reason), 120);
        return _db.Tx(() =>
        {
            _db.Exec("DELETE FROM claim_evidence WHERE claim_id = $i AND ref LIKE 'admin:%'", ("$i", id));
            if (status is ClaimStatus.Verified or ClaimStatus.Refuted or ClaimStatus.Disputed)
            {
                double conf = Math.Clamp(confidence ?? (status == ClaimStatus.Refuted ? 0.05 : status == ClaimStatus.Verified ? 0.95 : 0.5), 0, 1);
                int polarity = status switch { ClaimStatus.Verified => 1, ClaimStatus.Refuted => -1, _ => 0 };
                double weight = status == ClaimStatus.Refuted ? 1 - conf : conf;
                var kind = c.Category == ClaimCategory.Safety ? EvidenceKind.Experiment : EvidenceKind.Seed;
                _db.Exec("INSERT INTO claim_evidence(claim_id, kind, ref, polarity, weight, note, at) VALUES ($i, $k, $r, $p, $w, $n, $t)",
                    ("$i", id), ("$k", BeliefRanks.Name(kind)), ("$r", AdminRefPrefix + who), ("$p", polarity), ("$w", weight),
                    ("$n", reason ?? $"set by {who}"), ("$t", AgentDb.ToUnixMs(Now)));
                _db.Exec("UPDATE claims SET checked = $t, plan = 'none' WHERE id = $i", ("$t", AgentDb.ToUnixMs(Now)), ("$i", id));
                return Recompute(id);
            }
            var r = Recompute(id)!;
            if (r.Status == status && confidence == null && reason == null) return r;
            _db.Exec("UPDATE claims SET status = $s, confidence = $c, reason = $r, updated = $t WHERE id = $i",
                ("$s", BeliefRanks.Name(status)), ("$c", Math.Clamp(confidence ?? r.Confidence, 0, 1)), ("$r", reason ?? r.Reason),
                ("$t", AgentDb.ToUnixMs(Now)), ("$i", id));
            var after = Load(id)!;
            IndexDoc(after);
            return after;
        });
    }

    /// <summary>Deletes a claim (sources and evidence cascade) and its RAG doc. One transaction.</summary>
    public bool AdminDelete(long id)
    {
        if (Load(id) is not { } c) return false;
        return _db.Tx(() =>
        {
            _db.Exec("DELETE FROM claim_evidence WHERE claim_id = $i", ("$i", id));
            _db.Exec("DELETE FROM claim_sources WHERE claim_id = $i", ("$i", id));
            bool gone = _db.Exec("DELETE FROM claims WHERE id = $i", ("$i", id)) > 0;
            _index?.Delete(c.DocRef);
            return gone;
        });
    }

    public bool HasEvidence(long id, EvidenceKind kind) =>
        _db.ScalarLong("SELECT count(*) FROM claim_evidence WHERE claim_id = $i AND kind = $k", ("$i", id), ("$k", BeliefRanks.Name(kind))) > 0;

    // ======================= resolution =======================

    /// <summary>Re-derives status/confidence/reason from evidence + claimants, persists, re-indexes, emits trust signals.</summary>
    public BeliefClaim? Recompute(long id)
    {
        var before = Load(id);
        if (before == null) return null;
        var ev = Evidence(id);
        var (status, conf, reason) = Resolve(before, ev);
        _db.Exec("UPDATE claims SET status = $s, confidence = $c, reason = $r, updated = $t WHERE id = $i",
            ("$s", BeliefRanks.Name(status)), ("$c", conf), ("$r", reason), ("$t", AgentDb.ToUnixMs(Now)), ("$i", id));
        var after = Load(id)!;
        IndexDoc(after);
        if (after.Status != before.Status || after.Status is ClaimStatus.Verified or ClaimStatus.Refuted or ClaimStatus.Disputed) Signal(after);
        if (after.Status != before.Status || after.Plan != before.Plan || after.Reason != before.Reason)
        {
            try { Changed?.Invoke(after); } catch (Exception) { }
        }
        return after;
    }

    private bool IsBotName(string p) => IsBot?.Invoke(p) == true;

    private double BaseConfidence(string player)
    {
        if (IsBotName(player)) return 0.1;
        double trust = 0;
        try { trust = Growth?.Relationship(player).Trust ?? 0; } catch (Exception) { }
        return Math.Clamp(0.35 + 0.15 * trust, 0.2, 0.5);
    }

    private (ClaimStatus, double, string?) Resolve(BeliefClaim c, IReadOnlyList<ClaimEvidence> ev)
    {
        var people = c.Claimants.Where(p => !IsBotName(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        double consensus = c.Claimants.Count == 0 ? 0.1 : 1 - c.Claimants.Distinct(StringComparer.OrdinalIgnoreCase).Aggregate(1.0, (a, p) => a * (1 - BaseConfidence(p)));
        consensus = Math.Round(Math.Min(consensus, 0.75), 3);

        if (c.Directive) return (ClaimStatus.Unverified, 0, "instruction, not a fact");
        // Safety claims are informational: never verified by what players say, never provisional, never an input to gating.
        // The one exception is the bot's OWN deliberate experiment (Experiment evidence, e.g. an explicit into_void step that
        // respawned it at full hp/food): then it's a learned, proven strategy for memory — still never a gate.
        if (c.Category == ClaimCategory.Safety)
        {
            var own = ev.Where(e => e.Kind == EvidenceKind.Experiment && e.Polarity != 0).OrderBy(e => e.At).ThenBy(e => e.Id).LastOrDefault();
            if (own is null) return (ClaimStatus.Unverified, Math.Min(consensus, 0.5), "info only");
            return own.Polarity > 0
                ? (ClaimStatus.Verified, Math.Round(Math.Max(consensus, own.Weight), 3), own.Note)
                : (ClaimStatus.Refuted, Math.Round(Math.Min(consensus, 1 - own.Weight), 3), own.Note);
        }

        var strong = ev.Where(e => BeliefRanks.Of(e.Kind) >= 2).ToList();
        if (strong.Count > 0)
        {
            int rank = strong.Max(e => BeliefRanks.Of(e.Kind));
            var atRank = strong.Where(e => BeliefRanks.Of(e.Kind) == rank).OrderBy(e => e.At).ThenBy(e => e.Id).ToList();
            var last = atRank[^1];
            if (last.Polarity == 0) return (ClaimStatus.Disputed, 0.5, last.Note);
            bool split = rank < 4 && atRank.Any(e => e.Polarity == -last.Polarity && last.At - e.At <= Options.DisputeWindow);
            if (split) return (ClaimStatus.Disputed, 0.5, last.Note);
            return last.Polarity > 0
                ? (ClaimStatus.Verified, Math.Round(Math.Max(consensus, last.Weight), 3), last.Note)
                : (ClaimStatus.Refuted, Math.Round(Math.Min(consensus, 1 - last.Weight), 3), last.Note);
        }

        var counters = ev.Where(e => e.Kind == EvidenceKind.Counter).ToList();
        int sup = counters.Count(e => e.Polarity > 0), con = counters.Count(e => e.Polarity < 0);
        if (con >= Options.CounterSupport && sup > 0) return (ClaimStatus.Disputed, 0.5, $"{sup} for, {con} against");
        if (sup >= Options.CounterSupport && con == 0) return (ClaimStatus.Provisional, Math.Max(consensus, 0.6), $"{sup} observations agree");

        if (c.Category == ClaimCategory.Social) return (ClaimStatus.Provisional, Math.Max(consensus, 0.6), null);
        if (people.Count >= 2 && c.Stakes != ClaimStakes.High) return (ClaimStatus.Provisional, consensus, $"{people.Count} people agree");
        string? why = c.Category == ClaimCategory.Structure && c.Risk != ExperimentRisk.Safe ? "risky to test, ask first"
            : c.TimeBased ? "needs time to observe" : null;
        return (ClaimStatus.Unverified, consensus, why);
    }

    /// <summary>Trust feed: once per claimant and outcome. Social/safety/directive claims never touch trust.</summary>
    private void Signal(BeliefClaim c)
    {
        if (Growth == null || c.Directive || c.Category is ClaimCategory.Social or ClaimCategory.Safety) return;
        if (c.Status is not (ClaimStatus.Verified or ClaimStatus.Refuted or ClaimStatus.Disputed)) return;
        var outcome = c.Status switch { ClaimStatus.Verified => ClaimOutcome.Verified, ClaimStatus.Refuted => ClaimOutcome.Refuted, _ => ClaimOutcome.Disputed };
        double w = c.Stakes switch { ClaimStakes.Low => 0.4, ClaimStakes.Medium => 0.7, _ => 1.0 };
        if (outcome != ClaimOutcome.Verified) w *= 0.5; // refuted lowers trust only slightly
        var status = BeliefRanks.Name(c.Status);
        var due = _db.Query("SELECT claimant FROM claim_sources WHERE claim_id = $i AND coalesce(signaled, '') <> $s",
            r => r.GetString(0), ("$i", c.Id), ("$s", status));
        foreach (var p in due)
        {
            if (IsBotName(p)) continue;
            SelfSignal? s = null;
            try { s = Growth.RecordClaimOutcome(p, outcome, w, $"claim#{c.Id} {status}"); } catch (Exception) { }
            if (s != null)
                _db.Exec("UPDATE claim_sources SET signaled = $s WHERE claim_id = $i AND claimant = $p", ("$s", status), ("$i", c.Id), ("$p", p));
        }
    }

    // ======================= rendering + index =======================

    /// <summary>"fox says: iron in chest@2,64,0 (unverified)" / "robin says: glass from gravel (refuted: seed says sand)".</summary>
    public string Render(BeliefClaim c)
    {
        var names = c.Claimants.Count > 0 ? c.Claimants : [c.Claimant];
        var who = string.Join("+", names.Take(3).Select(Nick)) + (names.Count > 3 ? $"+{names.Count - 3}" : "");
        var verb = names.Count > 1 ? "say" : "says";
        var text = TextUtil.Truncate(TextUtil.OneLine(c.Text).TrimEnd('.', '!', ' '), Options.MaxTextChars);
        var reason = c.Reason is { Length: > 0 } r ? ": " + TextUtil.Truncate(r, Options.MaxReasonChars) : "";
        return TextUtil.Truncate($"{who} {verb}: {text} ({BeliefRanks.Name(c.Status)}{reason})", Options.MaxLineChars);
    }

    private string Nick(string player) => Options.Nicknames.TryGetValue(player, out var n) ? n : player.ToLowerInvariant();

    private void IndexDoc(BeliefClaim c)
    {
        if (_index == null || c.Directive) return;
        var line = Render(c);
        var title = $"claim {string.Join(" ", c.Claimants.Select(Nick))} {string.Join(" ", c.Claimants)} {BeliefRanks.Name(c.Category)}";
        _index.Upsert(new RagDoc(c.DocRef, DocKind, title, line)
        {
            Tag = BeliefRanks.Name(c.Category),
            Source = "claim",
            Bounds = c.Pos is { } p ? Box.Point(p.X, p.Y, p.Z) : null,
            Dimension = c.Dimension,
            Weight = c.Status switch { ClaimStatus.Verified => 1.1, ClaimStatus.Refuted => 0.85, _ => 0.9 },
            At = c.Updated,
        });
    }

    // ======================= reads =======================

    private const string Cols = """
        id, claimant, text, norm, subject, predicate, object, x, y, z, tx, ty, tz, category, stakes, confidence, status, reason,
        plan, risk, directive, time_based, attempts, checked, created, updated, clone_id, dimension
        """;

    private BeliefClaim Map(SqliteDataReader r)
    {
        ClaimPos? Pos(int i) => r.IsDBNull(i) ? null : new ClaimPos(r.GetInt32(i), r.GetInt32(i + 1), r.GetInt32(i + 2));
        return new BeliefClaim
        {
            Id = r.GetInt64(0), Claimant = r.GetString(1), Text = r.GetString(2), Norm = r.GetString(3),
            Subject = AgentDb.StrOrNull(r, 4), Predicate = AgentDb.StrOrNull(r, 5), Object = AgentDb.StrOrNull(r, 6),
            Pos = Pos(7), Target = Pos(10),
            Category = BeliefRanks.Parse(r.GetString(13), ClaimCategory.Other), Stakes = BeliefRanks.Parse(r.GetString(14), ClaimStakes.Low),
            Confidence = r.GetDouble(15), Status = BeliefRanks.Parse(r.GetString(16), ClaimStatus.Unverified), Reason = AgentDb.StrOrNull(r, 17),
            Plan = BeliefRanks.Parse(r.GetString(18), ClaimPlan.None), Risk = BeliefRanks.Parse(r.GetString(19), ExperimentRisk.Edge),
            Directive = r.GetInt64(20) != 0, TimeBased = r.GetInt64(21) != 0, Attempts = (int)r.GetInt64(22),
            Checked = AgentDb.LongOrNull(r, 23) is { } ck ? AgentDb.FromUnixMs(ck) : null,
            Created = AgentDb.FromUnixMs(r.GetInt64(24)), Updated = AgentDb.FromUnixMs(r.GetInt64(25)), CloneId = r.GetString(26),
            Dimension = (int)r.GetInt64(27),
        };
    }

    private BeliefClaim WithClaimants(BeliefClaim c) => c with
    {
        Claimants = _db.Query("SELECT claimant FROM claim_sources WHERE claim_id = $i ORDER BY at, claimant", r => r.GetString(0), ("$i", c.Id)),
    };

    public BeliefClaim? Get(long id) => Load(id);

    private BeliefClaim? Load(long id) =>
        _db.Query($"SELECT {Cols} FROM claims WHERE id = $i", Map, ("$i", id)).Select(WithClaimants).FirstOrDefault();

    public IReadOnlyList<ClaimEvidence> Evidence(long id) =>
        _db.Query("SELECT id, claim_id, kind, ref, polarity, weight, note, at FROM claim_evidence WHERE claim_id = $i ORDER BY at, id",
            r => new ClaimEvidence(r.GetInt64(0), r.GetInt64(1), BeliefRanks.Parse(r.GetString(2), EvidenceKind.Counter), AgentDb.StrOrNull(r, 3),
                (int)r.GetInt64(4), r.GetDouble(5), AgentDb.StrOrNull(r, 6), AgentDb.FromUnixMs(r.GetInt64(7))), ("$i", id));

    /// <summary>Claims waiting for a safe Bot-side check (this clone's), most recent first.</summary>
    public List<BeliefClaim> Pending(int limit = 20) =>
        _db.Query($"""
            SELECT {Cols} FROM claims WHERE plan IN ('container', 'experiment', 'location') AND risk = 'safe' AND directive = 0
              AND status IN ('unverified', 'disputed', 'provisional') AND attempts < $max AND clone_id = $c
              AND (x IS NULL OR dimension = $dim)
            ORDER BY updated DESC LIMIT $n
            """, Map, ("$max", Options.MaxAttempts), ("$c", CloneId), ("$n", limit), ("$dim", CurrentDimension)).Select(WithClaimants).ToList();

    public List<BeliefClaim> Recent(int limit = 10, bool includeDirectives = false) =>
        _db.Query($"SELECT {Cols} FROM claims WHERE ($d = 1 OR directive = 0) ORDER BY updated DESC, id DESC LIMIT $n", Map,
            ("$d", includeDirectives ? 1 : 0), ("$n", limit)).Select(WithClaimants).ToList();

    /// <summary>Open (not refuted) container claims mentioning an item, as rendered lines (where_is fallback).</summary>
    public List<string> ContainerClaims(string item, int limit = 2)
    {
        var word = item.Trim().ToLowerInvariant().Replace(' ', '_');
        if (word.Length == 0) return [];
        var resolved = ClaimParser.ResolveItem(item)?.Name;
        return _db.Query($"""
            SELECT {Cols} FROM claims WHERE category = 'container' AND directive = 0 AND status <> 'refuted'
              AND (object LIKE $w OR ($r IS NOT NULL AND object = $r) OR text LIKE $t) AND (x IS NULL OR dimension = $dim)
            ORDER BY CASE status WHEN 'verified' THEN 0 WHEN 'provisional' THEN 1 ELSE 2 END, updated DESC LIMIT $n
            """, Map, ("$w", "%" + word + "%"), ("$r", resolved), ("$t", "%" + item.Trim() + "%"), ("$n", limit), ("$dim", CurrentDimension))
            .Select(WithClaimants).Select(Render).ToList();
    }

    public int Count(bool includeDirectives = true) =>
        (int)_db.ScalarLong("SELECT count(*) FROM claims WHERE ($d = 1 OR directive = 0)", ("$d", includeDirectives ? 1 : 0));
}
