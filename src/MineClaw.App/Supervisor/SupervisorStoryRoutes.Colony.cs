using System.Text.Json.Nodes;
using MineClaw.Hive.Story;

namespace MineClaw.App.Supervisor;

/// <summary>
/// <c>POST /api/story/colonies/{id}/{action}</c>: one handler per action, looked up by the lower-cased action name (an unknown
/// action is 404). Each handler validates its body and calls the colony; the error texts and statuses are what the web reads.
/// </summary>
public sealed partial class SupervisorStoryRoutes
{
    private delegate SupervisorReply ColonyHandler(SupervisorStoryRoutes routes, StoryColony c, JsonObject b);

    private static readonly IReadOnlyDictionary<string, ColonyHandler> ColonyActions = new Dictionary<string, ColonyHandler>(StringComparer.Ordinal)
    {
        ["release"] = (s, c, _) => Reply(200, new JsonObject { ["id"] = c.Id, ["released"] = s.Colonies.Release(c) }),
        ["resume"] = (s, c, _) => s.ResumeColony(c),
        ["pawns"] = (s, c, b) => s.PawnsAction(c, b),
        ["online"] = (s, c, b) => s.OnlineAction(c, b),
        ["home"] = (_, c, b) => HomeAction(c, b),
        ["beds"] = (_, c, b) => BedsAction(c, b),
        ["pause"] = (_, c, b) => PauseAction(c, b),
        ["priorities"] = (_, c, b) => PrioritiesAction(c, b),
        ["schedule"] = (_, c, b) => ScheduleAction(c, b),
        ["draft"] = (_, c, b) => DraftAction(c, b),
        ["orders"] = (_, c, b) => OrdersAction(c, b),
        ["roles"] = (_, c, b) => Result(c.RolesOp(b)), // work roles: add|update|remove|reset_defaults (StoryColony.Roles.cs)
        ["policies"] = (_, c, b) => Result(c.PoliciesOp(b)),
        ["material-rules"] = (_, c, b) => Result(c.MaterialRulesOp(b)),
        ["gear"] = (_, c, b) => GearAction(c, b),
        ["zones"] = (_, c, b) => ZonesAction(c, b),
        ["designations"] = (_, c, b) => DesignationsAction(c, b),
        ["builds"] = (_, c, b) => BuildsAction(c, b),
        ["storage"] = (_, c, b) => StorageAction(c, b),
        ["bills"] = (_, c, b) => Result(c.BillsOp(S(b, "op") ?? "", b)),
        ["workbenches"] = (_, c, b) => WorkbenchesAction(c, b),
        ["options"] = (_, c, b) => OptionsAction(c, b),
    };

    private SupervisorReply Colony(StoryColony c, string action, JsonObject b) =>
        ColonyActions.TryGetValue(action, out var handle) ? handle(this, c, b) : Reply(404, new JsonObject { ["error"] = "not found" });

    private static List<string>? Names(JsonNode? n) => n is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").ToList() : null;

    private static (List<PawnState> Pawns, string? Error) PawnsOf(StoryColony c, JsonObject b) => c.Resolve(Names(b["pawns"]));

    private static SupervisorReply NotFound(string error) => Reply(404, new JsonObject { ["error"] = error });

    /// <summary>A list of <c>[x,y,z]</c> (null when absent); <paramref name="bad"/> when an entry isn't one.</summary>
    private static List<P3>? Points(JsonNode? n, out bool bad)
    {
        var list = n is JsonArray a ? a.Select(P3.Parse).ToList() : null;
        bad = list != null && list.Any(x => x == null);
        return bad || list == null ? null : list.Select(x => x!.Value).ToList();
    }

    /// <summary>The optional <c>area</c> (null when absent); <paramref name="bad"/> when present and not an area.</summary>
    private static Area? AreaOf(JsonObject b, out bool bad)
    {
        Area? area = b["area"] != null ? Area.Parse(b["area"]) : null;
        bad = b["area"] != null && area == null;
        return area;
    }

    private SupervisorReply ResumeColony(StoryColony c)
    {
        var why = Colonies.Resume(c);
        return Reply(200, new JsonObject { ["id"] = c.Id, ["pawns"] = c.Pawns.Count, ["note"] = why });
    }

