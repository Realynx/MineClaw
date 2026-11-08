using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.App.Supervisor;

/// <summary>
/// Story Mode's routes (docs/web/story-mode.md), behind the web viewer's gate: 404 unless <c>Supervisor:Viewer:Enabled</c>, 403
/// without the token in the X-Supervisor-Token header; writes (POST) also need <see cref="SupervisorViewerOptions.AllowStory"/>
/// (default on when the viewer is). JSON is snake_case with nulls written.
/// <list type="bullet">
/// <item><c>GET /api/story</c> → <c>{colonies:[…], ai:[…], directives:[…]}</c>; <c>GET /api/story/recipes?workbench=</c> → <c>[Recipe]</c></item>
/// <item><c>POST /api/story/colonies</c> <c>{name, pawns, home?}</c> → <c>{id}</c>; <c>GET /api/story/colonies/{id}?since=rev</c> → the state (delta with since)</item>
/// <item><c>POST /api/story/colonies/{id}/release|resume|online|pause|home|priorities|roles|schedule|draft|orders|zones|designations|builds|storage|bills|workbenches|options|policies|material-rules|gear</c></item>
/// <item><c>GET /api/story/colonies/{id}/architect</c> → <c>{entries:[…], benches, tabs}</c> (the Architect palette with tiers)</item>
/// <item><c>POST /api/story/time</c> <c>{time}</c> → <c>{time, by}</c> (409 no op account, 429 over one a second)</item>
/// <item><c>POST /api/story/directives</c> → <c>{id, delivered}</c>; <c>GET /api/story/directives[/{id}]</c></item>
/// </list>
/// </summary>
public sealed partial class SupervisorStoryRoutes(SupervisorOptions o, StoryColonies colonies, StoryDirectives? directives = null,
    Func<IReadOnlyList<BodyRow>>? ai = null, IViewerData? viewer = null, TimeProvider? time = null, StoryTimeControl? timeControl = null)
{
    public const string Prefix = "/api/story";
    public const int MaxBodyBytes = 64 * 1024;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public StoryColonies Colonies => colonies;
    public void StopTimeControl() => timeControl?.Dispose();

    public static bool IsStoryPath(string path)
    {
        var p = path.TrimEnd('/');
        return p.Equals(Prefix, StringComparison.OrdinalIgnoreCase) || p.StartsWith(Prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes are on: AllowStory, else on whenever the viewer is.</summary>
    public static bool Writes(SupervisorOptions o) => o.Viewer.Enabled && (o.Viewer.AllowStory ?? true);

    /// <summary>A POST body for a story route (null otherwise), read up to <see cref="MaxBodyBytes"/>, only with the right header.</summary>
    public static async Task<string?> ReadBodyAsync(HttpListenerRequest req, SupervisorOptions o, CancellationToken ct)
    {
        if (!req.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) || !IsStoryPath(req.Url?.AbsolutePath ?? "/") || !req.HasEntityBody) return null;
        if (!o.Viewer.Enabled || !TokenOk(req.Headers["X-Supervisor-Token"], o.Token)) return null;
        return await SupervisorAdminRoutes.ReadCappedAsync(req, MaxBodyBytes, 8192, ct).ConfigureAwait(false);
    }

    private static bool TokenOk(string? given, string? token)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(given)) return false;
        var a = Encoding.UTF8.GetBytes(given);
        var b = Encoding.UTF8.GetBytes(token);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>Null = not a story route.</summary>
    public async Task<SupervisorReply?> TryHandleAsync(SupervisorRequest r, CancellationToken ct = default)
    {
        var path = r.Path.TrimEnd('/');
        if (!IsStoryPath(path)) return null;
        if (Gate(r) is { } denied) return denied;
        bool get = IsGet(r.Method);
        if (MethodDenied(r.Method, get) is { } refused) return refused;
        var (body, badBody) = get ? (new JsonObject(), null) : BodyOf(r.Body);
        if (badBody != null) return badBody;
        var parts = path[Prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        try
        {
            return await RouteAsync(r, parts, body, get, ct).ConfigureAwait(false) ?? Reply(404, new JsonObject { ["error"] = "not found" });
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        {
            return Bad("bad value: " + ex.Message); // a field of the wrong JSON type
        }
    }

    /// <summary>The viewer's gate: 404 while it is off, 403 with no token configured or the wrong one; null = through.</summary>
    private SupervisorReply? Gate(SupervisorRequest r)
    {
        if (!o.Viewer.Enabled) return Reply(404, new JsonObject { ["error"] = "not found" });
        if (string.IsNullOrEmpty(o.Token)) return Reply(403, new JsonObject { ["error"] = "Story Mode needs Supervisor:Token" });
        if (!TokenOk(r.TokenHeader, o.Token)) return Reply(403, new JsonObject { ["error"] = "X-Supervisor-Token header required" });
        return null;
    }

    private static bool IsGet(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase) || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    /// <summary>Only GET, HEAD and POST; a POST only with story writes on. Null = allowed (past this, not GET/HEAD means POST).</summary>
    private SupervisorReply? MethodDenied(string method, bool get)
    {
        bool post = method.Equals("POST", StringComparison.OrdinalIgnoreCase);
        if (!post && !get) return NotAllowed("GET, HEAD, POST");
        if (post && !Writes(o)) return Reply(403, new JsonObject { ["error"] = "story writes are off (Supervisor:Viewer:AllowStory)" });
        return null;
    }

    /// <summary>A POST body as a JSON object (blank = {}); the error reply for one too large or not an object.</summary>
    private static (JsonObject Body, SupervisorReply? Error) BodyOf(string? text)
    {
        if (text == SupervisorAdminRoutes.TooLarge) return (new JsonObject(), Reply(413, new JsonObject { ["error"] = $"body over {MaxBodyBytes} bytes" }));
        if (string.IsNullOrWhiteSpace(text)) return (new JsonObject(), null);
        try { return (JsonNode.Parse(text) as JsonObject ?? throw new JsonException(), null); }
        catch (JsonException) { return (new JsonObject(), Bad("body must be a JSON object")); }
    }

    /// <summary>Dispatch by path segments; null = no such route (404). A request that isn't GET/HEAD here is a POST.</summary>
    private async Task<SupervisorReply?> RouteAsync(SupervisorRequest r, string[] parts, JsonObject body, bool get, CancellationToken ct)
    {
        switch (parts)
        {
            case []:
                return GetOnly(get, () => Reply(200, Index()));
            case ["time"]:
                return await PostOnlyAsync(get, () => SetTimeAsync(body)).ConfigureAwait(false);
            case ["time-range"]:
                return await PostOnlyAsync(get, () => SetTimeRangeAsync(body)).ConfigureAwait(false);
            case ["recipes"]:
                return GetOnly(get, () => Recipes(r));
            case ["colonies"]:
                return PostOnly(get, () => Create(body));
            case ["colonies", var id]:
                return GetOnly(get, () => ColonyState(r, id));
            case ["colonies", var id, "architect"]:
                return GetOnly(get, () => Architect(id));
            case ["colonies", var id, var action]:
                return PostOnly(get, () => ColonyAction(id, action, body));
            case ["directives"]:
                return await DirectivesRouteAsync(get, body, ct).ConfigureAwait(false);
            case ["directives", var did]:
                return GetOnly(get, () => Directive(did));
            default:
                return null;
        }
    }

    private static SupervisorReply GetOnly(bool get, Func<SupervisorReply> reply) => get ? reply() : NotAllowed("GET, HEAD");
    private static SupervisorReply PostOnly(bool get, Func<SupervisorReply> reply) => get ? NotAllowed("POST") : reply();
    private static async Task<SupervisorReply> PostOnlyAsync(bool get, Func<Task<SupervisorReply>> reply) =>
        get ? NotAllowed("POST") : await reply().ConfigureAwait(false);

    /// <summary><c>GET</c> → every directive; <c>POST</c> → a new one.</summary>
    private async Task<SupervisorReply> DirectivesRouteAsync(bool get, JsonObject body, CancellationToken ct) =>
        get ? Reply(200, new JsonObject { ["directives"] = DirectivesJson() }) : await DirectiveAsync(body, ct).ConfigureAwait(false);

    private JsonArray DirectivesJson() => directives?.Json() ?? new JsonArray();

    private JsonObject Index() => new()
    {
        ["colonies"] = new JsonArray(colonies.All.Select(c => (JsonNode)c.Row()).ToArray()), ["ai"] = Ai(), ["directives"] = DirectivesJson(),
        ["time_control"] = TimeControl(),
    };

    private SupervisorReply Recipes(SupervisorRequest r)
    {
        if (colonies.Host.World is not { } w) return Reply(503, new JsonObject { ["error"] = "no world view yet" });
        var bench = Q(r, "workbench");
        if (bench != null && !StoryRecipes.Benches.Contains(bench)) return Bad($"workbench must be {string.Join("|", StoryRecipes.Benches)}");
        return Reply(200, new JsonArray(StoryRecipes.For(w.Data, bench).Select(x => (JsonNode)x.Json(w.Data)).ToArray()));
    }

    private SupervisorReply ColonyState(SupervisorRequest r, string id)
    {
        if (colonies.Get(id) is not { } c) return NoColony();
        long? since = null;
        if (Q(r, "since") is { } sv)
        {
            if (!long.TryParse(sv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) || s < 0) return Bad("since must be a revision number");
            since = s;
        }
        NoteMainNotOp(c);
        var st = c.State(since);
        st["ai"] = Ai(c);
        st["time_control"] = TimeControl();
        st["directives"] = DirectivesJson();
        return Reply(200, st);
    }

    /// <summary>The Architect palette: every placeable with its tier, unlock state and the colony's stock of it.</summary>
    private SupervisorReply Architect(string id)
    {
        if (colonies.Get(id) is not { } c) return NoColony();
        if (colonies.Host.World == null) return Reply(503, new JsonObject { ["error"] = "no world view yet" });
        return Reply(200, c.Architect());
    }

    private SupervisorReply ColonyAction(string id, string action, JsonObject body) =>
        colonies.Get(id) is { } c ? Colony(c, action.ToLowerInvariant(), body) : NoColony();

    private SupervisorReply Directive(string id)
    {
        directives?.Correlate();
        if (directives?.Get(id) is not { } d) return Reply(404, new JsonObject { ["error"] = "no such directive" });
        return Reply(200, new JsonObject
        {
            ["id"] = d.Id, ["at"] = d.At, ["kind"] = d.Kind, ["status"] = d.Status, ["summary"] = d.Summary, ["line"] = d.Line, ["reply"] = d.Reply,
            ["design"] = d.Design, ["bots"] = new JsonArray(d.Bots.Select(b => (JsonNode)b!).ToArray()),
            ["delivered"] = new JsonArray(d.Delivered.Select(b => (JsonNode)b!).ToArray()), ["detail"] = d.Detail?.DeepClone(),
        });
    }

    // ---------------------------------------------------------------- colonies

    private SupervisorReply Create(JsonObject b)
    {
        var name = b["name"]?.GetValue<string>();
        if (b["pawns"] is not JsonValue pv || !pv.TryGetValue<int>(out var n)) return Bad("pawns must be a whole number, 1 or more");
        P3? home = null;
        if (b["home"] is { } h && (home = P3.Parse(h)) == null) return Bad("home must be {x,y,z}");
        var (c, err) = colonies.Create(name ?? "", n, home);
        return c == null ? Reply(err is { } e && (e.StartsWith("name") || e.StartsWith("pawns")) ? 400 : 503, new JsonObject { ["error"] = err }) : Reply(200, new JsonObject { ["id"] = c.Id });
    }

    // ---------------------------------------------------------------- time of day

    /// <summary><c>{available, by}</c>: an op account the bot controls can set the time (main first).</summary>
    private JsonObject TimeControl()
    {
        var (available, by) = timeControl?.Status() ?? (false, null);
        var state = new JsonObject { ["available"] = available, ["by"] = by };
        if (timeControl?.Range is { } range) state["range"] = new JsonObject { ["start"] = range.Start, ["end"] = range.End };
        return state;
    }

    private async Task<SupervisorReply> SetTimeRangeAsync(JsonObject b)
    {
        if (!b.ContainsKey("range")) return Bad("range is required (null disables the loop)");
        StoryTimeRange? range = null;
        if (b["range"] != null)
        {
            if (b["range"] is not JsonObject r || r["start"] is not JsonValue sv || !sv.TryGetValue<int>(out var start)
                || r["end"] is not JsonValue ev || !ev.TryGetValue<int>(out var end)) return Bad("range needs integer start and end ticks");
            range = new(start, end);
            if (!range.Valid) return Bad("range endpoints must be different ticks in 0..23999");
        }
        if (timeControl == null || !await timeControl.SetRangeAsync(range).ConfigureAwait(false))
            return Reply(409, new JsonObject { ["error"] = "no op account" });
        return Reply(200, TimeControl());
    }

    private readonly HashSet<string> _notOpAlerted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Main known not to be op: the colony hears it once (main should be op: it runs the whitelist).</summary>
    private void NoteMainNotOp(StoryColony c)
    {
        if (timeControl?.MainNotOp() is not { } main) return;
        lock (_notOpAlerted)
            if (!_notOpAlerted.Add(c.Id)) return;
        c.Alert("warn", $"{main} (main) isn't op on the server: it can't run the whitelist or set the time (/op {main})");
    }

    /// <summary><c>{time: 0..23999}</c> → <c>{time, by}</c>; 409 with no op account, 429 for more than one change a second.</summary>
    private async Task<SupervisorReply> SetTimeAsync(JsonObject b)
    {
        if (b["time"] is not JsonValue tv || !tv.TryGetValue<int>(out var ticks) || ticks is < 0 or > StoryTimeControl.MaxTime)
            return Bad($"time must be a whole number 0..{StoryTimeControl.MaxTime}");
        if (timeControl == null) return Reply(409, new JsonObject { ["error"] = "no op account" });
        var (result, by, err) = await timeControl.SetAsync(ticks, S(b, "player")).ConfigureAwait(false);
        return result switch
        {
            StoryTimeControl.Outcome.Ok => Reply(200, new JsonObject { ["time"] = ticks, ["by"] = by }),
            StoryTimeControl.Outcome.NoOp => Reply(409, new JsonObject { ["error"] = "no op account" }),
            StoryTimeControl.Outcome.TooSoon => Reply(429, new JsonObject { ["error"] = err }, new Dictionary<string, string> { ["Retry-After"] = "1" }),
            _ => Reply(503, new JsonObject { ["error"] = err }),
        };
    }

    // ---------------------------------------------------------------- AI hives

    private async Task<SupervisorReply> DirectiveAsync(JsonObject b, CancellationToken ct)
    {
        if (directives == null || viewer == null) return Reply(503, new JsonObject { ["error"] = "directives need the viewer chat path" });
        var (input, refused) = DirectiveInputOf(b, viewer);
        if (input == null) return refused!;
        var (dir, err) = await directives.SendAsync(input.Bots, input.Directive, input.Player, input.Commands, S(b, "request_id"), ct).ConfigureAwait(false);
        if (dir == null) return err!.StartsWith("no such bot", StringComparison.Ordinal) ? Reply(404, new JsonObject { ["error"] = err }) : Bad(err);
        return Reply(200, new JsonObject
        {
            ["id"] = dir.Id, ["delivered"] = new JsonArray(dir.Delivered.Select(x => (JsonNode)x!).ToArray()), ["line"] = dir.Line, ["design"] = dir.Design,
        });
    }

    /// <summary>A directive request: the trusted player sending it, the directive, the bots it goes to, whether commands are allowed.</summary>
    private sealed record DirectiveInput(string Player, JsonObject Directive, List<string> Bots, bool Commands);

    /// <summary>The checks in order: a trusted player, a directive object, at least one bot, a well-formed request id.</summary>
    private (DirectiveInput? Input, SupervisorReply? Refused) DirectiveInputOf(JsonObject b, IViewerData v)
    {
        var player = v.TrustedPlayers.FirstOrDefault(p => p.Equals(S(b, "player")?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (player == null) return (null, Reply(403, new JsonObject { ["error"] = "player is not trusted (Agent:Self:TrustedPlayers)" }));
        if (b["directive"] is not JsonObject d) return (null, Bad("directive must be {kind, area?, targets?, filter?, build?, bill?, text?}"));
        var bots = b["bots"] is JsonArray ba ? ba.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() : [];
        if (bots.Count == 0) return (null, Bad("bots required"));
        if (S(b, "request_id") is { } rid && !System.Text.RegularExpressions.Regex.IsMatch(rid, @"^[A-Za-z0-9_.:\-]{1,64}$"))
            return (null, Bad("request_id: 1-64 of A-Z a-z 0-9 _ . : -"));
        return (new DirectiveInput(player, d, bots, CommandsAllowed(b)), null);
    }

    /// <summary>allow_commands, honoured only with <see cref="SupervisorViewerOptions.AllowCommands"/>.</summary>
    private bool CommandsAllowed(JsonObject b) => b["allow_commands"] is JsonValue ac && ac.TryGetValue<bool>(out var a) && a && o.Viewer.AllowCommands;

    /// <summary>The AI bodies (main, hives, their bodies) for the pawn bar; a colony's own pawns are not AI.</summary>
    private JsonArray Ai(StoryColony? only = null)
    {
        if (ai == null) return new JsonArray();
        var pawns = colonies.All.SelectMany(c => c.Pawns.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new JsonArray(ai().Where(r => !pawns.Contains(r.Name)).Select(r => (JsonNode)new JsonObject
        {
            ["name"] = r.Name, ["kind"] = r.IsMain ? "main" : r.Kind is "hive" or "subhive" ? r.Kind : "subhive", ["owner"] = r.Owner, ["online"] = r.Online,
            ["hp"] = r.Hp, ["food"] = r.Food, ["pos"] = r.HasPos ? new JsonArray(Math.Round(r.X, 2), Math.Round(r.Y, 2), Math.Round(r.Z, 2)) : null, ["dim"] = r.Dim,
            ["task"] = r.Job, ["brain"] = r.Brain, ["brain_state"] = !r.Online ? "offline" : r.Script != null ? "script" : r.Job != null ? "working" : "idle",
            ["script"] = r.Script?.Name,
        }).ToArray());
    }

    // ---------------------------------------------------------------- helpers

    private static string? S(JsonObject b, string k) => b[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static string? Q(SupervisorRequest r, string k) => r.Query.TryGetValue(k, out var v) ? v : null;
    private static SupervisorReply NoColony() => Reply(404, new JsonObject { ["error"] = "no such colony" });
    private static SupervisorReply Bad(string why) => Reply(400, new JsonObject { ["error"] = why });
    private static SupervisorReply Ok(string? err) => err != null ? Bad(err) : Reply(200, new JsonObject { ["ok"] = true });
    private static SupervisorReply Result((JsonNode? Result, string? Error) r) => r.Error != null
        ? Reply(r.Error.StartsWith("no world", StringComparison.Ordinal) ? 503 : 400, new JsonObject { ["error"] = r.Error })
        : Reply(200, r.Result ?? new JsonObject { ["ok"] = true });

    private static SupervisorReply NotAllowed(string allow) =>
        Reply(405, new JsonObject { ["error"] = $"{allow} only" }, new Dictionary<string, string> { ["Allow"] = allow });

    private static SupervisorReply Reply(int status, JsonNode value, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(value.ToJsonString(StoryJson.Options)), headers);
}
