using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MineClaw.Agent.Chat;
using MineClaw.Hive.Story;

namespace MineClaw.App.Supervisor;

/// <summary>One directive sent to AI bots from the Story Mode map (best effort: the brain decides how).</summary>
public sealed class Directive
{
    public string Id { get; init; } = "";
    public DateTimeOffset At { get; init; }
    public List<string> Bots { get; init; } = new();
    public string Kind { get; init; } = "";
    public string Summary { get; set; } = "";
    /// <summary>The line each brain got.</summary>
    public string Line { get; set; } = "";
    public string Status { get; set; } = "sent";
    public string? Reply { get; set; }
    public string? RequestId { get; init; }
    public string? Player { get; init; }
    public string? FeedText { get; set; }
    public long? ViewerSeq { get; set; }
    public long? Design { get; set; }
    /// <summary>The exact targets/blocks behind the summary (the brain's <c>story_directive</c> lookup).</summary>
    public JsonNode? Detail { get; set; }
    /// <summary>The feed seq after which a brain's lines are its replies.</summary>
    public long FeedSeq { get; set; }
    public HashSet<string> Delivered { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Retryable { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// AI hives and bodies take Story Mode's selections as directives (docs/web/story-mode.md "AI hives"): ONE compact natural-language
/// line per bot through the viewer chat path (origin viewer, the trusted player, commands only with the commands grant), with
/// counts and bounds instead of target lists; a build is saved as a design and referenced by id. Status comes from the bot's own
/// next lines in the viewer feed (acknowledged, then done/failed when it says so): best effort, no guarantees.
/// </summary>
public sealed class StoryDirectives(IViewerData data, ViewerFeed? feed, TimeProvider? time = null)
{
    public static readonly IReadOnlyList<string> Kinds =
        ["chop", "cut", "harvest", "mine", "haul", "hunt", "deconstruct", "build", "move", "attack", "guard", "craft", "text"];
    /// <summary>The line budget (tokens are a hard requirement: one short line, never a list).</summary>
    public const int MaxLine = 240;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<Directive> _all = new();
    private readonly object _lock = new();
    private int _seq;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>Saves a build as a design (name, ghosts, requester) → its id, or null when it can't be one (too big, no designs here).</summary>
    public Func<string, IReadOnlyList<GhostState>, string, long?>? SaveDesign { get; set; }
    /// <summary>The world's game data (build expansion) and a chest's label for the line.</summary>
    public Func<MineClaw.Data.Versioning.IGameData?>? Data { get; set; }
    public Func<IStoryWorld?>? World { get; set; }

    public Directive? Get(string id) { lock (_lock) return _all.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); }

    private static string F(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string Box(Area a) => $"{F(a.X1)},{F(a.Y1)},{F(a.Z1)} → {F(a.X2)},{F(a.Y2)},{F(a.Z2)}";

    /// <summary>The one line a brain gets (≤ <see cref="MaxLine"/> chars): who, what, where in counts and bounds, the reference id.</summary>
    public static string Render(string player, string kind, JsonObject d, string id, int? count = null, long? design = null)
    {
        var area = Area.Parse(d["area"]);
        var parts = new LineParts(d, Where(d, area), count, area, design);
        var text = d["text"]?.GetValue<string>()?.Trim();
        string body = Bodies.TryGetValue(kind, out var line) ? line(parts) : text ?? "";
        if (kind != "text" && !string.IsNullOrWhiteSpace(text)) body += " — " + text;
        var full = $"{player} (story map): {body} [{id}]";
        return full.Length <= MaxLine ? full : full[..(MaxLine - 1)] + "…";
    }

    /// <summary>" in the area x,y,z → x,y,z", " at N spots", or nothing.</summary>
    private static string Where(JsonObject d, Area? area)
    {
        if (area is { } a) return $" in the area {Box(a)}";
        return d["targets"] is JsonArray t && t.Count > 0 ? $" at {t.Count} spot{(t.Count == 1 ? "" : "s")}" : "";
    }

    /// <summary>What a directive's line is made of: the directive, where it applies, the counted targets, its area and saved design.</summary>
    private sealed record LineParts(JsonObject D, string Where, int? Count, Area? Area, long? Design)
    {
        /// <summary>"the 14 trees", "the 1 tree", or "the trees" without a count.</summary>
        public string N(string what) => Count is { } c ? $"the {c} {what}{(c == 1 ? "" : "s")}" : $"the {what}s";
    }

    /// <summary>The line's body per kind (a "text" directive, or an unknown kind, is its text).</summary>
    private static readonly Dictionary<string, Func<LineParts, string>> Bodies = new(StringComparer.Ordinal)
    {
        ["chop"] = p => $"chop {p.N("tree")}{p.Where}",
        ["cut"] = p => $"cut the plants{p.Where}",
        ["harvest"] = p => $"harvest {p.N("ripe crop")}{p.Where} and replant",
        ["mine"] = p => $"mine {(p.D["filter"]?.GetValue<string>() is { } f && f != "all" ? f : "the blocks")}{p.Where}",
        ["haul"] = p => $"pick up {p.N("drop")}{p.Where} and store them",
        ["hunt"] = p => $"hunt {p.N("animal")}{p.Where}",
        ["deconstruct"] = p => $"take down the built blocks{p.Where}",
        ["build"] = BuildBody,
        ["move"] = p => $"go to {(p.Area is { } ma ? Box(ma) : p.Where.Trim())}",
        ["attack"] = p => $"attack the target{p.Where}",
        ["guard"] = p => $"guard the area{p.Where}",
        ["craft"] = CraftBody,
    };

    private static string BuildBody(LineParts p) =>
        p.Design is { } ds
            ? $"build design #{F((int)ds)}{(p.D["build"]?["name"]?.GetValue<string>() is { } bn ? $" '{bn}'" : "")}{p.Where}"
            : $"build {Describe(p.D["build"])}{p.Where}";

    private static string CraftBody(LineParts p) =>
        $"craft {p.D["bill"]?["recipe"]?.GetValue<string>()?.Replace("craft:", "").Replace("smelt:", "smelt ") ?? "it"}" +
        (p.D["bill"]?["target"] is { } bt ? $" x{bt}" : "");

    private static string Describe(JsonNode? b)
    {
        if (b is not JsonObject o) return "it";
        var kind = o["kind"]?.GetValue<string>() ?? "a structure";
        var mat = o["material"] is JsonValue v && v.TryGetValue<string>(out var m) ? m + " " : "";
        var h = o["height"] is { } hh ? $" {hh}-high" : "";
        var roof = o["roof"]?["style"]?.GetValue<string>() is { } rs && rs != "none" ? $" with a {rs} roof" : "";
        return $"a {mat}{kind}{h}{roof}";
    }

    /// <summary>Sends a directive → (directive, error). <paramref name="allowCommands"/> is already gated by the caller.</summary>
    public async Task<(Directive? D, string? Error)> SendAsync(IReadOnlyList<string> bots, JsonObject directive, string player, bool allowCommands,
        string? requestId, CancellationToken ct)
    {
        // Retries from a disconnected viewer must not repeat a colony configuration request.
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(requestId))
            {
                Directive? prior;
                lock (_lock) prior = _all.FirstOrDefault(d => d.RequestId == requestId && string.Equals(d.Player, player, StringComparison.OrdinalIgnoreCase));
                if (prior != null) return await RetryDeliveryAsync(prior, player, allowCommands, requestId, ct).ConfigureAwait(false);
            }
            return await SendCoreAsync(bots, directive, player, allowCommands, requestId, ct).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    private async Task<(Directive? D, string? Error)> RetryDeliveryAsync(Directive prior, string player, bool allowCommands,
        string requestId, CancellationToken ct)
    {
        // Successful deliveries are never replayed. Only an explicit refusal (or an unattempted target) is safe to retry;
        // a transport exception may have delivered the message before losing its acknowledgement.
        var retry = prior.Retryable.Where(n => !prior.Delivered.Contains(n)).ToList();
        if (retry.Count == 0) return (prior, null);
        var (targets, error) = Targets(retry);
        if (targets == null) return (null, error);
        prior.FeedSeq = feed?.Last ?? 0;
        prior.Status = "sent";
        prior.Reply = null;
        await DeliverAsync(prior, targets, player, allowCommands, requestId, ct).ConfigureAwait(false);
        if (prior.Delivered.Count == 0) prior.Status = "failed";
        return (prior, null);
    }

    private async Task<(Directive? D, string? Error)> SendCoreAsync(IReadOnlyList<string> bots, JsonObject directive, string player,
        bool allowCommands, string? requestId, CancellationToken ct)
    {
        var (kind, invalid) = KindOf(directive);
        if (kind == null) return (null, invalid);
        var (targets, unknown) = Targets(bots);
        if (targets == null) return (null, unknown);
        string id;
        lock (_lock) id = "dir" + (++_seq).ToString(CultureInfo.InvariantCulture);
        var d = new Directive { Id = id, At = _time.GetUtcNow(), Bots = targets.Select(t => t.Login).ToList(), Kind = kind, RequestId = requestId, Player = player };
        // Counts from the world view (the brain gets the count and the bounds; the list stays here for its lookup).
        int? count = CountTargets(kind, directive, d);
        if (kind == "build" && ExpandBuild(directive, player, d) is { } buildError) return (null, buildError);
        d.Line = Render(player, kind, directive, id, count, d.Design);
        d.Summary = d.Line[(d.Line.IndexOf(": ", StringComparison.Ordinal) + 2)..];
        if (directive["colony"]?.GetValue<string>() is { } colony)
        {
            // The compact line directs the brain to the full request, never silently truncates the owner's instructions.
            d.Detail = (JsonObject)directive.DeepClone();
            d.FeedText = directive["text"]!.GetValue<string>().Trim();
            d.Summary = d.FeedText.Length > 160 ? d.FeedText[..159] + "…" : d.FeedText;
            d.Line = $"{player} (Controller {colony}): read story_directive {{id:'{id}'}} for my full request. Use colony_control to change the registered colony settings and work, then report the result.";
        }
        d.FeedSeq = feed?.Last ?? 0;
        d.Retryable.UnionWith(targets.Select(t => t.Login));
        // Publish before waking the brain: it may immediately look up the reference in its first tool call.
        lock (_lock)
        {
            _all.Add(d);
            if (_all.Count > 200) _all.RemoveRange(0, _all.Count - 200);
        }
        await DeliverAsync(d, targets, player, allowCommands, requestId, ct).ConfigureAwait(false);
        if (d.Delivered.Count == 0) d.Status = "failed";
        return (d, null);
    }

    /// <summary>The directive's kind (lower case), or why the directive is refused.</summary>
    private static (string? Kind, string? Error) KindOf(JsonObject directive)
    {
        var kind = directive["kind"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        if (kind == null || !Kinds.Contains(kind)) return (null, $"directive.kind must be {string.Join("|", Kinds)}");
        if (kind == "text" && string.IsNullOrWhiteSpace(directive["text"]?.GetValue<string>())) return (null, "a text directive needs text");
        int maxText = directive["colony"] == null ? 200 : 4000;
        if (directive["text"]?.GetValue<string>() is { } text && text.Length > maxText) return (null, $"text over {maxText} chars");
        if (directive["colony"] != null && kind != "text") return (null, "colony context requires a text directive");
        if (directive["area"] != null && Area.Parse(directive["area"]) == null) return (null, "area must be [x1,y1,z1,x2,y2,z2]");
        return (kind, null);
    }

    /// <summary>Each named bot (once) with its login, or the first one that isn't a bot.</summary>
    private (List<(string Bot, string Login)>? Targets, string? Error) Targets(IReadOnlyList<string> bots)
    {
        var targets = new List<(string Bot, string Login)>();
        foreach (var b in bots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (data.Resolve(b) is not { } login) return (null, $"no such bot '{b}'");
            targets.Add((b, login));
        }
        return targets.Count == 0 ? (null, "bots required") : (targets, null);
    }

    /// <summary>A chop/harvest/haul/hunt in an area: its targets from the world view into the directive's detail; their count (null = not counted).</summary>
    private int? CountTargets(string kind, JsonObject directive, Directive d)
    {
        if (World?.Invoke() is not { } w || Area.Parse(directive["area"]) is not { } area) return null;
        var list = kind switch
        {
            "chop" => StoryBlocks.TreesIn(w, area).Select(p => (JsonNode)p.Json()).ToList(),
            "harvest" => area.Volume <= 262_144 ? area.Blocks().Where(p => StoryBlocks.Crop(w, p.X, p.Y, p.Z) is { Ripe: true }).Take(2048).Select(p => (JsonNode)p.Json()).ToList() : null,
            "haul" => w.Entities().Where(e => e.Kind == "item" && area.Contains(e.Block)).Select(e => (JsonNode)JsonValue.Create(e.Id)).ToList(),
            "hunt" => w.Entities().Where(e => e.Kind == "mob" && !e.Hostile && area.Contains(e.Block)).Select(e => (JsonNode)JsonValue.Create(e.Id)).ToList(),
            _ => null,
        };
        if (list == null) return null;
        d.Detail = new JsonObject { ["targets"] = new JsonArray(list.ToArray()) };
        return list.Count;
    }

    /// <summary>A build spec becomes ghosts, saved as a design the line names; null = fine (or no game data), else the expansion error.</summary>
    private string? ExpandBuild(JsonObject directive, string player, Directive d)
    {
        if (directive["build"] is not JsonObject spec || Data?.Invoke() is not { } gd) return null;
        var (ghosts, _, err) = StoryConstruction.Expand(gd, spec, World?.Invoke());
        if (err != null) return "build: " + err;
        d.Design = SaveDesign?.Invoke(spec["name"]?.GetValue<string>() ?? $"story {spec["kind"]}", ghosts, player);
        d.Detail = new JsonObject { ["build"] = spec.DeepClone(), ["blocks"] = ghosts.Count };
        return null;
    }

    /// <summary>The line to every target bot as a viewer chat line (the feed shows it first); the ones it reached.</summary>
    private async Task DeliverAsync(Directive d, List<(string Bot, string Login)> targets, string player, bool allowCommands, string? requestId,
        CancellationToken ct)
    {
        foreach (var (_, login) in targets)
        {
            d.ViewerSeq = feed?.Add(login, "viewer", player, d.FeedText ?? d.Line, "viewer", requestId);
            d.Retryable.Remove(login);
            if (await data.SendAsync(new RemoteChat(player, d.Line, "viewer", login, allowCommands, requestId ?? d.Id), ct).ConfigureAwait(false))
                d.Delivered.Add(login);
            else d.Retryable.Add(login);
        }
    }

    private static readonly Regex DoneWords = new(@"\b(done|finished|complete[d]?|all (?:chopped|mined|harvested|built|stored)|built it|that's all)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FailWords = new(@"\b(can'?t|cannot|couldn'?t|unable|failed|won'?t|no way)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Statuses from the bots' own lines after each directive (said or internal); "done" only when the bot says so.</summary>
    public void Correlate()
    {
        if (feed == null) return;
        List<Directive> open;
        lock (_lock) open = _all.Where(d => d.Status is "sent" or "acknowledged").ToList();
        foreach (var d in open)
        {
            ReadReplies(d, feed);
            if (d.Status == "sent" && _time.GetUtcNow() - d.At > TimeSpan.FromMinutes(30)) d.Status = "unknown";
        }
    }

    /// <summary>A line one of the directive's bots said (or thought) itself.</summary>
    private static bool IsBotLine(ViewerLine l, Directive d) =>
        l.Kind is "said" or "internal" && l.Origin == "bot" && d.Bots.Contains(l.Bot, StringComparer.OrdinalIgnoreCase);

    /// <summary>The directive's bots' own lines since it was last read: the latest is its reply and status, until one says done or failed.</summary>
    private static void ReadReplies(Directive d, ViewerFeed feed)
    {
        foreach (var l in feed.Since(d.FeedSeq, 500))
        {
            if (!IsBotLine(l, d)) continue;
            d.Reply = l.Text.Length > 200 ? l.Text[..199] + "…" : l.Text;
            d.Status = DoneWords.IsMatch(l.Text) ? "done" : FailWords.IsMatch(l.Text) ? "failed" : "acknowledged";
            d.FeedSeq = l.Seq;
            if (d.Status is "done" or "failed") break;
        }
    }

    public JsonArray Json()
    {
        Correlate();
        lock (_lock)
            return new JsonArray(_all.TakeLast(50).Select(d => (JsonNode)new JsonObject
            {
                ["id"] = d.Id, ["at"] = d.At, ["bots"] = new JsonArray(d.Bots.Select(b => (JsonNode)b!).ToArray()), ["kind"] = d.Kind, ["summary"] = d.Summary,
                ["status"] = d.Status, ["reply"] = d.Reply, ["design"] = d.Design,
            }).ToArray());
    }

    /// <summary>The brain's lookup (<c>story_directive{id}</c>): the line, its status and the exact targets/blocks, compact.</summary>
    public string Lookup(string id)
    {
        if (Get(id) is not { } d) return $"no directive {id}";
        if (d.Detail?["colony"] is { } colony)
            return new JsonObject { ["id"] = d.Id, ["colony"] = colony.DeepClone(), ["request"] = d.Detail["text"]?.DeepClone(),
                ["instruction"] = "Inspect colony_control state, use help for action payloads, apply the requested changes through colony_control, and report actual results. Changes appear in the Controller tabs and map. Ask the owner if their requested area or targets are ambiguous." }.ToJsonString();
        var sb = new StringBuilder($"{d.Id} {d.Kind} ({d.Status}): {d.Summary}");
        if (d.Detail?["targets"] is JsonArray t) sb.Append("; targets ").Append(string.Join(" ", t.Take(64).Select(x => x is JsonArray p ? string.Join(",", p) : x?.ToString()))).Append(t.Count > 64 ? $" (+{t.Count - 64})" : "");
        if (d.Design is { } ds) sb.Append($"; design #{ds}");
        return sb.ToString();
    }
}
