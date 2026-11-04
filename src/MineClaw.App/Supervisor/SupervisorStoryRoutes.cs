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
public sealed class SupervisorStoryRoutes(SupervisorOptions o, StoryColonies colonies, StoryDirectives? directives = null,
    Func<IReadOnlyList<BodyRow>>? ai = null, IViewerData? viewer = null, TimeProvider? time = null, StoryTimeControl? timeControl = null)
{
    public const string Prefix = "/api/story";
    public const int MaxBodyBytes = 64 * 1024;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public StoryColonies Colonies => colonies;

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
        if (req.ContentLength64 > MaxBodyBytes) return SupervisorAdminRoutes.TooLarge;
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        while (true)
        {
            int r = await req.InputStream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (r == 0) break;
            ms.Write(buf, 0, r);
            if (ms.Length > MaxBodyBytes) return SupervisorAdminRoutes.TooLarge;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
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
        if (!o.Viewer.Enabled) return Reply(404, new JsonObject { ["error"] = "not found" });
        if (string.IsNullOrEmpty(o.Token)) return Reply(403, new JsonObject { ["error"] = "Story Mode needs Supervisor:Token" });
        if (!TokenOk(r.TokenHeader, o.Token)) return Reply(403, new JsonObject { ["error"] = "X-Supervisor-Token header required" });
        bool post = r.Method.Equals("POST", StringComparison.OrdinalIgnoreCase);
        bool get = r.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) || r.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);
        if (!post && !get) return NotAllowed("GET, HEAD, POST");
        if (post && !Writes(o)) return Reply(403, new JsonObject { ["error"] = "story writes are off (Supervisor:Viewer:AllowStory)" });
        JsonObject body = new();
        if (post)
        {
            if (r.Body == SupervisorAdminRoutes.TooLarge) return Reply(413, new JsonObject { ["error"] = $"body over {MaxBodyBytes} bytes" });
            if (!string.IsNullOrWhiteSpace(r.Body))
            {
                try { body = JsonNode.Parse(r.Body) as JsonObject ?? throw new JsonException(); }
                catch (JsonException) { return Bad("body must be a JSON object"); }
            }
        }
        var parts = path[Prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        try
        {
            switch (parts)
            {
                case []:
                    return get ? Reply(200, new JsonObject
                    {
                        ["colonies"] = new JsonArray(colonies.All.Select(c => (JsonNode)c.Row()).ToArray()), ["ai"] = Ai(), ["directives"] = directives?.Json() ?? new JsonArray(),
                        ["time_control"] = TimeControl(),
                    }) : NotAllowed("GET, HEAD");
                case ["time"]:
                    return post ? await SetTimeAsync(body).ConfigureAwait(false) : NotAllowed("POST");
                case ["recipes"]:
                {
                    if (!get) return NotAllowed("GET, HEAD");
                    if (colonies.Host.World is not { } w) return Reply(503, new JsonObject { ["error"] = "no world view yet" });
                    var bench = Q(r, "workbench");
                    if (bench != null && !StoryRecipes.Benches.Contains(bench)) return Bad($"workbench must be {string.Join("|", StoryRecipes.Benches)}");
                    return Reply(200, new JsonArray(StoryRecipes.For(w.Data, bench).Select(x => (JsonNode)x.Json(w.Data)).ToArray()));
                }
                case ["colonies"]:
                    return post ? Create(body) : NotAllowed("POST");
                case ["colonies", var id]:
                {
                    if (!get) return NotAllowed("GET, HEAD");
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
                    st["directives"] = directives?.Json() ?? new JsonArray();
                    return Reply(200, st);
                }
                case ["colonies", var id, "architect"]:
                {
                    // The Architect palette: every placeable with its tier, unlock state and the colony's stock of it.
                    if (!get) return NotAllowed("GET, HEAD");
                    if (colonies.Get(id) is not { } c) return NoColony();
                    if (colonies.Host.World == null) return Reply(503, new JsonObject { ["error"] = "no world view yet" });
                    return Reply(200, c.Architect());
                }
                case ["colonies", var id, var action]:
                {
                    if (!post) return NotAllowed("POST");
                    if (colonies.Get(id) is not { } c) return NoColony();
                    return Colony(c, action.ToLowerInvariant(), body);
                }
                case ["directives"]:
                    if (get) return Reply(200, new JsonObject { ["directives"] = directives?.Json() ?? new JsonArray() });
                    return await DirectiveAsync(body, ct).ConfigureAwait(false);
                case ["directives", var did]:
                {
                    if (!get) return NotAllowed("GET, HEAD");
                    directives?.Correlate();
                    if (directives?.Get(did) is not { } d) return Reply(404, new JsonObject { ["error"] = "no such directive" });
                    return Reply(200, new JsonObject
                    {
                        ["id"] = d.Id, ["at"] = d.At, ["kind"] = d.Kind, ["status"] = d.Status, ["summary"] = d.Summary, ["line"] = d.Line, ["reply"] = d.Reply,
                        ["design"] = d.Design, ["bots"] = new JsonArray(d.Bots.Select(b => (JsonNode)b!).ToArray()),
                        ["delivered"] = new JsonArray(d.Delivered.Select(b => (JsonNode)b!).ToArray()), ["detail"] = d.Detail?.DeepClone(),
                    });
                }
            }
            return Reply(404, new JsonObject { ["error"] = "not found" });
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        {
            return Bad("bad value: " + ex.Message); // a field of the wrong JSON type
        }
    }

    // ---------------------------------------------------------------- colonies

    private SupervisorReply Create(JsonObject b)
    {
        var name = b["name"]?.GetValue<string>();
        if (b["pawns"] is not JsonValue pv || !pv.TryGetValue<int>(out var n)) return Bad("pawns must be a number 1..12");
        P3? home = null;
        if (b["home"] is { } h && (home = P3.Parse(h)) == null) return Bad("home must be {x,y,z}");
        var (c, err) = colonies.Create(name ?? "", n, home);
        return c == null ? Reply(err is { } e && (e.StartsWith("name") || e.StartsWith("pawns")) ? 400 : 503, new JsonObject { ["error"] = err }) : Reply(200, new JsonObject { ["id"] = c.Id });
    }

    private SupervisorReply Colony(StoryColony c, string action, JsonObject b)
    {
        static List<string>? Names(JsonNode? n) => n is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").ToList() : null;
        (List<PawnState> Pawns, string? Error) Pawns() => c.Resolve(Names(b["pawns"]));
        switch (action)
        {
            case "release":
                return Reply(200, new JsonObject { ["id"] = c.Id, ["released"] = colonies.Release(c) });
            case "resume":
            {
                var why = colonies.Resume(c);
                return Reply(200, new JsonObject { ["id"] = c.Id, ["pawns"] = c.Pawns.Count, ["note"] = why });
            }
            case "pawns":
            {
                // {op:"add", count:1..12} | {op:"remove", pawn} → {pawns, wanted, pending:[{reason}], leaving, blocked:[{reason}]}.
                var op = S(b, "op") ?? "";
                if (op == "add")
                {
                    if (b["count"] is not JsonValue cv || !cv.TryGetValue<int>(out var n) || n is < 1 or > StoryColony.MaxPawns)
                        return Bad($"count must be 1..{StoryColony.MaxPawns}");
                    var (res, aerr) = colonies.AddPawns(c, n);
                    return aerr != null ? Bad(aerr) : Reply(200, res!);
                }
                if (op == "remove")
                {
                    if (S(b, "pawn") is not { Length: > 0 } who) return Bad("pawn required");
                    var (res, rerr, notFound) = colonies.RemovePawn(c, who);
                    return rerr != null ? notFound ? Reply(404, new JsonObject { ["error"] = rerr }) : Bad(rerr) : Reply(200, res!);
                }
                return Bad("op must be add|remove");
            }
            case "online":
            {
                // {pawns:[name]|null, over_cap?:bool} → {started:[account], blocked:[{pawn, reason}]}; null = every offline pawn.
                List<string>? names = null;
                if (b["pawns"] is JsonArray arr)
                {
                    names = new List<string>();
                    foreach (var x in arr)
                    {
                        if (x is not JsonValue xv || !xv.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) return Bad("pawns must be [name] or null");
                        names.Add(s);
                    }
                    if (names.Count == 0) return Bad("pawns must name at least one pawn, or be null for every offline pawn");
                }
                else if (b["pawns"] != null) return Bad("pawns must be [name] or null");
                bool overCap = false;
                if (b["over_cap"] is { } oc && (oc is not JsonValue ov || !ov.TryGetValue<bool>(out overCap))) return Bad("over_cap must be true|false");
                List<string>? accounts = null;
                if (names != null)
                {
                    var (ps, err) = c.Resolve(names);
                    if (err != null) return Reply(404, new JsonObject { ["error"] = err });
                    accounts = ps.Select(p => p.Name).ToList();
                }
                var (started, blocked) = colonies.BringOnline(c, accounts, overCap);
                return Reply(200, new JsonObject
                {
                    ["started"] = new JsonArray(started.Select(s => (JsonNode)s!).ToArray()),
                    ["blocked"] = new JsonArray(blocked.Select(x => (JsonNode)new JsonObject { ["pawn"] = x.Pawn, ["reason"] = x.Reason }).ToArray()),
                });
            }
            case "home":
            {
                // {home:{x,y,z}} → {home:[x,y,z]}: the colony's home moves (persisted); pawns far from it are brought there.
                if (b["home"] is not JsonObject ho || P3.Parse(ho) is not { } home) return Bad("home must be {x,y,z} (whole numbers)");
                if (home.Y is < -64 or > 320 || Math.Abs(home.X) > 30_000_000 || Math.Abs(home.Z) > 30_000_000) return Bad("home is outside the world");
                c.SetHome(home);
                return Reply(200, new JsonObject { ["home"] = home.Json() });
            }
            case "beds":
            {
                // {op:"assign", bed:{x,y,z}, pawn} | {op:"unassign", bed:{x,y,z}} → {beds:[…]}
                var op = S(b, "op") ?? "";
                if (op is not ("assign" or "unassign")) return Bad("op must be assign|unassign");
                if (b["bed"] is not JsonObject bo || P3.Parse(bo) is not { } bed) return Bad("bed must be {x,y,z}");
                if (op == "assign" && S(b, "pawn") is not { Length: > 0 }) return Bad("pawn required");
                var (res, err, notFound) = c.BedsOp(op, bed, S(b, "pawn"));
                if (err != null)
                    return notFound ? Reply(404, new JsonObject { ["error"] = err })
                        : err.StartsWith("no world", StringComparison.Ordinal) ? Reply(503, new JsonObject { ["error"] = err }) : Bad(err);
                return Reply(200, res!);
            }
            case "pause":
            {
                if (b["paused"] is not JsonValue v || !v.TryGetValue<bool>(out var paused)) return Bad("paused must be true|false");
                c.Pause(paused);
                return Reply(200, new JsonObject { ["paused"] = paused });
            }
            case "priorities":
            {
                var (ps, err) = Pawns();
                if (err != null) return Bad(err);
                // {pawns, role: id|null}: the pawns take a role's priorities, or turn Custom with the values they have (Roles).
                if (b.ContainsKey("role"))
                {
                    if (b["work"] != null) return Bad("role or work, not both");
                    if (b["role"] != null && S(b, "role") is not { Length: > 0 }) return Bad("role must be a role id or null");
                    var rerr = c.SetRole(ps, S(b, "role"));
                    return rerr != null && rerr.StartsWith("role: no such", StringComparison.Ordinal) ? Reply(404, new JsonObject { ["error"] = rerr }) : Ok(rerr);
                }
                if (b["work"] is not JsonObject work) return Bad("work must be {type: 0..4} (or role: id|null)");
                var map = work.ToDictionary(kv => kv.Key, kv => kv.Value is JsonValue x && x.TryGetValue<int>(out var i) ? i : -1);
                return Ok(c.SetPriorities(ps, map));
            }
            case "schedule":
            {
                var (ps, err) = Pawns();
                if (err != null) return Bad(err);
                if (b["hours"] is not JsonObject hours) return Bad("hours must be {\"0\"..\"23\": work|sleep|anything|free}");
                return Ok(c.SetSchedule(ps, hours.ToDictionary(kv => kv.Key, kv => kv.Value is JsonValue x && x.TryGetValue<string>(out var s) ? s : "")));
            }
            case "draft":
            {
                var (ps, err) = Pawns();
                if (err != null) return Bad(err);
                if (b["drafted"] is not JsonValue v || !v.TryGetValue<bool>(out var d)) return Bad("drafted must be true|false");
                c.Draft(ps, d);
                return Ok(null);
            }
            case "orders":
            {
                var (ps, err) = Pawns();
                if (err != null) return Bad(err);
                if (b["order"] is not JsonObject order) return Bad("order must be {kind, target?, design?}");
                var given = c.GiveOrder(ps, order, b["queue"] is JsonValue q && q.TryGetValue<bool>(out var qq) && qq);
                // An attack on a player or another bot: the owner confirms first (409 with what to confirm; resend with confirm: true).
                if (given.Error is { } oe && oe.StartsWith(StoryColony.ConfirmError + ":", StringComparison.Ordinal))
                    return Reply(409, new JsonObject { ["error"] = oe, ["confirm"] = given.Result?["confirm"]?.DeepClone() });
                return Result(given);
            }
            case "roles":
                return Result(c.RolesOp(b)); // work roles: add|update|remove|reset_defaults (StoryColony.Roles.cs)
            case "policies":
                return Result(c.PoliciesOp(b));
            case "material-rules":
                return Result(c.MaterialRulesOp(b));
            case "gear":
            {
                // {op: arm|armor|pickup|return|tools, pawns:[name], item?, count?} → {results:[{pawn, display, got, missing}], summary}
                var (ps, err) = Pawns();
                if (err != null) return err.StartsWith("no pawn", StringComparison.Ordinal) ? Reply(404, new JsonObject { ["error"] = err }) : Bad(err);
                return Result(c.GearOp(S(b, "op") ?? "", ps, b));
            }
            case "zones":
            {
                Area? area = b["area"] != null ? Area.Parse(b["area"]) : null;
                if (b["area"] != null && area == null) return Bad("area must be [x1,y1,z1,x2,y2,z2]");
                // Growing zones: plant, sow, harvest on add/update; seek_seeds {id} (docs/web/story-mode.md "Farming").
                return Result(c.ZoneOp(S(b, "op") ?? "", S(b, "kind"), area, S(b, "id"), S(b, "name"), b));
            }
            case "designations":
            {
                Area? area = b["area"] != null ? Area.Parse(b["area"]) : null;
                if (b["area"] != null && area == null) return Bad("area must be [x1,y1,z1,x2,y2,z2]");
                var blocks = b["blocks"] is JsonArray ba ? ba.Select(P3.Parse).ToList() : null;
                if (blocks != null && blocks.Any(x => x == null)) return Bad("blocks must be [[x,y,z], ...]");
                var op = S(b, "op") ?? "";
                return Result(c.Designate(op, S(b, "kind"), area, blocks?.Select(x => x!.Value).ToList(), S(b, "filter"), Names(b["ids"])));
            }
            case "builds":
            {
                // retry {id|ids, cells?} | retry_all: failed build cells start over (StoryColony.BuildRetry.cs)
                var bop = S(b, "op") ?? "";
                if (bop is "retry" or "retry_all")
                {
                    var cells = b["cells"] is JsonArray rc ? rc.Select(P3.Parse).ToList() : null;
                    if (cells != null && cells.Any(x => x == null)) return Bad("cells must be [[x,y,z], ...]");
                    var rids = S(b, "id") is { } one ? new List<string> { one } : Names(b["ids"]);
                    return Result(c.RetryBuilds(bop, rids, cells?.Select(x => x!.Value).ToList()));
                }
                return Result(c.BuildsOp(bop, b["items"] as JsonArray, Names(b["ids"])));
            }
            case "storage":
            {
                var op = S(b, "op") ?? "";
                List<P3>? chests = null;
                var list = op == "copy" ? b["to"] : b["chests"];
                if (list is JsonArray ca)
                {
                    chests = ca.Select(P3.Parse).Where(x => x != null).Select(x => x!.Value).ToList();
                    if (chests.Count != ca.Count) return Bad("chests must be [[x,y,z], ...]");
                }
                StorageFilter? filter = null;
                if (b["filter"] is JsonObject fo)
                {
                    filter = new StorageFilter
                    {
                        Allow = fo["allow"] is JsonArray al ? al.Select(x => x?.GetValue<string>() ?? "").ToList() : ["*"],
                        Deny = fo["deny"] is JsonArray dl ? dl.Select(x => x?.GetValue<string>() ?? "").ToList() : [],
                    };
                }
                Area? area = b["area"] != null ? Area.Parse(b["area"]) : null;
                return Result(c.StorageOp(op, chests, filter, S(b, "priority"), S(b, "label"), P3.Parse(b["from"]), area));
            }
            case "bills":
                return Result(c.BillsOp(S(b, "op") ?? "", b));
            case "workbenches":
            {
                // {op: designate|release, pos:[x,y,z]} → {pos, kind, designated}; {op: fuel, pos, fuel} → + fuel (a furnace's fuel choice)
                if (P3.Parse(b["pos"]) is not { } pos) return Bad("pos must be [x,y,z]");
                return Result(c.WorkbenchOp(S(b, "op") ?? "", pos, S(b, "fuel")));
            }
            case "options":
            {
                // {auto_place_benches?: bool, detect_benches?: bool} → the options
                bool? Flag(string k)
                {
                    if (b[k] is null) return null;
                    return b[k] is JsonValue v && v.TryGetValue<bool>(out var f) ? f : throw new FormatException($"{k} must be true|false");
                }
                var auto = Flag("auto_place_benches");
                var detect = Flag("detect_benches");
                var craftTools = Flag("auto_craft_tools");
                var spare = b["spare_tools"] as JsonObject;
                if (b["spare_tools"] != null && spare == null) return Bad("spare_tools must be {count, kinds}");
                // idle: recreation | wander | stay (what pawns do with nothing to do; StoryColony.Recreation.cs)
                var idle = b["idle"] is null ? null : b["idle"] is JsonValue iv && iv.TryGetValue<string>(out var im) ? im : "";
                // {rescue_teleport?, unfiltered_storage_takes_anything?, keep_disallowed?: bool} (StoryColony.Hauling.cs)
                var rescue = Flag("rescue_teleport");
                var unfiltered = Flag("unfiltered_storage_takes_anything");
                var keep = Flag("keep_disallowed");
                var lava = Flag("allow_lava"); // pawns may pour lava (StoryColony.Water.cs)
                // pillar_stack: 0..64 blocks a pawn keeps to nerd-pole with (StoryColony.Reach.cs)
                int? pillarStack = b["pillar_stack"] is null ? null : b["pillar_stack"] is JsonValue pv && pv.TryGetValue<int>(out var pn) && pn is >= 0 and <= 64
                    ? pn : throw new FormatException("pillar_stack must be 0..64");
                // stuck_teleport_after_s: the escape budget before the teleport home (5..3600 s, StoryColony.Rescue.cs)
                int? stuckAfter = b["stuck_teleport_after_s"] is null ? null : b["stuck_teleport_after_s"] is JsonValue stv && stv.TryGetValue<int>(out var stn) && stn is >= 5 and <= 3600
                    ? stn : throw new FormatException("stuck_teleport_after_s must be 5..3600");
                // speed: normal | fast | faster, speed_effect: bool (StoryColony.Speed.cs)
                var speed = b["speed"] is null ? null : b["speed"] is JsonValue sv && sv.TryGetValue<string>(out var sm) ? sm : "";
                var speedEffect = Flag("speed_effect");
                if (auto == null && detect == null && idle == null && craftTools == null && spare == null && rescue == null && unfiltered == null && keep == null
                    && pillarStack == null && stuckAfter == null && speed == null && speedEffect == null && lava == null)
                    return Bad("auto_place_benches, detect_benches, idle, auto_craft_tools, spare_tools, rescue_teleport, unfiltered_storage_takes_anything, keep_disallowed, pillar_stack, stuck_teleport_after_s, speed, speed_effect or allow_lava required");
                if (c.SetSpeed(speed, speedEffect) is { } speedErr) return Bad(speedErr);
                if (lava != null) c.SetWaterOptions(lava);
                if (c.SetIdle(idle) is { } idleErr) return Bad(idleErr);
                if ((craftTools != null || spare != null) && c.SetGearOptions(craftTools, spare) is { } gerr) return Bad(gerr);
                if (rescue != null || unfiltered != null || keep != null || pillarStack != null || stuckAfter != null)
                    c.SetHaulOptions(rescue, unfiltered, keep, pillarStack, stuckAfter);
                return Reply(200, auto == null && detect == null ? c.OptionsNow() : c.SetOptions(auto, detect));
            }
            default:
                return Reply(404, new JsonObject { ["error"] = "not found" });
        }
    }

    // ---------------------------------------------------------------- time of day

    /// <summary><c>{available, by}</c>: an op account the bot controls can set the time (main first).</summary>
    private JsonObject TimeControl()
    {
        var (available, by) = timeControl?.Status() ?? (false, null);
        return new JsonObject { ["available"] = available, ["by"] = by };
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
        var player = viewer.TrustedPlayers.FirstOrDefault(p => p.Equals(S(b, "player")?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (player == null) return Reply(403, new JsonObject { ["error"] = "player is not trusted (Agent:Self:TrustedPlayers)" });
        if (b["directive"] is not JsonObject d) return Bad("directive must be {kind, area?, targets?, filter?, build?, bill?, text?}");
        var bots = b["bots"] is JsonArray ba ? ba.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() : [];
        if (bots.Count == 0) return Bad("bots required");
        if (S(b, "request_id") is { } rid && !System.Text.RegularExpressions.Regex.IsMatch(rid, @"^[A-Za-z0-9_.:\-]{1,64}$")) return Bad("request_id: 1-64 of A-Z a-z 0-9 _ . : -");
        bool cmds = b["allow_commands"] is JsonValue ac && ac.TryGetValue<bool>(out var a) && a && o.Viewer.AllowCommands;
        var (dir, err) = await directives.SendAsync(bots, d, player, cmds, S(b, "request_id"), ct).ConfigureAwait(false);
        if (dir == null) return err!.StartsWith("no such bot", StringComparison.Ordinal) ? Reply(404, new JsonObject { ["error"] = err }) : Bad(err);
        return Reply(200, new JsonObject
        {
            ["id"] = dir.Id, ["delivered"] = new JsonArray(dir.Delivered.Select(x => (JsonNode)x!).ToArray()), ["line"] = dir.Line, ["design"] = dir.Design,
        });
    }

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