    // {op:"add", count ≥ 1 (no upper limit)} | {op:"remove", pawn} → {pawns, wanted, pending:[{reason}], leaving, blocked:[{reason}]}.
    private SupervisorReply PawnsAction(StoryColony c, JsonObject b) => (S(b, "op") ?? "") switch
    {
        "add" => AddPawns(c, b),
        "remove" => RemovePawn(c, b),
        _ => Bad("op must be add|remove"),
    };

    private SupervisorReply AddPawns(StoryColony c, JsonObject b)
    {
        if (b["count"] is not JsonValue cv || !cv.TryGetValue<int>(out var n) || n < 1)
            return Bad("count must be a whole number, 1 or more");
        var (res, err) = Colonies.AddPawns(c, n);
        return err != null ? Bad(err) : Reply(200, res!);
    }

    private SupervisorReply RemovePawn(StoryColony c, JsonObject b)
    {
        if (S(b, "pawn") is not { Length: > 0 } who) return Bad("pawn required");
        var (res, err, notFound) = Colonies.RemovePawn(c, who);
        if (err == null) return Reply(200, res!);
        return notFound ? NotFound(err) : Bad(err);
    }

    // {pawns:[name]|null, over_cap?:bool} → {started:[account], blocked:[{pawn, reason}]}; null = every offline pawn.
    private SupervisorReply OnlineAction(StoryColony c, JsonObject b)
    {
        var (names, namesErr) = OnlineNames(b["pawns"]);
        if (namesErr != null) return Bad(namesErr);
        bool overCap = false;
        if (b["over_cap"] is { } oc && (oc is not JsonValue ov || !ov.TryGetValue<bool>(out overCap))) return Bad("over_cap must be true|false");
        List<string>? accounts = null;
        if (names != null)
        {
            var (ps, err) = c.Resolve(names);
            if (err != null) return NotFound(err);
            accounts = ps.Select(p => p.Name).ToList();
        }
        var (started, blocked) = Colonies.BringOnline(c, accounts, overCap);
        return Reply(200, new JsonObject
        {
            ["started"] = new JsonArray(started.Select(s => (JsonNode)s!).ToArray()),
            ["blocked"] = new JsonArray(blocked.Select(x => (JsonNode)new JsonObject { ["pawn"] = x.Pawn, ["reason"] = x.Reason }).ToArray()),
        });
    }

    private static (List<string>? Names, string? Error) OnlineNames(JsonNode? pawns)
    {
        if (pawns == null) return (null, null);
        if (pawns is not JsonArray arr) return (null, "pawns must be [name] or null");
        var names = new List<string>();
        foreach (var x in arr)
        {
            if (x is not JsonValue xv || !xv.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) return (null, "pawns must be [name] or null");
            names.Add(s);
        }
        return names.Count == 0 ? (null, "pawns must name at least one pawn, or be null for every offline pawn") : (names, null);
    }

    // {home:{x,y,z}} → {home:[x,y,z]}: the colony's home moves (persisted); pawns far from it are brought there.
    private static SupervisorReply HomeAction(StoryColony c, JsonObject b)
    {
        if (b["home"] is not JsonObject ho || P3.Parse(ho) is not { } home) return Bad("home must be {x,y,z} (whole numbers)");
        if (home.Y is < -64 or > 320 || Math.Abs(home.X) > 30_000_000 || Math.Abs(home.Z) > 30_000_000) return Bad("home is outside the world");
        c.SetHome(home);
        return Reply(200, new JsonObject { ["home"] = home.Json() });
    }

    // {op:"assign", bed:{x,y,z}, pawn} | {op:"unassign", bed:{x,y,z}} → {beds:[…]}
    private static SupervisorReply BedsAction(StoryColony c, JsonObject b)
    {
        var op = S(b, "op") ?? "";
        if (op is not ("assign" or "unassign")) return Bad("op must be assign|unassign");
        if (b["bed"] is not JsonObject bo || P3.Parse(bo) is not { } bed) return Bad("bed must be {x,y,z}");
        if (op == "assign" && S(b, "pawn") is not { Length: > 0 }) return Bad("pawn required");
        var (res, err, notFound) = c.BedsOp(op, bed, S(b, "pawn"));
        return err == null ? Reply(200, res!) : BedsError(err, notFound);
    }

