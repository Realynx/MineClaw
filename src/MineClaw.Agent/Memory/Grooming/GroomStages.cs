using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MineClaw.Agent.Admin.Shards;
using MineClaw.Agent.Rag;
using MineClaw.Agent.Tools;

namespace MineClaw.Agent.Memory.Grooming;

/// <summary>Natural verbs for broken blocks ("chopped log", "mined cobblestone"), never "broke".</summary>
public static class ActivityVerbs
{
    private static readonly string[] Wood = ["log", "wood"];
    private static readonly string[] Leaves = ["leaves", "leaf"];
    private static readonly string[] Soil = ["dirt", "sand", "gravel", "grass", "farmland", "clay", "snow", "soul sand", "mycelium"];
    private static readonly string[] Fixtures = ["torch", "sign", "ladder", "rail", "fence", "door", "bed", "chest", "crafting table",
        "workbench", "furnace", "planks", "plank", "lever", "button", "pressure plate", "redstone", "glass", "trapdoor", "wool", "slab", "stairs", "web", "cobweb"];
    private static readonly string[] Crops = ["wheat", "crops", "sugar cane", "reeds", "cactus", "melon", "pumpkin", "sapling", "mushroom"];

    /// <summary>The verb for having broken <paramref name="item"/> ("log" → "chopped").</summary>
    public static string Broken(string item)
    {
        var i = item.ToLowerInvariant().Trim();
        if (Has(i, Wood)) return "chopped";
        if (Has(i, Leaves)) return "cleared";
        if (Has(i, Crops)) return "harvested";
        if (Has(i, Soil)) return "dug";
        if (Has(i, Fixtures)) return "took down";
        return "mined";
    }

    private static bool Has(string item, string[] words) =>
        words.Any(w => item == w || item.EndsWith(" " + w, StringComparison.Ordinal) || item.StartsWith(w + " ", StringComparison.Ordinal)
                       || item == w + "s" || item.EndsWith(" " + w + "s", StringComparison.Ordinal));
}

/// <summary>Shared text helpers of the stages.</summary>
internal static partial class GroomText
{
    /// <summary>"(10-28 00:31-02:10" / "(10-28 00:31 – 02:10" → canonical "(10-28 00:31–02:10"; one minute → "(10-28 00:31".</summary>
    [GeneratedRegex(@"\((\d{2}-\d{2}) (\d{2}:\d{2}) ?[-–] ?(\d{2}:\d{2})(?=[,)])")]
    internal static partial Regex RawSpan();

    /// <summary>A canonical trailing span: "(10-28 00:31–02:10, 6 sessions)" / "(10-28 00:31)" / "(10-28 06:46–07:10, 3×)".</summary>
    [GeneratedRegex(@"\s*\((\d{2}-\d{2}) (\d{2}:\d{2})(?:–(\d{2}:\d{2}))?(?:, (\d+)(?: sessions?|×))?\)\s*$")]
    internal static partial Regex TrailingSpan();

    [GeneratedRegex(@"\b\d{4}-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?")]
    internal static partial Regex IsoTime();

    [GeneratedRegex(@"(?<![\w.])(-?\d+(?:\.\d+)?), ?(-?\d+(?:\.\d+)?), ?(-?\d+(?:\.\d+)?)(?![\w.])")]
    internal static partial Regex Coords();

    [GeneratedRegex(@"[ \t ]+")]
    internal static partial Regex Spaces();

    /// <summary>Console/file log prefix: "06:45:53.713 INF App: " (level kept in group 1).</summary>
    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2}(?:\.\d+)? (INF|DBG|WRN|ERR|TRC|FTL|CRT) [\w.]+: ")]
    internal static partial Regex ConsolePrefix();

    /// <summary>journald: "2026-10-27T06:45:53+00:00 host mineclaw[12749]: MineClaw.App[0] ".</summary>
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2})?(?:Z|[+-]\d{2}:?\d{2})?) \S+ [\w.\-]+\[\d+\]: (?:[\w.]+\[\d+\] )?")]
    internal static partial Regex JournalPrefix();

    internal static string Span(string day, string start, string end, int count = 0, string unit = "sessions") =>
        "(" + day + " " + start + (end != start ? "–" + end : "") + (count > 1 ? $", {count}{(unit == "×" ? "×" : " " + unit)}" : "") + ")";

    internal static string Coord(string v)
    {
        if (!v.Contains('.')) return v;
        var d = double.Parse(v, CultureInfo.InvariantCulture);
        var s = Math.Round(d, 1).ToString("0.#", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }
}

