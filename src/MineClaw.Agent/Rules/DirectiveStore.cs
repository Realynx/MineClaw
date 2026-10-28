using System.Globalization;
using System.Runtime.CompilerServices;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent.Rules;

/// <summary>One standing rule a trusted player gave the bots (verified by authority, never by evidence).</summary>
public sealed record Directive
{
    public long Id { get; init; }
    /// <summary>Who gave it (login).</summary>
    public required string Player { get; init; }
    public required string Text { get; init; }
    public DirectiveScope Scope { get; init; } = DirectiveScope.General;
    public IReadOnlyList<string> Topics { get; init; } = [];
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; init; }
    public bool Active { get; init; } = true;
    public string? EndedBy { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    /// <summary>The rule that replaced this one.</summary>
    public long? ReplacedBy { get; init; }
    /// <summary>The claim row this rule was migrated from (it had been stored as an unverified claim).</summary>
    public long? SourceClaim { get; init; }
    public string CloneId { get; init; } = "main";

    public string Ref => "rule#" + Id;
    public string DocRef => "rule:" + Id;
}

/// <summary>What happened to a directive line (the tool result is <see cref="Text"/>).</summary>
public sealed record DirectiveResult(bool Stored, string Text, Directive? Rule = null, IReadOnlyList<Directive>? Replaced = null);

/// <summary>
/// Standing rules from trusted players (db <c>directives</c>, shared by every hive bot on the file): an instruction, preference
/// or rule ("only build in daytime though to avoid mobs") is a directive, not a world claim to verify. Stored with who, when and
/// scope (a design, a place, an activity, everywhere); a newer rule on the same topic and condition, or one that lifts it ("you
/// can build at night now"), replaces it; <c>forget rule#N</c> revokes it. Surfaced as text only where its activity is in play
/// (the turn's <c>[standing rules: ...]</c> line, the design tool's resume/build results): learned, not coded, the brain decides.
/// Each active rule is a RAG doc (kind <see cref="DocKind"/>) so recall finds it. Test chat is never stored.
/// </summary>
public sealed class DirectiveStore
{
    public const string DocKind = "rule";
    public const int MaxTextChars = 200;
    private static readonly ConditionalWeakTable<AgentDb, DirectiveStore> Stores = new();

    private readonly AgentDb _db;
    private readonly RagIndex? _index;
    private readonly List<Func<string?>> _inPlay = new();
    private bool _table;

    public DirectiveStore(AgentDb db, RagIndex? index, TimeProvider? time = null)
    {
        _db = db;
        _index = index;
        Time = time ?? TimeProvider.System;
    }

    /// <summary>The store shared by everything on this database (memory tools, the turn header, the design tool).</summary>
    public static DirectiveStore For(AgentDb db, RagIndex? index)
    {
        lock (Stores)
        {
            if (Stores.TryGetValue(db, out var s)) return s;
            s = new DirectiveStore(db, index);
            Stores.Add(db, s);
            return s;
        }
    }

    public static DirectiveStore? Find(AgentDb db) => Stores.TryGetValue(db, out var s) ? s : null;

    public TimeProvider Time { get; set; }
    /// <summary>Players whose instructions are directives (Self:TrustedPlayers). Null = nobody: every line stays a claim.</summary>
    public Func<string, bool>? IsTrusted { get; set; }
    /// <summary>Display name for a login ("PoofimaFox" → "Fox").</summary>
    public Func<string, string>? Nick { get; set; }
    /// <summary>"day" / "night" from the world clock (Bot layer), shown next to a rule that names a time of day.</summary>
    public Func<string?>? TimeOfDay { get; set; }
    /// <summary>Login of this process (hive clone id), recorded on new rules.</summary>
    public string CloneId { get; set; } = "main";

    /// <summary>
    /// Registers what is in play right now beyond the turn's own text (a paused or running build: "build design #4"), so its rules
    /// show in the header even on a turn about something else.
    /// </summary>
    public void AddInPlay(Func<string?> source)
    {
        lock (_inPlay) _inPlay.Add(source);
    }