    private static SupervisorReply BedsError(string err, bool notFound)
    {
        if (notFound) return NotFound(err);
        return err.StartsWith("no world", StringComparison.Ordinal) ? Reply(503, new JsonObject { ["error"] = err }) : Bad(err);
    }

    private static SupervisorReply PauseAction(StoryColony c, JsonObject b)
    {
        if (b["paused"] is not JsonValue v || !v.TryGetValue<bool>(out var paused)) return Bad("paused must be true|false");
        c.Pause(paused);
        return Reply(200, new JsonObject { ["paused"] = paused });
    }

    private static SupervisorReply PrioritiesAction(StoryColony c, JsonObject b)
    {
        var (ps, err) = PawnsOf(c, b);
        if (err != null) return Bad(err);
        if (b.ContainsKey("role")) return RoleAction(c, b, ps);
        if (b["work"] is not JsonObject work) return Bad("work must be {type: 0..4} (or role: id|null)");
        var map = work.ToDictionary(kv => kv.Key, kv => kv.Value is JsonValue x && x.TryGetValue<int>(out var i) ? i : -1);
        return Ok(c.SetPriorities(ps, map));
    }

    // {pawns, role: id|null}: the pawns take a role's priorities, or turn Custom with the values they have (Roles).
    private static SupervisorReply RoleAction(StoryColony c, JsonObject b, List<PawnState> ps)
    {
        if (b["work"] != null) return Bad("role or work, not both");
        if (b["role"] != null && S(b, "role") is not { Length: > 0 }) return Bad("role must be a role id or null");
        var rerr = c.SetRole(ps, S(b, "role"));
        return rerr != null && rerr.StartsWith("role: no such", StringComparison.Ordinal) ? NotFound(rerr) : Ok(rerr);
    }

    private static SupervisorReply ScheduleAction(StoryColony c, JsonObject b)
    {
        var (ps, err) = PawnsOf(c, b);
        if (err != null) return Bad(err);
        if (b["hours"] is not JsonObject hours) return Bad("hours must be {\"0\"..\"23\": work|sleep|anything|free}");
        return Ok(c.SetSchedule(ps, hours.ToDictionary(kv => kv.Key, kv => kv.Value is JsonValue x && x.TryGetValue<string>(out var s) ? s : "")));
    }

    private static SupervisorReply DraftAction(StoryColony c, JsonObject b)
    {
        var (ps, err) = PawnsOf(c, b);
        if (err != null) return Bad(err);
        if (b["drafted"] is not JsonValue v || !v.TryGetValue<bool>(out var d)) return Bad("drafted must be true|false");
        c.Draft(ps, d);
        return Ok(null);
    }

    private static SupervisorReply OrdersAction(StoryColony c, JsonObject b)
    {
        var (ps, err) = PawnsOf(c, b);
        if (err != null) return Bad(err);
        if (b["order"] is not JsonObject order) return Bad("order must be {kind, target?, design?}");
        var given = c.GiveOrder(ps, order, b["queue"] is JsonValue q && q.TryGetValue<bool>(out var qq) && qq);
        // An attack on a player or another bot: the owner confirms first (409 with what to confirm; resend with confirm: true).
        if (given.Error is { } oe && oe.StartsWith(StoryColony.ConfirmError + ":", StringComparison.Ordinal))
            return Reply(409, new JsonObject { ["error"] = oe, ["confirm"] = given.Result?["confirm"]?.DeepClone() });
        return Result(given);
    }

    // {op: arm|armor|pickup|return|tools, pawns:[name], item?, count?} → {results:[{pawn, display, got, missing}], summary}
    private static SupervisorReply GearAction(StoryColony c, JsonObject b)
    {
        var (ps, err) = PawnsOf(c, b);
        if (err != null) return err.StartsWith("no pawn", StringComparison.Ordinal) ? NotFound(err) : Bad(err);
        return Result(c.GearOp(S(b, "op") ?? "", ps, b));
    }

    // Growing zones: plant, sow, harvest on add/update; seek_seeds {id} (docs/web/story-mode.md "Farming").
    private static SupervisorReply ZonesAction(StoryColony c, JsonObject b)
    {
        var area = AreaOf(b, out var badArea);
        if (badArea) return Bad("area must be [x1,y1,z1,x2,y2,z2]");
        return Result(c.ZoneOp(S(b, "op") ?? "", S(b, "kind"), area, S(b, "id"), S(b, "name"), b));
    }

