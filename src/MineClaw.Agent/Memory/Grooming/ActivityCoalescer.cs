using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MineClaw.Agent.Rag;

namespace MineClaw.Agent.Memory.Grooming;

/// <summary>Structured hints an activity rollup doc carries into the groomer (<see cref="RagDoc.Meta"/>).</summary>
public static class ActivityMeta
{
    public const string Player = "player", Start = "start", End = "end", PlaceKind = "place_kind", PlaceKey = "place_key", PlaceName = "place_name";
    public const string DocPrefix = "act:";
    public const string UnknownStructure = "unknown_structure";
}

/// <summary>One counted thing an actor did ("mined ~312 cobblestone"): verb class + item.</summary>
public sealed record ActivityPart(string Verb, string Item, int Count, bool Approx, bool Probably)
{
    [JsonIgnore] public string Key => Verb + "|" + Item;
}

/// <summary>A parsed activity rollup (one session of one actor at one place).</summary>
public sealed record ActivitySpan(string Actor, string At, IReadOnlyList<ActivityPart> Parts, IReadOnlyList<string> Novel, string Day,
    string WallStart, string WallEnd, long StartMs, long EndMs, string PlaceKey, string? PlaceKind, int Sessions = 1)
{
    public bool IsRoutine => Novel.Count == 0 && Parts.Count > 0 && PlaceKind != ActivityMeta.UnknownStructure;
}