    public bool Trusted(string? player) => player != null && IsTrusted?.Invoke(player) == true;

    private void EnsureTable()
    {
        if (_table) return;
        _db.Exec("""
            CREATE TABLE IF NOT EXISTS directives(
                id INTEGER PRIMARY KEY, player TEXT NOT NULL, text TEXT NOT NULL, scope TEXT NOT NULL, topics TEXT NOT NULL,
                created INTEGER NOT NULL, updated INTEGER NOT NULL, active INTEGER NOT NULL DEFAULT 1, ended_by TEXT, ended_at INTEGER,
                replaced_by INTEGER, source_claim INTEGER, clone_id TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS directives_active ON directives(active, updated);
            CREATE UNIQUE INDEX IF NOT EXISTS directives_claim ON directives(source_claim) WHERE source_claim IS NOT NULL;
            """);
        _table = true;
    }

    private DateTimeOffset Now => Time.GetUtcNow();

    // ======================= writes =======================

    /// <summary>
    /// Stores <paramref name="text"/> from <paramref name="player"/> (must be trusted) as a standing rule. An identical active rule
    /// is refreshed; an active rule it supersedes (same topic, and the same time-of-day condition or a lifting phrase like "you
    /// can build at night now") ends, replaced by this one.
    /// </summary>
    public DirectiveResult Add(string player, string text, DateTimeOffset? at = null, long? sourceClaim = null)
    {
        if (TurnContext.IsTest) return new(false, "test chat: rule not stored");
        if (!Trusted(player)) return new(false, $"{player} isn't trusted: not a rule");
        var clean = TextUtil.Truncate(TextUtil.OneLine(text ?? ""), MaxTextChars).Trim();
        if (clean.Length == 0) return new(false, "empty rule");
        if (DirectiveParser.IsSecret(clean)) return new(false, "not saved: never store secrets");
        EnsureTable();
        var scope = DirectiveParser.ScopeOf(clean);
        var topics = DirectiveParser.TopicsOf(clean);
        var when = at ?? Now;
        var replaced = new List<Directive>();
        var rule = _db.Tx(() =>
        {
            if (sourceClaim is { } sc && _db.Scalar("SELECT id FROM directives WHERE source_claim = $c", ("$c", sc)) is { } done)
                return Load(Convert.ToInt64(done, CultureInfo.InvariantCulture));
            var same = ActiveRows().FirstOrDefault(d => d.Text.Equals(clean, StringComparison.OrdinalIgnoreCase));
            if (same != null)
            {
                _db.Exec("UPDATE directives SET updated = $t WHERE id = $i", ("$t", AgentDb.ToUnixMs(when)), ("$i", same.Id));
                return Load(same.Id);
            }
            long ms = AgentDb.ToUnixMs(when);
            _db.Exec("""
                INSERT INTO directives(player, text, scope, topics, created, updated, active, source_claim, clone_id)
                VALUES ($p, $x, $s, $tp, $t, $t, 1, $c, $clone)
                """, ("$p", player.Trim()), ("$x", clean), ("$s", scope.Key), ("$tp", string.Join(",", topics)), ("$t", ms),
                ("$c", sourceClaim), ("$clone", CloneId));
            long id = _db.ScalarLong("SELECT last_insert_rowid()");
            var fresh = Load(id)!;
            foreach (var old in ActiveRows().Where(d => d.Id != id && Supersedes(fresh, d)))
            {
                _db.Exec("UPDATE directives SET active = 0, ended_by = $b, ended_at = $t, replaced_by = $r, updated = $t WHERE id = $i",
                    ("$b", player.Trim()), ("$t", ms), ("$r", id), ("$i", old.Id));
                replaced.Add(old);
            }
            return fresh;
        });
        if (rule == null) return new(false, "rule not stored");
        foreach (var r in replaced) _index?.Delete(r.DocRef);
        IndexDoc(rule);
        var msg = $"{rule.Ref} saved: standing rule from {Name(rule.Player)} ({rule.Scope.Describe()}); it shows where it applies";
        if (replaced.Count > 0) msg += "; replaces " + string.Join(", ", replaced.Select(r => $"{r.Ref} \"{TextUtil.Truncate(r.Text, 50)}\""));
        return new(true, msg, rule, replaced);
    }