    private static SupervisorReply DesignationsAction(StoryColony c, JsonObject b)
    {
        var area = AreaOf(b, out var badArea);
        if (badArea) return Bad("area must be [x1,y1,z1,x2,y2,z2]");
        var blocks = Points(b["blocks"], out var badBlocks);
        if (badBlocks) return Bad("blocks must be [[x,y,z], ...]");
        return Result(c.Designate(S(b, "op") ?? "", S(b, "kind"), area, blocks, S(b, "filter"), Names(b["ids"])));
    }

    // retry {id|ids, cells?} | retry_all: failed build cells start over (StoryColony.BuildRetry.cs)
    private static SupervisorReply BuildsAction(StoryColony c, JsonObject b)
    {
        var bop = S(b, "op") ?? "";
        if (bop == "edit")
        {
            if (b["build_id"] != null && (b["build_id"] is not JsonValue id || !id.TryGetValue<string>(out _))) return Bad("build_id must be a string");
            return Result(c.EditBuildCells(S(b, "build_id"), b["cells"] as JsonArray));
        }
        if (bop is not ("retry" or "retry_all" or "unprotect")) return Result(c.BuildsOp(bop, b["items"] as JsonArray, Names(b["ids"])));
        var cells = Points(b["cells"], out var badCells);
        if (badCells) return Bad("cells must be [[x,y,z], ...]");
        if (bop == "unprotect") return Result(c.UnprotectCells(cells));
        var rids = S(b, "id") is { } one ? new List<string> { one } : Names(b["ids"]);
        return Result(c.RetryBuilds(bop, rids, cells));
    }

    private static SupervisorReply StorageAction(StoryColony c, JsonObject b)
    {
        var op = S(b, "op") ?? "";
        List<P3>? chests = null;
        if ((op == "copy" ? b["to"] : b["chests"]) is JsonArray ca)
        {
            chests = ca.Select(P3.Parse).Where(x => x != null).Select(x => x!.Value).ToList();
            if (chests.Count != ca.Count) return Bad("chests must be [[x,y,z], ...]");
        }
        var filter = StorageFilterOf(b["filter"]);
        if (filter != null && op is "add" or "remove_entries" && b["filter"]?["allow"] == null) filter.Allow = [];
        Area? area = b["area"] != null ? Area.Parse(b["area"]) : null;
        return Result(c.StorageOp(op, chests, filter, S(b, "priority"), S(b, "label"), P3.Parse(b["from"]), area));
    }

    private static StorageFilter? StorageFilterOf(JsonNode? n) => n is JsonObject fo
        ? new StorageFilter
        {
            Allow = fo["allow"] is JsonArray al ? al.Select(x => x?.GetValue<string>() ?? "").ToList() : ["*"],
            Deny = fo["deny"] is JsonArray dl ? dl.Select(x => x?.GetValue<string>() ?? "").ToList() : [],
        }
        : null;

    // {op: designate|release, pos:[x,y,z]} → {pos, kind, designated}; {op: fuel, pos, fuel} → + fuel (a furnace's fuel choice)
    private static SupervisorReply WorkbenchesAction(StoryColony c, JsonObject b)
    {
        if (P3.Parse(b["pos"]) is not { } pos) return Bad("pos must be [x,y,z]");
        return Result(c.WorkbenchOp(S(b, "op") ?? "", pos, S(b, "fuel")));
    }
    // ---------------------------------------------------------------- options

    /// <summary>The <c>options</c> body: each field null when absent. Parsing throws <see cref="FormatException"/> for a bad flag or
    /// number (the caller answers 400 "bad value: …"), in field order.</summary>
    private sealed record ColonyOptions(bool? Auto, bool? Detect, bool? CraftTools, JsonObject? Spare, string? Idle, bool? Rescue, bool? Unfiltered,
        bool? Keep, bool? Lava, int? PillarStack, int? StuckAfter, string? Speed, bool? SpeedEffect)
    {
        public bool Empty => new object?[] { Auto, Detect, Idle, CraftTools, Spare, Rescue, Unfiltered, Keep, PillarStack, StuckAfter, Speed, SpeedEffect, Lava }
            .All(x => x == null);
        public bool Haul => new object?[] { Rescue, Unfiltered, Keep, PillarStack, StuckAfter }.Any(x => x != null);
    }