/// <summary>
/// normalize: log prefixes stripped, timestamps into the canonical "(10-28 00:31–02:10)" span, coordinates kept but trimmed to one
/// decimal, bot/player aliases → canonical names (and their casing), whitespace collapsed (line breaks kept). Idempotent.
/// </summary>
public sealed class NormalizeStage : IMemoryGroomer
{
    public string Name => "normalize";

    public IReadOnlyList<RagDoc> Apply(RagDoc doc, GroomContext ctx)
    {
        var body = Normalize(doc.Body, ctx);
        var title = Normalize(doc.Title, ctx).Replace('\n', ' ');
        return [doc with { Title = title, Body = body }];
    }

    public static string Normalize(string text, GroomContext ctx)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder(text.Length);
        bool blank = false;
        foreach (var raw in lines)
        {
            var l = StripPrefix(raw);
            l = GroomText.Spaces().Replace(l, " ").Trim();
            if (l.Length == 0)
            {
                if (sb.Length > 0) blank = true;
                continue;
            }
            l = GroomText.IsoTime().Replace(l, m => $"{m.Groups[1].Value}-{m.Groups[2].Value} {m.Groups[3].Value}:{m.Groups[4].Value}");
            l = GroomText.RawSpan().Replace(l, m => "(" + m.Groups[1].Value + " " + m.Groups[2].Value +
                (m.Groups[3].Value != m.Groups[2].Value ? "–" + m.Groups[3].Value : ""));
            l = GroomText.Coords().Replace(l, m => m.Value.Contains('.')
                ? $"{GroomText.Coord(m.Groups[1].Value)},{GroomText.Coord(m.Groups[2].Value)},{GroomText.Coord(m.Groups[3].Value)}"
                : m.Value);
            l = Names(l, ctx);
            if (sb.Length > 0) sb.Append(blank ? "\n\n" : "\n");
            blank = false;
            sb.Append(l);
        }
        return sb.ToString();
    }

    /// <summary>Strips a console or journald prefix; a journald timestamp becomes "10-27 06:45: ".</summary>
    internal static string StripPrefix(string line)
    {
        var m = GroomText.JournalPrefix().Match(line);
        if (m.Success)
        {
            var t = GroomText.IsoTime().Match(m.Groups[1].Value);
            return (t.Success ? $"{t.Groups[1].Value}-{t.Groups[2].Value} {t.Groups[3].Value}:{t.Groups[4].Value}: " : "") + line[m.Length..];
        }
        var c = GroomText.ConsolePrefix().Match(line);
        return c.Success ? line[c.Length..] : line;
    }

    private static string Names(string line, GroomContext ctx)
    {
        foreach (var (alias, name) in ctx.Options.Names)
            line = Regex.Replace(line, $@"(?<![\w]){Regex.Escape(alias)}(?![\w])", name.Replace("$", "$$"), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var name in ctx.KnownNames)
        {
            if (name.Length < 3) continue;
            line = Regex.Replace(line, $@"(?<![\w]){Regex.Escape(name)}(?![\w])", m => m.Value == name ? m.Value : name,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return line;
    }
}

/// <summary>
/// denoise: drops debug-only lines and pure telemetry (turn/call cost lines, cache numbers, triage, raptor sync, positions dumps)
/// and strips inline cost/token fragments. A doc left with nothing is dropped.
/// </summary>
public sealed partial class DenoiseStage : IMemoryGroomer
{
    private readonly Regex[] _extra;

    public DenoiseStage(IEnumerable<string>? extra = null) =>
        _extra = (extra ?? []).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))).ToArray();

    public string Name => "denoise";

    private static readonly Regex[] DropLine =
    [
        new(@"^\d{2}:\d{2}:\d{2}(?:\.\d+)? (DBG|TRC) ", RegexOptions.CultureInvariant),
        new(@"^(?:\d{2}-\d{2} \d{2}:\d{2}: )?(turn: tier \w+|call (?:opus|sonnet|haiku|\w+) \(|triage →|raptor (?:sync|refresh|rebuild|tree loaded)|telemetry:|\d+ tools \(|in game: pos|addressing(?: miss)?:|logged in: entity|worker call \d+:|episode: \w+ said:\s*$|spawned at \(|safety: |Anthropic key ok|perception:|vision:|embed(?:ding)?s? (?:batch|flush))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"mineclaw\.service|^(?:\d{2}-\d{2} \d{2}:\d{2}: )?(?:connecting to|hive: \d+ clone|\d+ tools|knowledge \d+ chars)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    ];

    [GeneratedRegex(@"\s*\[in \d+ out \d+(?: cr \d+)?(?: cw \d+)?\]|\s*\$\d+\.\d{3,}|\s*\b\d+ call\(s\)|\s*\bcr \d+ cw \d+\b|,?\s*\bcache \d+ ?%")]
    private static partial Regex Inline();

    [GeneratedRegex(@"^[\W\d_]*$")]
    private static partial Regex NoWords();

    public IReadOnlyList<RagDoc> Apply(RagDoc doc, GroomContext ctx)
    {
        var kept = new List<string>();
        foreach (var line in doc.Body.Split('\n'))
        {
            if (IsNoise(line)) continue;
            var l = Inline().Replace(line, "");
            if (l.Trim().Length > 0 && (NoWords().IsMatch(l.Trim()) || IsNoise(l))) continue;
            kept.Add(l);
        }
        var body = string.Join("\n", kept).Trim();
        return body.Length == 0 ? [] : [doc with { Body = body }];
    }

    /// <summary>Debug-only or telemetry (raw log lines and already-normalized ones alike).</summary>
    public bool IsNoise(string line)
    {
        if (DropLine[0].IsMatch(line)) return true;
        var bare = NormalizeStage.StripPrefix(line).Trim();
        return DropLine.Skip(1).Any(r => r.IsMatch(bare) || r.IsMatch(line)) || _extra.Any(r => r.IsMatch(line) || r.IsMatch(bare));
    }
}