    /// <summary>
    /// <paramref name="newer"/> replaces <paramref name="older"/>: they share an activity topic (or a design), the newer one's scope
    /// covers the older one's, and it names the same time-of-day condition or lifts a rule ("no longer", "you can ... now").
    /// </summary>
    internal static bool Supersedes(Directive newer, Directive older)
    {
        bool sameDesign = newer.Scope.Kind == DirectiveScopeKind.Design && older.Scope.Key == newer.Scope.Key;
        if (newer.Scope.Kind == DirectiveScopeKind.Design && !sameDesign) return false;
        if (newer.Scope.Kind == DirectiveScopeKind.Place && older.Scope.Key != newer.Scope.Key) return false;
        // Both are about the same main activity ("mine cobble every night" never replaces "only build in daytime; at night mine").
        bool topic = sameDesign || (Primary(newer) is { } pn && older.Topics.Contains(pn) && Primary(older) is { } po && newer.Topics.Contains(po));
        if (!topic) return false;
        var nt = DirectiveParser.TimeWords(newer.Text);
        bool condition = nt.Count > 0 && nt.Overlaps(DirectiveParser.TimeWords(older.Text));
        return condition || DirectiveParser.IsRevocation(newer.Text);
    }

    /// <summary>The activity a rule is mainly about: its activity scope, "build" for a design, else its first topic.</summary>
    private static string? Primary(Directive d) => d.Scope.Kind switch
    {
        DirectiveScopeKind.Activity => d.Scope.Value,
        DirectiveScopeKind.Design => "build",
        _ => d.Topics.FirstOrDefault(),
    };

    /// <summary>Ends a rule ("forget rule#3", "you can stop doing that"). False when there is no such active rule.</summary>
    public bool Revoke(long id, string? by)
    {
        if (TurnContext.IsTest) return false;
        EnsureTable();
        var d = Load(id);
        if (d is not { Active: true }) return false;
        _db.Exec("UPDATE directives SET active = 0, ended_by = $b, ended_at = $t, updated = $t WHERE id = $i",
            ("$b", string.IsNullOrWhiteSpace(by) ? "brain" : by.Trim()), ("$t", AgentDb.ToUnixMs(Now)), ("$i", id));
        _index?.Delete(d.DocRef);
        return true;
    }

    private void IndexDoc(Directive d)
    {
        if (_index == null || !d.Active) return;
        try
        {
            _index.Upsert(new RagDoc(d.DocRef, DocKind, $"rule {Name(d.Player)} {d.Player} {d.Scope.Describe()}", Render(d, withAge: false))
            {
                Tag = d.Scope.Kind.ToString().ToLowerInvariant(),
                Source = "rule",
                Weight = 1.2,
                At = d.Updated,
            });
        }
        catch (Exception) { /* retrieval is best effort; the table is the record */ }
    }

    // ======================= migration =======================

    /// <summary>
    /// One-time fix on startup (idempotent): claims a trusted player made that read as instructions (live 10-28: claim#9 "Fox OK'd
    /// continuing the drop tower (design #4), but only in daytime..." was an unverified claim, so it was never obeyed) become
    /// rules with the claim's time; the claim, its sources/evidence and its RAG doc go. Returns how many moved.
    /// </summary>
    public int MigrateClaims(ILogger? log = null)
    {
        if (IsTrusted == null || !_db.TableExists("claims")) return 0;
        EnsureTable();
        var rows = _db.Query("SELECT id, claimant, text, created FROM claims ORDER BY id",
            r => (Id: r.GetInt64(0), Who: r.GetString(1), Text: r.GetString(2), At: AgentDb.FromUnixMs(r.GetInt64(3))));
        int moved = 0;
        foreach (var c in rows)
        {
            if (!Trusted(c.Who) || !DirectiveParser.IsInstruction(c.Text)) continue;
            try
            {
                var r = Add(c.Who, c.Text, c.At, sourceClaim: c.Id);
                if (r.Rule == null) continue;
                _db.Tx(() =>
                {
                    _db.Exec("DELETE FROM claim_evidence WHERE claim_id = $i", ("$i", c.Id));
                    _db.Exec("DELETE FROM claim_sources WHERE claim_id = $i", ("$i", c.Id));
                    _db.Exec("DELETE FROM claims WHERE id = $i", ("$i", c.Id));
                });
                _index?.Delete("claim:" + c.Id);
                moved++;
                log?.LogInformation("directives: claim#{Claim} from {Player} is an instruction, now {Rule}: {Text}", c.Id, c.Who, r.Rule.Ref,
                    TextUtil.Truncate(c.Text, 100));
            }
            catch (SqliteException ex) { log?.LogWarning(ex, "directives: migrating claim#{Claim} failed", c.Id); }
        }
        return moved;
    }