    private static bool? Flag(JsonObject b, string k)
    {
        if (b[k] is null) return null;
        return b[k] is JsonValue v && v.TryGetValue<bool>(out var f) ? f : throw new FormatException($"{k} must be true|false");
    }

    /// <summary>A string field: null when absent, "" when not a string (the colony rejects it).</summary>
    private static string? Text(JsonObject b, string k)
    {
        if (b[k] is null) return null;
        return b[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    }

    private static int? IntIn(JsonObject b, string k, int min, int max)
    {
        if (b[k] is null) return null;
        return b[k] is JsonValue v && v.TryGetValue<int>(out var n) && n >= min && n <= max ? n : throw new FormatException($"{k} must be {min}..{max}");
    }

    // {auto_place_benches?, detect_benches?, auto_craft_tools?: bool, spare_tools?: {count, kinds}} (StoryColony.Gear.cs);
    // idle: recreation | wander | stay (what pawns do with nothing to do; StoryColony.Recreation.cs);
    // {rescue_teleport?, unfiltered_storage_takes_anything?, keep_disallowed?: bool} (StoryColony.Hauling.cs);
    // allow_lava: pawns may pour lava (StoryColony.Water.cs); pillar_stack: 0..64 blocks a pawn keeps to nerd-pole with (StoryColony.Reach.cs);
    // stuck_teleport_after_s: the escape budget before the teleport home (5..3600 s, StoryColony.Rescue.cs);
    // speed: normal | fast | faster, speed_effect: bool (StoryColony.Speed.cs).
    private static (ColonyOptions? Options, string? Error) ParseOptions(JsonObject b)
    {
        var auto = Flag(b, "auto_place_benches");
        var detect = Flag(b, "detect_benches");
        var craftTools = Flag(b, "auto_craft_tools");
        var spare = b["spare_tools"] as JsonObject;
        if (b["spare_tools"] != null && spare == null) return (null, "spare_tools must be {count, kinds}");
        var idle = Text(b, "idle");
        var rescue = Flag(b, "rescue_teleport");
        var unfiltered = Flag(b, "unfiltered_storage_takes_anything");
        var keep = Flag(b, "keep_disallowed");
        var lava = Flag(b, "allow_lava");
        var pillarStack = IntIn(b, "pillar_stack", 0, 64);
        var stuckAfter = IntIn(b, "stuck_teleport_after_s", 5, 3600);
        var speed = Text(b, "speed");
        var speedEffect = Flag(b, "speed_effect");
        return (new ColonyOptions(auto, detect, craftTools, spare, idle, rescue, unfiltered, keep, lava, pillarStack, stuckAfter, speed, speedEffect), null);
    }

    // {auto_place_benches?: bool, detect_benches?: bool, …} → the options
    private static SupervisorReply OptionsAction(StoryColony c, JsonObject b)
    {
        var (o, err) = ParseOptions(b);
        if (o == null) return Bad(err!);
        if (o.Empty)
            return Bad("auto_place_benches, detect_benches, idle, auto_craft_tools, spare_tools, rescue_teleport, unfiltered_storage_takes_anything, keep_disallowed, pillar_stack, stuck_teleport_after_s, speed, speed_effect or allow_lava required");
        if (ApplyOptions(c, o) is { } applyErr) return Bad(applyErr);
        return Reply(200, o.Auto == null && o.Detect == null ? c.OptionsNow() : c.SetOptions(o.Auto, o.Detect));
    }

    /// <summary>Speed, water, idle, gear, then hauling; the first error stops it (what came before stays applied).</summary>
    private static string? ApplyOptions(StoryColony c, ColonyOptions o)
    {
        if (c.SetSpeed(o.Speed, o.SpeedEffect) is { } speedErr) return speedErr;
        if (o.Lava != null) c.SetWaterOptions(o.Lava);
        if (c.SetIdle(o.Idle) is { } idleErr) return idleErr;
        if ((o.CraftTools != null || o.Spare != null) && c.SetGearOptions(o.CraftTools, o.Spare) is { } gerr) return gerr;
        if (o.Haul) c.SetHaulOptions(o.Rescue, o.Unfiltered, o.Keep, o.PillarStack, o.StuckAfter);
        return null;
    }
}