/// <summary>A coalesced episode (one doc): routine spans folded together, or one novel span kept individual.</summary>
public sealed class ActivityEpisode
{
    public string Ref { get; set; } = "";
    public string Place { get; set; } = "";
    public int Dim { get; set; }
    public string Day { get; set; } = "";
    public string WallStart { get; set; } = "";
    public string WallEnd { get; set; } = "";
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public bool Routine { get; set; }
    public bool First { get; set; }
    public string At { get; set; } = "";
    public int Sessions { get; set; } = 1;
    public List<string> Members { get; set; } = [];
    public List<ActivityPart> Parts { get; set; } = [];
    /// <summary>Member → part key → count (the crew breakdown).</summary>
    public Dictionary<string, Dictionary<string, int>> ByMember { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Novel { get; set; } = [];
    public double[]? Bounds { get; set; }

    public ActivityEpisode Clone() => JsonSerializer.Deserialize(JsonSerializer.Serialize(this, GroomJson.Default.ActivityEpisode), GroomJson.Default.ActivityEpisode)!;
}

/// <summary>Source-generated JSON for the episode state (works with reflection-free serialization too).</summary>
[JsonSerializable(typeof(ActivityEpisode))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class GroomJson : JsonSerializerContext;

/// <summary>
/// Where open episodes live: the <c>activity_episodes</c> table (created on first use; read-only dbs are only read), or an
/// in-memory overlay on top of it for a dry run (preview, compaction preview) that never writes.
/// </summary>
public sealed class ActivityEpisodeState
{
    private readonly AgentDb? _db;
    private readonly Dictionary<string, ActivityEpisode>? _overlay;
    private readonly ActivityEpisodeState? _base;
    private bool _ensured;

    public ActivityEpisodeState(AgentDb? db) => _db = db;

    private ActivityEpisodeState(ActivityEpisodeState b)
    {
        _db = b._db;
        _base = b;
        _overlay = new Dictionary<string, ActivityEpisode>(StringComparer.Ordinal);
    }

    /// <summary>A dry-run view: reads this state, keeps its own writes in memory.</summary>
    public ActivityEpisodeState Overlay() => new(_base ?? this);

    public bool IsDryRun => _overlay != null;

    private bool Ready()
    {
        if (_db == null) return false;
        if (_ensured) return true;
        if (!_db.ReadOnly)
        {
            _db.Exec("""
                CREATE TABLE IF NOT EXISTS activity_episodes(ref TEXT PRIMARY KEY, place TEXT NOT NULL, dim INTEGER NOT NULL, day TEXT NOT NULL,
                    start INTEGER NOT NULL, end INTEGER NOT NULL, routine INTEGER NOT NULL, members TEXT NOT NULL, data TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_actep_place ON activity_episodes(place, dim, day, end);
                """);
            _ensured = true;
        }
        else _ensured = _db.TableExists("activity_episodes");
        return _ensured;
    }

    private IEnumerable<ActivityEpisode> Stored(string sql, params (string, object?)[] args) =>
        Ready() ? _db!.Query(sql, r => JsonSerializer.Deserialize(r.GetString(0), GroomJson.Default.ActivityEpisode)!, args) : [];

    /// <summary>Routine episodes at a place on a day, newest first.</summary>
    public List<ActivityEpisode> Open(string place, int dim, string day)
    {
        var list = Stored("SELECT data FROM activity_episodes WHERE place = $p AND dim = $d AND day = $day AND routine = 1 ORDER BY end DESC LIMIT 40",
            ("$p", place), ("$d", dim), ("$day", day)).ToList();
        if (_overlay == null) return list;
        foreach (var e in _overlay.Values.Where(e => e.Routine && e.Place == place && e.Dim == dim && e.Day == day))
        {
            list.RemoveAll(x => x.Ref == e.Ref);
            list.Add(e);
        }
        return list.OrderByDescending(e => e.EndMs).ToList();
    }

    /// <summary>True when <paramref name="actor"/> has any episode at the place (first-visit detection).</summary>
    public bool Visited(string actor, string place, int dim)
    {
        var tag = "%\"" + actor.ToLowerInvariant() + "\"%";
        if (Ready() && _db!.ScalarLong("SELECT count(*) FROM activity_episodes WHERE place = $p AND dim = $d AND members LIKE $m",
                ("$p", place), ("$d", dim), ("$m", tag)) > 0) return true;
        return _overlay != null && _overlay.Values.Any(e => e.Place == place && e.Dim == dim && e.Members.Contains(actor, StringComparer.OrdinalIgnoreCase));
    }

    public bool IsEpisode(string docRef) =>
        (_overlay?.ContainsKey(docRef) ?? false) || (Ready() && _db!.ScalarLong("SELECT count(*) FROM activity_episodes WHERE ref = $r", ("$r", docRef)) > 0);

    public int Count => (Ready() ? (int)_db!.ScalarLong("SELECT count(*) FROM activity_episodes") : 0) + (_overlay?.Count ?? 0);

    public void Save(ActivityEpisode e)
    {
        if (_overlay != null) { _overlay[e.Ref] = e.Clone(); return; }
        if (!Ready() || _db!.ReadOnly) return;
        _db.Exec("""
            INSERT INTO activity_episodes(ref, place, dim, day, start, end, routine, members, data) VALUES ($r, $p, $d, $day, $s, $e, $ro, $m, $j)
            ON CONFLICT(ref) DO UPDATE SET place = excluded.place, dim = excluded.dim, day = excluded.day, start = excluded.start, end = excluded.end,
                routine = excluded.routine, members = excluded.members, data = excluded.data
            """, ("$r", e.Ref), ("$p", e.Place), ("$d", e.Dim), ("$day", e.Day), ("$s", e.StartMs), ("$e", e.EndMs), ("$ro", e.Routine ? 1 : 0),
            ("$m", JsonSerializer.Serialize(e.Members.Select(m => m.ToLowerInvariant()).ToList(), GroomJson.Default.ListString)),
            ("$j", JsonSerializer.Serialize(e, GroomJson.Default.ActivityEpisode)));
    }
}

/// <summary>
/// coalesce (activity source): activity becomes an episode doc only at a meaningful grain. Routine work (same actor, same place:
/// gen mining, chopping, farming rounds, chest runs) folds into one episode per actor+place per
/// <see cref="ActivityEpisodeOptions.RoutineWindowMinutes"/> within a day, counts summed and times spanned ("maid_chan mined
/// ~312 cobblestone at the cobble gen (10-28 00:31–02:10, 6 sessions)"); crew bots working the same place at overlapping times share
/// one crew episode ("the crew (main, 67, 69) mined …"). Novel sessions (a death, a fight, a build, a sign, an unknown structure,
/// anything this parser doesn't recognise as routine) stay individual. Verbs are natural ("mined", "chopped"), never "broke".
/// </summary>
public sealed partial class ActivityCoalesceStage : IMemoryGroomer
{
    public string Name => "coalesce";

    public IReadOnlyList<RagDoc> Apply(RagDoc doc, GroomContext ctx)
    {
        if (!doc.Ref.StartsWith(ActivityMeta.DocPrefix, StringComparison.Ordinal) || doc.Level != 0) return [doc];
        if (Parse(doc, ctx) is not { } span) return [doc];
        var o = ctx.Options.Activity;
        var state = ctx.Episodes;
        var place = span.PlaceKey;
        bool crew = ctx.Crew.Contains(span.Actor);

        if (span.IsRoutine && !span.Parts.Any(p => p.Verb == "place" && p.Count >= o.BuildMinBlocks))
        {
            var open = state.Open(place, doc.Dimension, span.Day);
            ActivityEpisode? hit = null;
            foreach (var e in open)
            {
                if (span.StartMs - e.StartMs >= TimeSpan.FromMinutes(Math.Max(1, o.RoutineWindowMinutes)).TotalMilliseconds) continue;
                if (span.StartMs < e.StartMs - 60_000) continue;
                bool mine = e.Members.Count == 1 && e.Members[0].Equals(span.Actor, StringComparison.OrdinalIgnoreCase)
                            || e.Members.Contains(span.Actor, StringComparer.OrdinalIgnoreCase) && e.Members.All(ctx.Crew.Contains);
                bool join = crew && o.CrewMerge && e.Members.All(ctx.Crew.Contains)
                            && span.StartMs <= e.EndMs + (long)TimeSpan.FromMinutes(Math.Max(0, o.CrewOverlapMinutes)).TotalMilliseconds;
                if (mine || join) { hit = e; break; }
            }
            if (hit != null)
            {
                Merge(hit, span, doc);
                state.Save(hit);
                return [Render(hit, doc, ctx)];
            }
        }

        var ep = new ActivityEpisode
        {
            Ref = doc.Ref, Place = place, Dim = doc.Dimension, Day = span.Day, WallStart = span.WallStart, WallEnd = span.WallEnd,
            StartMs = span.StartMs, EndMs = span.EndMs, Routine = span.IsRoutine && !span.Parts.Any(p => p.Verb == "place" && p.Count >= o.BuildMinBlocks),
            At = span.At, Sessions = span.Sessions, Members = [span.Actor], Novel = [.. span.Novel],
            First = span.At.Length > 0 && !state.Visited(span.Actor, place, doc.Dimension),
            Bounds = doc.Bounds is { } b ? [b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ] : null,
        };
        ep.Parts = span.Parts.ToList();
        ep.ByMember[span.Actor] = span.Parts.ToDictionary(p => p.Key, p => p.Count);
        state.Save(ep);
        return [Render(ep, doc, ctx)];
    }

    private static void Merge(ActivityEpisode e, ActivitySpan s, RagDoc doc)
    {
        if (!e.Members.Contains(s.Actor, StringComparer.OrdinalIgnoreCase)) e.Members.Add(s.Actor);
        if (!e.ByMember.TryGetValue(s.Actor, out var mine)) e.ByMember[s.Actor] = mine = new Dictionary<string, int>();
        foreach (var p in s.Parts)
        {
            int i = e.Parts.FindIndex(x => x.Key == p.Key);
            if (i < 0) e.Parts.Add(p);
            else
            {
                var x = e.Parts[i];
                e.Parts[i] = x with { Count = x.Count + p.Count, Approx = x.Approx || p.Approx, Probably = x.Probably && p.Probably };
            }
            mine[p.Key] = mine.GetValueOrDefault(p.Key) + p.Count;
        }
        e.Sessions += s.Sessions;
        if (s.EndMs > e.EndMs) { e.EndMs = s.EndMs; e.WallEnd = s.WallEnd; }
        if (s.StartMs < e.StartMs) { e.StartMs = s.StartMs; e.WallStart = s.WallStart; }
        if (e.At.Length == 0) e.At = s.At;
        if (doc.Bounds is { } b)
            e.Bounds = e.Bounds is { Length: 6 } a
                ? [Math.Min(a[0], b.MinX), Math.Min(a[1], b.MinY), Math.Min(a[2], b.MinZ), Math.Max(a[3], b.MaxX), Math.Max(a[4], b.MaxY), Math.Max(a[5], b.MaxZ)]
                : [b.MinX, b.MinY, b.MinZ, b.MaxX, b.MaxY, b.MaxZ];
    }

    // ======================= rendering =======================

    /// <summary>"main" for the main bot, "67" for maid_chan_67, the name otherwise.</summary>
    public static string Short(string member, string? main)
    {
        if (main == null) return member;
        if (member.Equals(main, StringComparison.OrdinalIgnoreCase)) return "main";
        return member.StartsWith(main + "_", StringComparison.OrdinalIgnoreCase) ? member[(main.Length + 1)..] : member;
    }

    private static List<string> Ordered(ActivityEpisode e, string? main) =>
        e.Members.OrderBy(m => main != null && m.Equals(main, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(m => m.Length).ThenBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();

    public static string Who(ActivityEpisode e, string? main)
    {
        if (e.Members.Count == 1) return e.Members[0];
        return "the crew (" + string.Join(", ", Ordered(e, main).Select(m => Short(m, main))) + ")";
    }

    private static string Qty(ActivityPart p) => p.Count == 1 && !p.Approx ? "" : (p.Approx ? "~" : "") + p.Count.ToString(CultureInfo.InvariantCulture) + " ";

    /// <summary>One part as a phrase ("chopped 18 log").</summary>
    public static string Phrase(ActivityPart p) => Split(p) is var (v, o) ? v + " " + o : "";

    /// <summary>A part as (verb, object): ("collected", "216 cobblestone"), ("opened", "the chest 16 times").</summary>
    public static (string Verb, string Obj) Split(ActivityPart p)
    {
        string times = p.Count > 1 ? $" {p.Count} times" : "";
        var (v, o) = p.Verb switch
        {
            "mine" => (ActivityVerbs.Broken(p.Item), $"{Qty(p)}{p.Item}"),
            "harvest" => ("harvested", $"{Qty(p)}{p.Item}"),
            "plant" => ("planted", $"{Qty(p)}{p.Item}"),
            "place" => ("placed", $"{Qty(p)}{p.Item}"),
            "take" => ("took", $"{Qty(p)}{p.Item}"),
            "store" => ("stored", $"{Qty(p)}{p.Item}"),
            "craft" => ("crafted", $"{Qty(p)}{p.Item}"),
            "collect" => ("collected", $"{Qty(p)}{p.Item}"),
            "open" => ("opened", $"the {p.Item}{times}"),
            "light" => ("lit", $"the furnace{times}"),
            "chests" => ("used", $"{p.Count} chests"),
            _ => (p.Verb, $"{Qty(p)}{p.Item}"),
        };
        return ((p.Probably ? "probably " : "") + v, o);
    }

    private static string List(IReadOnlyList<string> items) =>
        items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    private static readonly string[] VerbOrder = ["mine", "harvest", "plant", "place", "craft", "store", "take", "collect", "open", "light", "chests"];

    /// <summary>The episode's text: who, what (counts summed), where, when.</summary>
    public static string Text(ActivityEpisode e, string? main)
    {
        var parts = e.Parts.OrderByDescending(p => p.Verb is "open" or "light" or "chests" ? 0 : p.Count)
            .ThenBy(p => Array.IndexOf(VerbOrder, p.Verb)).ThenBy(p => p.Item, StringComparer.Ordinal).ToList();
        var phrases = new List<string>();
        if (!e.Routine) phrases.AddRange(e.Novel);
        // Every counted thing is kept (the facts), grouped by verb: "collected 216 cobblestone, 14 bread and 4 planks".
        var groups = parts.Select(p => (Part: p, Split: Split(p))).GroupBy(x => x.Split.Verb, StringComparer.Ordinal).ToList();
        bool nested = groups.Count > 1 && groups.Any(g => g.Count() > 1);
        var grouped = groups.Select(g => g.Key + " " + List(g.Select(x => x.Split.Obj).ToList())).ToList();
        phrases.AddRange(grouped);
        var what = phrases.Count == 0 ? "was busy" : nested ? string.Join("; ", phrases) : List(phrases);
        var sb = new StringBuilder(Who(e, main)).Append(' ').Append(what);
        if (e.Members.Count > 1 && parts.FirstOrDefault() is { } top)
        {
            var split = Ordered(e, main).Select(m => (m, n: e.ByMember.TryGetValue(m, out var d) ? d.GetValueOrDefault(top.Key) : 0))
                .Where(x => x.n > 0).Select(x => $"{Short(x.m, main)} {(top.Approx ? "~" : "")}{x.n}").ToList();
            if (split.Count > 1)
                sb.Append(" (").Append(phrases.Count > 1 || parts.Count > 1 ? Split(top).Verb + " " + top.Item + ": " : "").Append(string.Join(", ", split)).Append(')');
        }
        sb.Append(e.At);
        if (e.First) sb.Append(", first time there");
        sb.Append(' ').Append(GroomText.Span(e.Day, e.WallStart, e.WallEnd, e.Sessions));
        return sb.ToString();
    }

    private static RagDoc Render(ActivityEpisode e, RagDoc doc, GroomContext ctx) => doc with
    {
        Ref = e.Ref,
        Kind = "episode",
        Title = (e.Members.Count == 1 ? e.Members[0] : "crew") + " activity",
        Body = Text(e, ctx.MainName),
        Bounds = e.Bounds is { Length: 6 } a ? new Box(a[0], a[1], a[2], a[3], a[4], a[5]) : doc.Bounds,
        At = DateTimeOffset.FromUnixTimeMilliseconds(e.EndMs),
    };

    // ======================= parsing =======================

    [GeneratedRegex(@"^(mined|broke|chopped|dug|cleared|took down|harvested|planted|placed|crafted|stored|took|picked up|collected) (?:(~)?(\d+) )?(.+)$")]
    private static partial Regex Counted();

    [GeneratedRegex(@"^opened the (.+?)(?: (\d+) times)?$")]
    private static partial Regex Opened();

    [GeneratedRegex(@"^lit the furnace(?: (\d+) times)?$")]
    private static partial Regex Lit();

    [GeneratedRegex(@"^(?:used|reorganized) (\d+) chests$")]
    private static partial Regex Chests();

    [GeneratedRegex(@" (at the [^,()]+|on [^,()]+)$")]
    private static partial Regex AtTail();

    [GeneratedRegex(@", first time there$")]
    private static partial Regex FirstTail();

    private static string VerbClass(string v) => v switch
    {
        "mined" or "broke" or "chopped" or "dug" or "cleared" or "took down" => "mine",
        "harvested" => "harvest", "planted" => "plant", "placed" => "place", "crafted" => "craft", "stored" => "store",
        "took" => "take", "picked up" or "collected" => "collect",
        _ => v,
    };

    /// <summary>One phrase of a rollup list → a counted part (null = not routine: a fight, a death, a sign…).</summary>
    public static ActivityPart? ParsePart(string phrase)
    {
        var p = phrase.Trim();
        bool prob = p.StartsWith("probably ", StringComparison.Ordinal);
        if (prob) p = p["probably ".Length..];
        if (p.StartsWith("placed sign", StringComparison.Ordinal)) return null;
        var m = Counted().Match(p);
        if (m.Success)
        {
            var item = m.Groups[4].Value.Trim();
            if (item.Length == 0 || item.Contains('"') || item.Contains('(')) return null;
            int n = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 1;
            return new ActivityPart(VerbClass(m.Groups[1].Value), item, n, m.Groups[2].Success, prob);
        }
        if ((m = Opened().Match(p)).Success)
            return new ActivityPart("open", m.Groups[1].Value, m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1, false, prob);
        if ((m = Lit().Match(p)).Success)
            return new ActivityPart("light", "furnace", m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 1, false, prob);
        if ((m = Chests().Match(p)).Success)
            return new ActivityPart("chests", "chests", int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), false, prob);
        return null;
    }

    /// <summary>Splits "a, b, c and d" (the rollup's list join).</summary>
    public static List<string> SplitList(string list)
    {
        var parts = list.Split(", ").ToList();
        var last = parts[^1];
        int i = last.LastIndexOf(" and ", StringComparison.Ordinal);
        if (i > 0)
        {
            parts[^1] = last[..i];
            parts.Add(last[(i + 5)..]);
        }
        return parts.Where(p => p.Length > 0).ToList();
    }

    /// <summary>A rollup doc → its span (null = not an activity rollup this parser understands; the doc passes through).</summary>
    public static ActivitySpan? Parse(RagDoc doc, GroomContext ctx)
    {
        var meta = doc.Meta;
        if (ActorOf(doc) is not { } actor) return null;
        var body = doc.Body.Trim();
        if (!body.StartsWith(actor + " ", StringComparison.OrdinalIgnoreCase)) return null;

        long? startMs = MetaMs(meta, ActivityMeta.Start);
        long? endMs = MetaMs(meta, ActivityMeta.End);
        var (day, ws, we, sessions) = SpanWords(ref body, doc, ctx, startMs, endMs);
        long end = endMs ?? AgentDb.ToUnixMs(doc.At ?? ctx.Time.GetUtcNow());
        long start = startMs ?? end - (long)Math.Max(0, (TimeSpan.Parse(we, CultureInfo.InvariantCulture) - TimeSpan.Parse(ws, CultureInfo.InvariantCulture)).TotalMilliseconds);

        var rest = body[(actor.Length + 1)..].Trim();
        rest = FirstTail().Replace(rest, "");
        string at = PlaceTail(rest, meta?.GetValueOrDefault(ActivityMeta.PlaceName));
        if (at.Length > 0) rest = rest[..^at.Length];
        var (parts, novel) = Phrases(rest, ctx);
        var (placeKey, placeKind) = PlaceOf(meta, at);
        return new ActivitySpan(actor, at, parts, novel, day, ws, we, start, end, placeKey, placeKind, sessions);
    }

    /// <summary>Who the rollup is about: its player meta, else "X activity" in the title; null for none or the crew.</summary>
    private static string? ActorOf(RagDoc doc)
    {
        string? actor = doc.Meta?.GetValueOrDefault(ActivityMeta.Player);
        if (actor == null && doc.Title.EndsWith(" activity", StringComparison.Ordinal)) actor = doc.Title[..^" activity".Length];
        return string.IsNullOrWhiteSpace(actor) || actor == "crew" ? null : actor;
    }

    private static long? MetaMs(IReadOnlyDictionary<string, string>? meta, string key) =>
        long.TryParse(meta?.GetValueOrDefault(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>
    /// Day, window start/end and session count: from the body's trailing "(10-26 14:05-14:40, 3 sessions)" (cut off the body),
    /// else from the start/end meta or the doc time, in local time.
    /// </summary>
    private static (string Day, string Ws, string We, int Sessions) SpanWords(ref string body, RagDoc doc, GroomContext ctx, long? startMs, long? endMs)
    {
        var span = GroomText.TrailingSpan().Match(body);
        if (span.Success)
        {
            var ws = span.Groups[2].Value;
            int sessions = span.Groups[4].Success ? int.Parse(span.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
            body = body[..span.Index];
            return (span.Groups[1].Value, ws, span.Groups[3].Success ? span.Groups[3].Value : ws, sessions);
        }
        var st = (startMs is { } a ? DateTimeOffset.FromUnixTimeMilliseconds(a) : doc.At ?? ctx.Time.GetUtcNow()).ToLocalTime();
        var en = (endMs is { } b ? DateTimeOffset.FromUnixTimeMilliseconds(b) : doc.At ?? ctx.Time.GetUtcNow()).ToLocalTime();
        return (st.ToString("MM-dd", CultureInfo.InvariantCulture), st.ToString("HH:mm", CultureInfo.InvariantCulture), en.ToString("HH:mm", CultureInfo.InvariantCulture), 1);
    }

    /// <summary>The " at the X" / " on X" tail naming the place (the place meta's name, else any "at ..." tail); "" = none.</summary>
    private static string PlaceTail(string rest, string? placeName)
    {
        if (placeName == null) return AtTail().Match(rest) is { Success: true } am ? " " + am.Groups[1].Value : "";
        foreach (var cand in new[] { " at the " + placeName, " on " + placeName })
            if (rest.EndsWith(cand, StringComparison.Ordinal)) return cand;
        return "";
    }

    /// <summary>The listed phrases: known actions merged by key (counts summed), the rest kept as novel lines.</summary>
    private static (List<ActivityPart> Parts, List<string> Novel) Phrases(string rest, GroomContext ctx)
    {
        var parts = new List<ActivityPart>();
        var novel = new List<string>();
        foreach (var phrase in SplitList(rest))
        {
            if (ParsePart(phrase) is not { } p)
            {
                novel.Add(PhraseStage.Line(phrase, ctx));
                continue;
            }
            int i = parts.FindIndex(x => x.Key == p.Key);
            if (i < 0) parts.Add(p);
            else parts[i] = parts[i] with { Count = parts[i].Count + p.Count, Approx = parts[i].Approx || p.Approx, Probably = parts[i].Probably && p.Probably };
        }
        return (parts, novel);
    }

    private static (string Key, string? Kind) PlaceOf(IReadOnlyDictionary<string, string>? meta, string at)
    {
        var placeKind = meta?.GetValueOrDefault(ActivityMeta.PlaceKind);
        if (placeKind == null && at.Contains("unknown build", StringComparison.Ordinal)) placeKind = ActivityMeta.UnknownStructure;
        var placeKey = meta?.GetValueOrDefault(ActivityMeta.PlaceKey) is { Length: > 0 } pk ? (placeKind ?? "") + ":" + pk
            : at.Length > 0 ? "@" + at.Trim().ToLowerInvariant() : "-";
        return (placeKey, placeKind);
    }
}