/// <summary>redact: token-shaped secrets replaced with "[redacted]" (the shard redaction rules).</summary>
public sealed class RedactStage : IMemoryGroomer
{
    public string Name => "redact";

    public IReadOnlyList<RagDoc> Apply(RagDoc doc, GroomContext ctx)
    {
        var body = ShardRedactor.Redact(doc.Body, out int nb);
        var title = ShardRedactor.Redact(doc.Title, out int nt);
        return nb + nt == 0 ? [doc] : [doc with { Body = body, Title = title }];
    }
}

/// <summary>
/// phrase: natural verbs and short third-person sentences in the memory style ("maid_chan mined 20 cobblestone", "Fox said
/// \"hi\"", "maid_chan joined the server"); "broke X" becomes the verb that fits X.
/// </summary>
public sealed partial class PhraseStage : IMemoryGroomer
{
    public string Name => "phrase";

    [GeneratedRegex(@"\bbroke (~?\d+ )?([a-z][a-z ]*?)(?=$| at | on | in |,| and |\.|\()")]
    private static partial Regex Broke();

    [GeneratedRegex(@"^((?:\d{2}-\d{2} \d{2}:\d{2}: )?)chat: <(\w+)> (.+)$")]
    private static partial Regex Chat();

    [GeneratedRegex(@"^((?:\d{2}-\d{2} \d{2}:\d{2}: )?)episode: \w+ said: (.+)$")]
    private static partial Regex BotSaid();

    [GeneratedRegex(@"^((?:\d{2}-\d{2} \d{2}:\d{2}: )?)joined [\w.:]+ as (\w+)$")]
    private static partial Regex Joined();

