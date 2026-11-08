using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent.Tools;
using MineClaw.Hive.Story;

namespace MineClaw.App.Supervisor;

/// <summary>The main brain controls the same persistent colony model as the Controller UI. No parallel task system.</summary>
public sealed partial class SupervisorStoryRoutes
{
    public void RegisterControllerTools(ToolRegistry tools)
    {
        tools.Register("colony_control",
            "Read and configure Controller colonies. list discovers ids; read shows a state section (paged); help explains an action's payload; " +
            "act runs that action through the Controller's validated handlers. Use for pawn roles/priorities/schedules, zones, builds, bills, gear and options. " +
            "Inspect first, use real ids, change only what the owner asked, and report results. These are registered colony actions, not scripts. " +
            "Payload keys are snake_case; actions create, time and time-range are global. UI layout/camera preferences are local and not colony actions.",
            ToolSchema.Create().Str("op", enumValues: ["list", "read", "help", "act"])
                .Str("colony", "Required with multiple colonies; use the id in the Controller request.", required: false)
                .Str("section", "read: summary (default), pawns, roles, zones, builds, ghosts, storages, workbenches, craft_tasks, policies, material_rules, options, beds, designations, orders, alerts, categories, plants, materials, work_types, architect or recipes.", required: false)
                .Str("action", "help/act: an action from help.", required: false)
                .Raw("body", """{"type":"object","description":"The action payload returned by help; all coordinates are whole world block coordinates."}""", required: false)
                .Int("offset", required: false).Int("limit", "Read page size, 1..50 (default 10).", required: false).Build(),
            ControllerToolAsync, tiers: ToolTiers.Main, maxResultChars: 24_000);
        tools.Register("story_directive", "Read the full owner request and exact targets behind a Controller/map directive id.",
            ToolSchema.Create().Str("id").Build(),
            (a, _) => Task.FromResult(directives?.Get(a.Str("id") ?? "") is { } d
                ? ToolResult.Success(directives.Lookup(d.Id)) : ToolResult.Error("no such directive")),
            tiers: ToolTiers.Main, maxResultChars: 12_000);
    }