    // ======================= reads + rendering =======================

    public Directive? Get(long id)
    {
        EnsureTable();
        return Load(id);
    }

    /// <summary>Active rules, newest first.</summary>
    public IReadOnlyList<Directive> Active()
    {
        EnsureTable();
        return ActiveRows();
    }

    /// <summary>Every rule, ended ones included, newest first (admin, supervisor).</summary>
    public IReadOnlyList<Directive> All(int limit = 100)
    {
        EnsureTable();
        return _db.Query($"SELECT {Cols} FROM directives ORDER BY updated DESC, id DESC LIMIT $n", Map, ("$n", limit));
    }

    private List<Directive> ActiveRows() =>
        _db.Query($"SELECT {Cols} FROM directives WHERE active = 1 ORDER BY updated DESC, id DESC", Map);

    /// <summary>
    /// Active rules that apply to what is in play: <paramref name="inPlay"/> (the turn's task, standing order and events) plus the
    /// registered sources. A design rule needs its design in play; a place rule its coordinates; a chat rule its player in the
    /// turn; anything else shares an activity topic. Newest first, at most <paramref name="limit"/>.
    /// </summary>
    public IReadOnlyList<Directive> Relevant(string? inPlay, int limit = 3)
    {
        var all = Active();
        if (all.Count == 0) return [];
        var text = InPlayText(inPlay);
        if (text.Length == 0) return [];
        var topics = DirectiveParser.TopicsOf(text).ToHashSet(StringComparer.Ordinal);
        var design = DesignsIn(text);
        return all.Where(d => Applies(d, text, topics, design)).Take(Math.Max(1, limit)).ToList();
    }

