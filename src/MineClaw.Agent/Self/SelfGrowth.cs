using MineClaw.Agent.Budget;
using MineClaw.Agent.Context;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Rag;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MineClaw.Agent.Self;

/// <summary>Outcome of checking a player's factual claim (fed by the belief system).</summary>
public enum ClaimOutcome { Verified, Refuted, Disputed }

/// <summary>
/// Personality growth v2 for one bot process (scope = <c>main</c> or its clone username): records deterministic signals
/// from every finished episode (zero tokens), maintains bounded decaying aggregates in C# (relationships, preferences,
/// mood), renders the compact uncached "[self]" block, grounds reflection diffs in signal ids, and for hive clones owns the
/// frozen seed quirks and promotion into the shared hive profile. The config persona stays the base; all of this is a
/// layer of impressions on top that the prompt explicitly ranks below persona and rules.
/// </summary>
public sealed partial class SelfGrowth
{
    private readonly SelfStore _store;
    private readonly SelfOptions _o;
    private readonly HiveOptions _hive;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ILlmClient? _llm;
    private readonly UsageTracker? _usage;
    private readonly Func<IEnumerable<string>> _botNames;
    private readonly string[] _actionWords;
    private readonly object _lock = new();
    private string? _lastActivity;
    private DateTimeOffset _lastActivityAt;