    private async Task<ToolResult> ControllerToolAsync(JsonElement args, CancellationToken ct)
    {
        if (!o.Viewer.Enabled) return ToolResult.Error("Controller is disabled");
        var input = JsonNode.Parse(args.GetRawText())!.AsObject();
        string op = S(input, "op") ?? "", action = S(input, "action") ?? "";
        if (op == "list") return ToolResult.Success(Index().ToJsonString());
        if (op == "help") return ControllerHelp(action);
        if (op is not ("read" or "act")) return ToolResult.Error("op must be list|read|help|act");
        var body = input["body"] as JsonObject ?? new JsonObject();
        if (op == "act")
        {
            if (!Writes(o)) return ToolResult.Error("story writes are off (Supervisor:Viewer:AllowStory)");
            if (action == "create") return ControllerResult(Create(body));
            if (action == "time") return ControllerResult(await SetTimeAsync(body).ConfigureAwait(false));
            if (action == "time-range") return ControllerResult(await SetTimeRangeAsync(body).ConfigureAwait(false));
        }
        var id = S(input, "colony");
        var all = Colonies.All;
        var colony = id == null && all.Count == 1 ? all[0] : id == null ? null : Colonies.Get(id);
        if (colony == null) return ToolResult.Error("Choose a colony id from op:list.");
        ct.ThrowIfCancellationRequested();
        try
        {
            if (op == "act") return ControllerResult(Colony(colony, action, body));
            return ControllerRead(colony, S(input, "section") ?? "summary", input);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        { return ToolResult.Error("bad value: " + ex.Message); }
    }

    private static ToolResult ControllerResult(SupervisorReply r) => r.Status is >= 200 and < 300
        ? ToolResult.Success(r.Text) : ToolResult.Error(r.Text);

    private ToolResult ControllerRead(StoryColony c, string section, JsonObject input)
    {
        int offset = IntIn(input, "offset", 0, int.MaxValue) ?? 0, limit = IntIn(input, "limit", 1, 50) ?? 10;
        var state = c.State();
        if (section == "summary")
        {
            var summary = c.Row();
            summary["home"] = state["home"]?.DeepClone();
            summary["options"] = state["options"]?.DeepClone();
            summary["time_control"] = TimeControl();
            summary["sections"] = new JsonArray(state.Select(k => (JsonNode)k.Key).ToArray());
            summary["counts"] = new JsonObject(state.Where(k => k.Value is JsonArray)
                .Select(k => new KeyValuePair<string, JsonNode?>(k.Key, JsonValue.Create(((JsonArray)k.Value!).Count))));
            return ToolResult.Success(summary.ToJsonString());
        }
        JsonNode? value = section switch
        {
            "architect" => c.Architect()["entries"],
            "recipes" => Colonies.Host.World is { } w
                ? new JsonArray(StoryRecipes.For(w.Data, null).Select(r => (JsonNode)r.Json(w.Data)).ToArray()) : null,
            "ghosts" => state["ghosts"]?["blocks"],
            _ => state[section],
        };
        if (value == null) return ToolResult.Error("Unknown or unavailable section. Read summary for available sections.");
        var result = new JsonObject { ["colony"] = c.Id, ["section"] = section, ["rev"] = state["rev"]?.DeepClone() };
        if (value is JsonArray array)
        {
            result["total"] = array.Count;
            result["offset"] = offset;
            result["next_offset"] = offset + limit < array.Count ? offset + limit : null;
            result["items"] = new JsonArray(array.Skip(offset).Take(limit).Select(n => n?.DeepClone()).ToArray());
        }
        else result["value"] = value.DeepClone();
        return ToolResult.Success(result.ToJsonString());
    }

    private static ToolResult ControllerHelp(string action)
    {
        if (action.Length == 0) return ToolResult.Success(JsonSerializer.Serialize(new
        {
            actions = ControllerActions.Keys,
            usage = "op:help with action for its body. op:read section for current ids/settings. op:act applies one action. Omitted pawn list means all; always pass explicit names for a subset. Priorities: 0 off, 1 highest, 4 lowest. Schedules use hours 0..23 (midnight-based).",
        }));
        return ControllerActions.TryGetValue(action, out var help) ? ToolResult.Success(help) : ToolResult.Error("Unknown action; use op:help.");
    }

    private static readonly IReadOnlyDictionary<string, string> ControllerActions = new Dictionary<string, string>
    {
        ["priorities"] = "Assign role: {pawns:[name,...],role:role_id|null}; set individual priorities: {pawns:[name,...],work:{mine:1,haul:2}} (0 off,1 highest,4 lowest; editing work clears role). Read roles/work_types first.",
        ["roles"] = "{op:'add',role:{name,icon?,priorities:{work_type:0..4}}}; {op:'update',role:{id,name?,icon?,priorities?}}; {op:'remove',id}; {op:'reset_defaults'}. Updating a role updates all assigned pawns.",
        ["schedule"] = "{pawns:[name,...],hours:{'0':'sleep','1':'sleep','6':'work'}}. Hours 0..23 midnight-based; values work|sleep|anything|free. Only supplied hours change.",
        ["zones"] = "{op:'add',kind:'farm|fish|mine|chop|home|guard|preserve|forbidden',area:[x1,y1,z1,x2,y2,z2],name?,plant?,mode?:'ores|quarry'}; {op:'remove',id} or {op:'remove',kind,area}; {op:'rename',id,name}; {op:'update',id,plant?,sow?,harvest?,mode?}; {op:'preserve',id,on:true|false,below?,above?,retake?}; {op:'irrigation',id}; growing/fishing bills: {op:'bill_add',id,plant,mode:'count|until',target}; {op:'bill_update',id,bill:bill_id,mode?,target?}; bill_remove/bill_pause/bill_resume with id and bill:bill_id. Read zones/plants before changes.",
        ["builds"] = "{op:'add',items:[{kind,area:[x1,y1,z1,x2,y2,z2],material:[id,meta],height?,facing?:'n|e|s|w',name?}]}; {op:'pause|resume|remove|reorder',ids:[build_id,...]}; {op:'retry',id}; {op:'retry_all'}; {op:'unprotect',cells:[[x,y,z],...]}; {op:'edit',build_id?,cells:[[x,y,z,id,meta],...]}. edit is a planned exact block state; id=0 is persistent air. Removal cancels ownership; unprotect stops rebuilding selected cells. Read architect for supported kinds/materials.",
        ["designations"] = "{op:'add|cancel',kind:'mine|chop|cut|harvest|haul|hunt|deconstruct',area:[x1,y1,z1,x2,y2,z2],filter?}; or blocks:[[x,y,z],...]; cancel can use ids:[designation_id,...]. Inspect existing designations before cancelling.",
        ["orders"] = "{pawns:[name,...],order:{kind:'move|attack|chop|mine|haul|build|hold|stop',target?,design?},queue?:false}. Targets use coordinates or entity ids as appropriate. Prefer registered zones/designations/builds for ongoing colony work. Player/bot attacks may return a confirmation requirement; ask the owner before confirming.",
        ["draft"] = "{pawns:[name,...],drafted:true|false}",
        ["storage"] = "{op:'set|add|remove_entries',chests:[[x,y,z],...],filter:{allow:[category_or_item,...],deny:[]},priority?:'low|normal|preferred|important|critical',label?}; {op:'remove',chests}; {op:'copy',from:[x,y,z],to:[[x,y,z],...]}. colony_storage offers building-floor selection. Sign rules override settings until pawns remove the signs.",
        ["bills"] = "Read workbenches/recipes first. {op:'add',bench:[x,y,z],bill:{recipe,mode:'count|until',target,ingredients?:{allow:[],deny:[]},radius?,pawn?,min_skill?,store?}}; {op:'update',id,recipe?,mode?,target?,paused?,ingredients?,radius?,pawn?,min_skill?,store?,fuel?}; {op:'pause|resume|remove',id}; {op:'reorder',bench:[x,y,z],ids:[bill_id,...]}. Mode until keeps stock at target; count makes target once.",
        ["workbenches"] = "{op:'designate|release',pos:[x,y,z]}; furnace fuel: {op:'fuel',pos:[x,y,z],fuel:'auto|coal|...'} (valid fuel_choices are in read workbenches).",
        ["policies"] = "Read policies first. {op:'set',policy:{id?,name,tools_by_work?,tools?,weapon?,armor?,food?}}; {op:'assign',id:policy_id,pawns:[name,...]}; {op:'remove',id}.",
        ["material-rules"] = "Read material_rules/materials first. {op:'set',rule:{material,tool?,tier?,enchant?,fallback?:'plain|wait'}}; {op:'remove',material}. Preserve existing fields not requested.",
        ["gear"] = "{op:'arm|armor|pickup|return|tools',pawns:[name,...],item?,count?}. Read policies, materials and pawn inventories first.",
        ["beds"] = "{op:'assign',bed:{x,y,z},pawn:name}; {op:'unassign',bed:{x,y,z}}",
        ["options"] = "Partial settings: {auto_place_benches?,detect_benches?,auto_craft_tools?,spare_tools?:{count,kinds},idle?:'recreation|wander|stay',rescue_teleport?,unfiltered_storage_takes_anything?,keep_disallowed?,allow_lava?,pillar_stack?:0..64,stuck_teleport_after_s?:5..3600,speed?:'normal|fast|faster',speed_effect?}. Read options and change only requested fields.",
        ["pause"] = "{paused:true|false}",
        ["home"] = "{home:{x,y,z}} moves colony home and may relocate distant pawns. Only on the owner's request.",
        ["pawns"] = "{op:'add',count:positive_integer} or {op:'remove',pawn:name}. Changes colony roster; only on owner request.",
        ["online"] = "{pawns:[name,...]|null,over_cap?:false}. null brings all offline colony pawns online. Leave over_cap false unless explicitly requested.",
        ["release"] = "{} releases colony pawns while preserving saved colony data; only when requested.",
        ["resume"] = "{} resumes a released colony.",
        ["create"] = "{name,pawns:positive_integer,home?:{x,y,z}} creates a new colony; no colony id required.",
        ["time"] = "{time:0..23999}; global server day tick (0=06:00). Requires an available op account.",
        ["time-range"] = "{range:{start:0..23999,end:0..23999}} enables a loop (end must differ); {range:null} disables it. Global server time; requires op.",
    };
}