    public IReadOnlyList<RagDoc> Apply(RagDoc doc, GroomContext ctx) =>
        [doc with { Body = string.Join("\n", doc.Body.Split('\n').Select(l => Line(l, ctx))) }];

    public static string Line(string l, GroomContext ctx)
    {
        var bot = ctx.MainName ?? "the bot";
        var m = Chat().Match(l);
        if (m.Success) l = $"{m.Groups[1].Value}{m.Groups[2].Value} said \"{m.Groups[3].Value.Trim()}\"";
        else if ((m = BotSaid().Match(l)).Success) l = $"{m.Groups[1].Value}{bot} said \"{m.Groups[2].Value.Trim()}\"";
        else if ((m = Joined().Match(l)).Success) l = $"{m.Groups[1].Value}{m.Groups[2].Value} joined the server";
        return Broke().Replace(l, x => ActivityVerbs.Broken(x.Groups[2].Value) + " " + x.Groups[1].Value + x.Groups[2].Value);
    }
}

/// <summary>
/// merge: the same fact (text without its time span) as a doc of the same kind and source written within
/// <see cref="GroomOptions.MergeWindowMinutes"/> extends that doc ("… (10-28 06:46–07:10, 3×)") instead of inserting a new one.
/// </summary>
public sealed partial class MergeStage : IMemoryGroomer
{
    public string Name => "merge";

    [GeneratedRegex(@"^(\d{2}-\d{2}) (\d{2}:\d{2}): ")]
    private static partial Regex Leading();

    /// <summary>The fact without its time: lower-case, spans and leading timestamps removed.</summary>
    public static string FactKey(string body)
    {
        var b = GroomText.TrailingSpan().Replace(body.Trim(), "");
        b = Leading().Replace(b, "");
        return GroomText.Spaces().Replace(b, " ").Trim().ToLowerInvariant();
    }