    /// <param name="profile">This process's own profile (its scope is the growth scope).</param>
    /// <param name="hiveProfile">The shared hive profile (clones only).</param>
    /// <param name="clone">This process's login username (recorded on every signal).</param>
    /// <param name="botNames">The bot's chat names (stripped before phrase analysis).</param>
    public SelfGrowth(SelfStore store, SelfProfile profile, string clone, HiveOptions? hive = null, SelfProfile? hiveProfile = null,
        Func<IEnumerable<string>>? botNames = null, IEnumerable<string>? actionWords = null, ILlmClient? llm = null,
        UsageTracker? usage = null, TimeProvider? time = null, ILogger? log = null)
    {
        _store = store;
        _o = store.Options;
        Profile = profile;
        HiveProfile = hiveProfile;
        Clone = clone;
        _hive = hive ?? new HiveOptions();
        _botNames = botNames ?? (() => [clone]);
        _actionWords = (actionWords ?? []).ToArray();
        _llm = llm;
        _usage = usage;
        _time = time ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    public SelfStore Store => _store;
    public SelfOptions Options => _o;
    public SelfProfile Profile { get; }
    /// <summary>Shared hive profile (null for the main bot).</summary>
    public SelfProfile? HiveProfile { get; }
    public string Scope => Profile.Scope;
    public string Clone { get; }
    public bool IsClone => Scope != SelfSchema.MainScope;
    /// <summary>The "who I am" RAPTOR subtree (null when embeddings are off).</summary>
    public SelfTree? Tree { get; set; }
    /// <summary>Optional host hook: players currently online/nearby (one relationship line each, capped).</summary>
    public Func<IEnumerable<string>>? OnlinePlayers { get; set; }
    public SelfBlockGate Gate { get; } = new();

    private DateTimeOffset Now => _time.GetUtcNow();

    // ======================= signals =======================

    /// <summary>Context hook: extract and record the signals of a committed episode (never for test-driven ones).</summary>
    public void Attach(ContextManager context) => context.Committed += e =>
    {
        try { OnEpisode(e); }
        catch (Exception ex) { _log.LogWarning(ex, "personality signal extraction failed"); }
    };

    public static string EpisodeRef(string clone, Episode e) => $"{clone}:{e.Started:yyyyMMddHHmmss}:{e.Seq}";

    /// <summary>Extracts, weights, adds novelty and recurring-phrase candidates, then stores. Returns what was stored.</summary>
    public List<SelfSignal> OnEpisode(Episode e)
    {
        if (!_o.Enabled || !_o.Signals || e.IsTest) return [];
        var x = ExtractWithActivity(e);
        var epRef = EpisodeRef(Clone, e);
        var at = e.Started;
        var raw = new List<RawSignal>(x.Signals);
        // Novelty: first times (checked before this episode's own signals are stored).
        raw.AddRange(FirstTimes(raw));

        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double W(string? p) => p == null ? 1 : weights.TryGetValue(p, out var w) ? w : weights[p] = WeightOf(p);
        var batch = raw.Select(s => ToSignal(s, epRef, at, W(s.Player))).ToList();
        AddPhraseSignals(x.Phrases, batch, epRef, at, W);
        if (batch.Count == 0) return [];
        return _store.Record(batch);
    }

    /// <summary>Signals of the episode; "good job" credits the activity of the last 15 minutes, and the newest one is remembered.</summary>
    private ExtractionResult ExtractWithActivity(Episode e)
    {
        string? last;
        lock (_lock) last = _lastActivity != null && e.Started - _lastActivityAt <= TimeSpan.FromMinutes(15) ? _lastActivity : null;
        var x = SignalExtractor.Extract(e, _botNames(), _actionWords, last);
        if (x.LastActivity != null && x.LastActivity != last)
            lock (_lock) { _lastActivity = x.LastActivity; _lastActivityAt = e.Started; }
        return x;
    }

    private List<RawSignal> FirstTimes(List<RawSignal> raw)
    {
        var firsts = new List<RawSignal>();
        foreach (var s in raw)
        {
            if (s.Player != null && _hive.IsBot(s.Player)) continue;
            if (FirstTimeOf(s, firsts) is { } f && !_store.Exists(Scope, SignalKinds.FirstTime, f.Subject) && !firsts.Any(q => q.Subject == f.Subject))
                firsts.Add(new(SignalKinds.FirstTime, s.Player, 0.5, 0.7, f.Subject, f.Note));
        }
        return firsts;
    }

    /// <summary>The first time this signal could be (meeting a player, an emote, a task type, a spar, a death, an edge save), or null.</summary>
    private (string Subject, string Note)? FirstTimeOf(RawSignal s, List<RawSignal> firsts) =>
        IsFirstMeeting(s, firsts) ? ("met:" + s.Player!.ToLowerInvariant(), $"first chat with {s.Player}") : OtherFirstTime(s);

    /// <summary>The first treatment from a player the bot never had an episode with (once per episode).</summary>
    private bool IsFirstMeeting(RawSignal s, List<RawSignal> firsts) =>
        s.Player != null && SignalKinds.IsTreatment(s.Kind) && _store.InteractionEpisodes(Scope, s.Player) == 0
        && !firsts.Any(f => f.Subject == "met:" + s.Player.ToLowerInvariant());

    private static (string Subject, string Note)? OtherFirstTime(RawSignal s)
    {
        return s.Kind switch
        {
            SignalKinds.Affection when s.Player != null && s.Subject?.StartsWith("emote:") == true =>
                ($"{s.Subject}:{s.Player.ToLowerInvariant()}", $"first {s.Subject[6..]} from {s.Player}"),
            SignalKinds.TaskOk when s.Subject != null => ("task:" + s.Subject, $"first {TaskTypes.Label(s.Subject)} done"),
            SignalKinds.SparWin or SignalKinds.SparLoss or SignalKinds.SparDraw when s.Player != null =>
                ("spar:" + s.Player.ToLowerInvariant(), $"first spar with {s.Player} ({SparWord(s.Kind)})"),
            SignalKinds.Death => ("death", "first death"),
            SignalKinds.NearFall => ("edge_save", "first time the edge guard caught you"),
            _ => null,
        };
    }

    /// <summary>Recurring phrases → inside-joke / nickname / catchphrase candidates (regulars only: strangers can't plant jokes).</summary>
    private void AddPhraseSignals(List<PhraseObservation> phrases, List<SelfSignal> batch, string epRef, DateTimeOffset at, Func<string?, double> weight)
    {
        foreach (var ph in phrases)
        {
            if (ph.Player.Length > 0 && weight(ph.Player) < 1) continue;
            int threshold = ph.Kind == "catch" ? _o.InsideJokeEpisodes + 1 : _o.InsideJokeEpisodes;
            if (!_store.NotePhrase(Scope, ph.Player, ph.Kind, ph.Phrase, epRef, at, threshold)) continue;
            batch.Add(ToSignal(PhraseSignal(ph), epRef, at, 1));
        }
    }

    private static RawSignal PhraseSignal(PhraseObservation ph)
    {
        var who = ph.Player.Length > 0 ? ph.Player : null;
        return ph.Kind switch
        {
            "nick" => new(SignalKinds.Nickname, who, 0.6, 0.7, ph.Phrase, $"{who} calls you \"{ph.Phrase}\""),
            "catch" => new(SignalKinds.Catchphrase, null, 0.2, 0.5, ph.Phrase, $"you keep saying \"{ph.Phrase}\""),
            _ => new(SignalKinds.InsideJoke, who, 0.5, 0.7, ph.Phrase, $"{who} keeps saying \"{ph.Phrase}\""),
        };
    }

    private static string SparWord(string kind) => kind == SignalKinds.SparWin ? "won" : kind == SignalKinds.SparLoss ? "lost" : "even";

    private SelfSignal ToSignal(RawSignal s, string epRef, DateTimeOffset at, double weight) => new()
    {
        At = at, Scope = Scope, CloneId = Clone, Player = s.Player, Kind = s.Kind, Subject = s.Subject, Valence = s.Valence,
        Intensity = s.Intensity, Weight = weight, Episode = epRef, Note = s.Note,
    };

    /// <summary>Regulars (trusted, configured, or familiar by now) count fully; strangers less; other bots barely.</summary>
    public double WeightOf(string player)
    {
        if (_hive.IsBot(player) || _botNames().Any(n => n.Equals(player, StringComparison.OrdinalIgnoreCase))) return _o.BotWeight;
        return IsRegular(player) ? 1 : Math.Clamp(_o.NonRegularWeight, 0, 1);
    }

    public bool IsRegular(string player) =>
        _o.TrustedPlayers.Concat(_o.RegularPlayers).Any(p => p.Equals(player, StringComparison.OrdinalIgnoreCase))
        || (_o.RegularAfterEpisodes > 0 && _store.InteractionEpisodes(Scope, player) >= _o.RegularAfterEpisodes);

    /// <summary>
    /// Belief-system feed: a player's claim was checked against seed data / world observations. Verified claims raise
    /// trust, refuted ones lower it a little (disputed: barely). Slow (trust half-life), floored, and trust has no path
    /// to hostility: the worst it renders is "double-check their claims".
    /// </summary>
    /// <param name="weight">0..1: how much this claim matters / how sure the check is.</param>
    public SelfSignal? RecordClaimOutcome(string player, ClaimOutcome outcome, double weight = 1, string? note = null)
    {
        if (!_o.Enabled || TurnContext.IsTest || SelfSanitizer.CleanPlayer(player) is not { } p) return null;
        double v = outcome switch { ClaimOutcome.Verified => 1, ClaimOutcome.Refuted => -1, _ => -0.3 };
        var s = new SelfSignal
        {
            At = Now, Scope = Scope, CloneId = Clone, Player = p, Kind = SignalKinds.ClaimOutcome, Subject = outcome.ToString().ToLowerInvariant(),
            Valence = v, Intensity = Math.Clamp(weight, 0, 1), Weight = WeightOf(p), Source = "belief",
            Note = TextUtilShort(note ?? $"{p}'s claim {outcome.ToString().ToLowerInvariant()}"),
        };
        return _store.Record([s]).FirstOrDefault();
    }

    private static string TextUtilShort(string s) => Tools.TextUtil.Truncate(Tools.TextUtil.OneLine(s), 90);

    // ======================= aggregates =======================

    public MoodState Mood()
    {
        var v = _store.Values(Scope, SignalEffects.Mood, Now, "").ToDictionary(x => x.Dim, x => x.Sum);
        double D(string d) => Math.Tanh(v.GetValueOrDefault(d) / 1.5);
        return new MoodState(D("cheer"), D("confidence"), D("energy"), D("affection"));
    }

    public RelationshipState Relationship(string player)
    {
        var key = player.ToLowerInvariant();
        var now = Now;
        var v = _store.Values(Scope, SignalEffects.Rel, now, key);
        double Sum(string dim) => v.Where(x => x.Dim == dim).Sum(x => x.Sum);
        var treat = v.Where(x => x.Dim.StartsWith("t_")).ToDictionary(x => x.Dim[2..], x => x.Sum);
        var req = v.Where(x => x.Dim.StartsWith("req:")).ToDictionary(x => x.Dim[4..], x => x.Sum);
        var jokes = _store.Phrases(Scope, key, "phrase", 2).Select(p => p.Phrase).ToList();
        var nick = _store.Phrases(Scope, key, "nick", 1).Select(p => p.Phrase).FirstOrDefault();
        var firsts = _store.Recent(Scope, null, 3, player, SignalKinds.FirstTime).Select(s => s.Note ?? s.Subject ?? "").ToList();
        return new RelationshipState(player)
        {
            Affinity = Math.Max(_o.AffinityFloor, Math.Tanh(Sum("affinity") / 3)),
            Trust = Math.Max(_o.TrustFloor, Math.Tanh(Sum("trust") / 2.5)),
            Playful = Math.Clamp(Math.Tanh(Sum("playful") / 2), 0, 1),
            Treatment = treat,
            Requests = req,
            InsideJokes = jokes,
            Nickname = nick,
            Milestones = firsts,
            Episodes = _store.InteractionEpisodes(Scope, player),
            Regular = IsRegular(player),
        };
    }

    /// <summary>Players this scope has any relationship data for, most interacted first.</summary>
    public List<string> KnownPlayers(int limit = 12) =>
        _store.Db.Query("""
            SELECT player FROM self_signals WHERE scope = $s AND player IS NOT NULL GROUP BY player COLLATE NOCASE
            ORDER BY count(DISTINCT episode) DESC, max(at) DESC LIMIT $n
            """, r => r.GetString(0), ("$s", Scope), ("$n", limit))
            .Where(p => !_hive.IsBot(p)).ToList();

    public List<PreferenceState> Preferences()
    {
        var v = _store.Values(Scope, SignalEffects.Pref, Now);
        return v.GroupBy(x => x.Key).Select(g =>
        {
            double D(string d) => g.Where(x => x.Dim == d).Sum(x => x.Sum);
            return new PreferenceState(g.Key, D("done"), D("ok"), D("fail"), D("praise"));
        }).OrderByDescending(p => p.Done).ThenBy(p => p.Subject, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Explains one value by its source signals. <paramref name="what"/>: "mood:cheer", "rel:kiwi:affinity",
    /// "pref:cobble:praise" (family:key:dim; mood has no key).
    /// </summary>
    public List<Contribution> Explain(string what, int limit = 5)
    {
        var parts = what.Split(':');
        return parts switch
        {
            [SignalEffects.Mood, var dim] => _store.Explain(Scope, SignalEffects.Mood, "", dim, Now, limit),
            [var fam, var key, var dim] => _store.Explain(Scope, fam, fam == SignalEffects.Rel ? key.ToLowerInvariant() : key, dim, Now, limit),
            _ => [],
        };
    }

    /// <summary>Players to show relationship lines for: this turn's people, recent signal players, the host's online list.</summary>
    public List<string> PresentPlayers(IEnumerable<string>? turnPlayers)
    {
        var list = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p) || _hive.IsBot(p) || _botNames().Any(n => n.Equals(p, StringComparison.OrdinalIgnoreCase))) return;
            if (!list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
        }
        foreach (var p in turnPlayers ?? []) Add(p);
        foreach (var s in _store.Recent(Scope, Now - TimeSpan.FromMinutes(Math.Max(1, _o.PresentMinutes)), 40)) Add(s.Player);
        try { foreach (var p in OnlinePlayers?.Invoke() ?? []) Add(p); } catch (Exception) { }
        return list;
    }
}