    private string InPlayText(string? inPlay)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(inPlay)) parts.Add(inPlay);
        Func<string?>[] sources;
        lock (_inPlay) sources = _inPlay.ToArray();
        foreach (var s in sources)
        {
            try { if (s() is { Length: > 0 } x) parts.Add(x); } catch (Exception) { }
        }
        return string.Join("\n", parts);
    }

    private static HashSet<string> DesignsIn(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text,
                     @"\b(?:design|blueprint|build(?:ing)?|bp|tower)\s*(?:id\s*)?[#=:]?\s*\{?\s*(?:""?(?:id|bp)""?\s*[=:]\s*)?""?(\d{1,6})\b",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            set.Add(m.Groups[1].Value);
        return set;
    }

    private bool Applies(Directive d, string text, HashSet<string> topics, HashSet<string> designs)
    {
        switch (d.Scope.Kind)
        {
            case DirectiveScopeKind.Design:
                return d.Scope.Value != null && designs.Contains(d.Scope.Value);
            case DirectiveScopeKind.Place:
                return d.Scope.Value != null && text.Replace(" ", "").Contains(d.Scope.Value, StringComparison.Ordinal);
        }
        if (d.Topics.Contains("chat") && d.Topics.Count == 1)
            return Mentions(text, d.Player);
        return d.Topics.Any(t => t != "chat" && topics.Contains(t)) || (d.Topics.Contains("chat") && Mentions(text, d.Player));
    }

    private bool Mentions(string text, string player) =>
        text.Contains(player, StringComparison.OrdinalIgnoreCase)
        || (Name(player) is { Length: >= 3 } n && System.Text.RegularExpressions.Regex.IsMatch(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(n)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    /// <summary>"[standing rules: rule#2 (Fox, design #4, 40m ago): only build in daytime... | ...]" or null.</summary>
    public string? HeaderLine(string? inPlay)
    {
        IReadOnlyList<Directive> rules;
        try { rules = Relevant(inPlay); }
        catch (Exception) { return null; }
        if (rules.Count == 0) return null;
        return "[standing rules: " + string.Join(" | ", rules.Select(r => Render(r, withAge: true))) + "]";
    }

    /// <summary>
    /// The rules for building design <paramref name="designId"/> (its own and building in general) for a design tool result:
    /// "rule#2 (Fox, design #4, 40m ago): only build in daytime...; it's night now". Null when none apply.
    /// </summary>
    public string? ForDesign(long designId, string? extraInPlay = null)
    {
        IReadOnlyList<Directive> rules;
        try
        {
            var id = designId.ToString(CultureInfo.InvariantCulture);
            rules = Active().Where(d => d.Scope.Kind == DirectiveScopeKind.Design ? d.Scope.Value == id
                : d.Scope.Kind != DirectiveScopeKind.Place && d.Topics.Contains("build")).Take(3).ToList();
        }
        catch (Exception) { return null; }
        if (rules.Count == 0) return null;
        var line = string.Join("; ", rules.Select(r => Render(r, withAge: true)));
        if (rules.Any(r => DirectiveParser.MentionsTimeOfDay(r.Text)) && Clock() is { } now) line += $"; it's {now} now";
        return line;
    }

    private string? Clock()
    {
        try { return TimeOfDay?.Invoke() is { Length: > 0 } t ? t : null; } catch (Exception) { return null; }
    }

    /// <summary>"rule#2 (Fox, design #4, 40m ago): only build in daytime ...".</summary>
    public string Render(Directive d, bool withAge)
    {
        var meta = Name(d.Player);
        if (d.Scope.Kind != DirectiveScopeKind.General) meta += ", " + d.Scope.Describe();
        if (withAge) meta += ", " + Age(Now - d.Created);
        return $"{d.Ref} ({meta}): {TextUtil.Truncate(TextUtil.OneLine(d.Text).TrimEnd('.', ' '), 150)}";
    }

    internal static string Age(TimeSpan t) =>
        t < TimeSpan.FromMinutes(1) ? "just now"
        : t < TimeSpan.FromHours(1) ? $"{(int)t.TotalMinutes}m ago"
        : t < TimeSpan.FromDays(2) ? $"{(int)t.TotalHours}h ago"
        : $"{(int)t.TotalDays}d ago";

    private string Name(string login)
    {
        string n = login;
        try { if (Nick?.Invoke(login) is { Length: > 0 } x) n = x; } catch (Exception) { }
        return n.Length > 0 && char.IsLower(n[0]) ? char.ToUpperInvariant(n[0]) + n[1..] : n;
    }

    private const string Cols = "id, player, text, scope, topics, created, updated, active, ended_by, ended_at, replaced_by, source_claim, clone_id";

    private static Directive Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), Player = r.GetString(1), Text = r.GetString(2), Scope = DirectiveScope.Parse(r.GetString(3)),
        Topics = r.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries),
        Created = AgentDb.FromUnixMs(r.GetInt64(5)), Updated = AgentDb.FromUnixMs(r.GetInt64(6)), Active = r.GetInt64(7) != 0,
        EndedBy = AgentDb.StrOrNull(r, 8), EndedAt = AgentDb.LongOrNull(r, 9) is { } e ? AgentDb.FromUnixMs(e) : null,
        ReplacedBy = AgentDb.LongOrNull(r, 10), SourceClaim = AgentDb.LongOrNull(r, 11), CloneId = r.GetString(12),
    };

    private Directive? Load(long id) =>
        _db.Query($"SELECT {Cols} FROM directives WHERE id = $i", Map, ("$i", id)).FirstOrDefault();
}