    public IReadOnlyList<RagDoc> Apply(RagDoc doc, GroomContext ctx)
    {
        var key = FactKey(doc.Body);
        if (key.Length == 0) return [doc];
        var (day, time) = When(doc, ctx);
        var since = ctx.Time.GetUtcNow() - TimeSpan.FromMinutes(Math.Max(1, ctx.Options.MergeWindowMinutes));
        if (doc.At is { } at && at < ctx.Time.GetUtcNow()) since = at - TimeSpan.FromMinutes(Math.Max(1, ctx.Options.MergeWindowMinutes));
        RagDoc? hit = ctx.Emitted.LastOrDefault(e => e.Kind == doc.Kind && e.Source == doc.Source && FactKey(e.Body) == key);
        if (hit == null && ctx.Db != null)
        {
            var rows = ctx.Db.Query("""
                SELECT ref, title, body FROM docs WHERE kind = $k AND source IS $s AND level = 0 AND updated >= $t ORDER BY updated DESC LIMIT 300
                """, r => (Ref: r.GetString(0), Title: r.GetString(1), Body: r.GetString(2)),
                ("$k", doc.Kind), ("$s", doc.Source), ("$t", AgentDb.ToUnixMs(since)));
            var row = rows.FirstOrDefault(r => FactKey(r.Body) == key);
            if (row.Ref != null) hit = doc with { Ref = row.Ref, Title = row.Title, Body = row.Body };
        }
        if (hit == null) return [doc];
        var fact = GroomText.TrailingSpan().Replace(Leading().Replace(doc.Body.Trim(), ""), "");
        var span = GroomText.TrailingSpan().Match(hit.Body);
        string d0 = day, t0 = time;
        int n = 1;
        if (span.Success)
        {
            d0 = span.Groups[1].Value;
            t0 = span.Groups[2].Value;
            n = span.Groups[4].Success ? int.Parse(span.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
        }
        else if (Leading().Match(hit.Body) is { Success: true } lead) { d0 = lead.Groups[1].Value; t0 = lead.Groups[2].Value; }
        // Already inside the doc's span (the same line ingested twice, or the same fact twice in one minute): nothing new.
        var hitEnd = span.Success && span.Groups[3].Success ? span.Groups[3].Value : t0;
        if (d0 == day && string.CompareOrdinal(time, hitEnd) <= 0) return [hit];
        var end = d0 == day ? time : t0;
        return [doc with { Ref = hit.Ref, Title = hit.Title, Body = fact + " " + GroomText.Span(d0, t0, end, n + 1, "×") }];
    }

    private static (string Day, string Time) When(RagDoc doc, GroomContext ctx)
    {
        var lead = Leading().Match(doc.Body.Trim());
        if (lead.Success) return (lead.Groups[1].Value, lead.Groups[2].Value);
        var span = GroomText.TrailingSpan().Match(doc.Body);
        if (span.Success) return (span.Groups[1].Value, span.Groups[3].Success ? span.Groups[3].Value : span.Groups[2].Value);
        var t = (doc.At ?? ctx.Time.GetUtcNow()).ToLocalTime();
        return (t.ToString("MM-dd", CultureInfo.InvariantCulture), t.ToString("HH:mm", CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// size: titles/coords filled consistently (an empty title takes the first sentence; a doc without bounds takes the first
/// "@x,y,z" / "at x,y,z" it names) and docs over the chunk target are split at line/sentence boundaries (<c>ref</c>, <c>ref#2</c>…).
/// </summary>
public sealed partial class SizeStage : IMemoryGroomer
{
    public string Name => "size";

    [GeneratedRegex(@"(?:@|\bat )(-?\d+),(-?\d+),(-?\d+)\b")]
    private static partial Regex At();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex Sentence();

    public IReadOnlyList<RagDoc> Apply(RagDoc doc, GroomContext ctx)
    {
        var d = doc;
        if (string.IsNullOrWhiteSpace(d.Title))
        {
            var first = Sentence().Split(d.Body.Trim())[0].Split('\n')[0];
            d = d with { Title = TextUtil.Truncate(first, 80) };
        }
        if (d.Bounds == null && At().Match(d.Body) is { Success: true } m)
            d = d with { Bounds = Box.Point(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)) };
        int max = Math.Max(40, ctx.ChunkMaxTokens);
        if (TextUtil.EstimateTokens(d.Body) <= max) return [d];

        var pieces = d.Body.Split('\n').SelectMany(l => TextUtil.EstimateTokens(l) > max ? Sentence().Split(l) : [l]).ToList();
        var chunks = new List<string>();
        var cur = new StringBuilder();
        foreach (var p in pieces)
        {
            if (cur.Length > 0 && TextUtil.EstimateTokens(cur.ToString()) + TextUtil.EstimateTokens(p) > max)
            {
                chunks.Add(cur.ToString().Trim());
                cur.Clear();
            }
            if (cur.Length > 0) cur.Append('\n');
            cur.Append(p);
        }
        if (cur.Length > 0) chunks.Add(cur.ToString().Trim());
        if (chunks.Count <= 1) return [d];
        return chunks.Select((c, i) => d with
        {
            Ref = i == 0 ? d.Ref : $"{d.Ref}#{i + 1}",
            Title = $"{d.Title} ({i + 1}/{chunks.Count})",
            Body = c,
        }).ToList();
    }
}

/// <summary>rewrite: the optional LLM stage (memory tier, budget-capped, off by default; only on the async path).</summary>
public sealed class RewriteStage : IAsyncMemoryGroomer
{
    public string Name => "rewrite";

    public IReadOnlyList<RagDoc> Apply(RagDoc doc, GroomContext ctx) => [doc];

    public async Task<IReadOnlyList<RagDoc>> ApplyAsync(RagDoc doc, GroomContext ctx, CancellationToken ct)
    {
        var o = ctx.Options.Rewrite;
        if (!o.Enabled || ctx.Rewriter == null || doc.Source == null || !o.Sources.Contains(doc.Source, StringComparer.OrdinalIgnoreCase)) return [doc];
        var text = await ctx.Rewriter.RewriteAsync(doc.Source, doc.Body, ct).ConfigureAwait(false);
        return text is { Length: > 0 } t ? [doc with { Body = t.Trim() }] : [doc];
    }
}
