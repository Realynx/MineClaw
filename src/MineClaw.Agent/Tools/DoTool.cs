using System.Text.Json;
using System.Text.Json.Nodes;

namespace MineClaw.Agent.Tools;

/// <summary>
/// <c>do {action, args, then?, queue?}</c>: the brain runs a common single-step game macro itself instead of paying a Sonnet
/// round through <c>delegate</c>, optionally chained ("walk there, then work": a step never runs if the one before it
/// failed). One compact definition (≈200 tokens) replaces the brain's direct goto/follow/chop_tree/act tools. The brain
/// loop (<see cref="BotAgent"/>) expands it into the real tool calls, so the in-flight guard, failure memory, the body lock,
/// move queueing and every game-side safety gate apply exactly as if the tools had been called one by one.
/// </summary>
public static class DoTool
{
    public const string ToolName = "do";

    /// <summary>Macros <c>do</c> may run (only those registered are offered).</summary>
    public static readonly string[] Actions =
    [
        "goto", "follow", "mine", "harvest_generator", "chop_tree", "deposit", "withdraw", "move_items", "craft", "act", "see",
        // Food and hand-offs for every bot and tier (live 10-27 21:07: clone 67 was handed bread and said it couldn't eat).
        "eat", "give_items", "drop_items", "equip_armor",
        // light = the light_area skill: torch an area/design floor so mobs can't spawn; report=true = the spawn map only
        // (live 10-28 16:45, task #29). The short name keeps the brain's tool budget.
        "light",
        // dig = the excavate skill: a box, a remembered stair pattern to a depth, a room off a landing, a strip mine, seal a dig's
        // cave holes (live 10-30 CT 114: the shaft to diamond level went through the worker cell by cell with invented coordinates).
        "dig",
    ];

    /// <summary>Actions that are skills of the catalog (run through the worker's <c>skill</c> tool).</summary>
    public static readonly IReadOnlySet<string> SkillActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "move_items", "give_items", "drop_items", "equip_armor", "light", "dig" };

    /// <summary>A <c>do</c> action whose catalog skill has a longer name.</summary>
    public static string SkillName(string action) => action switch { "light" => "light_area", "dig" => "excavate", _ => action };

    public const string Description =
        "Run a game macro yourself (delegate multi-step jobs). then: next steps, skipped if one fails. goto returns at once " +
        "(walks on in the background; jobs called now run on arrival). queue=true: after the busy script. " +
        "goto{to,dist?,build?,allow_parkour?(only if a trusted player asked),into_void?:reason,keep_items_ok?} follow{player} mine{block,count?} " +
        "harvest_generator{at,count,store?} chop_tree{at?} deposit{at?,items?} withdraw{item,count?,at?} craft{item,count?} " +
        "move_items{moves} act{move:jump|spin|wave|crouch|look|punch,target?} see{view?} eat give_items{player,item,count?} " +
        "drop_items{item,count?} equip_armor light{area|design,report?} dig{op?:seal|room|strip|learn,box|site,to?}";

    public static JsonElement Schema(IEnumerable<string> actions) => ToolSchema.Create()
        .Str("action", enumValues: actions)
        .Raw("args", """{"type":"object"}""", required: false)
        .Raw("then", """{"type":"array","items":{"type":"object"}}""", required: false)
        .Bool("queue", required: false)
        .Build();

    /// <summary>Registers <c>do</c> (brain only) over the macros that exist in <paramref name="tools"/>.</summary>
    public static void Register(ToolRegistry tools)
    {
        var actions = Actions.Where(a => tools.Contains(a) || (SkillActions.Contains(a) && tools.Contains("skill"))).ToArray();
        if (actions.Length == 0) return;
        tools.SetInputAdapter(ToolName, NormalizeInput);
        tools.Register(new DelegateTool(ToolName, Description, Schema(actions), async (input, ct) =>
        {
            // Outside the brain loop (a script, a test): run the steps in order, stopping at the first failure.
            var steps = Expand(input, out var error);
            if (error != null) return ToolResult.Error(error);
            var parts = new List<string>();
            foreach (var (tool, args) in steps)
            {
                var r = await tools.ExecuteAsync(tool, args, ct).ConfigureAwait(false);
                parts.Add(r.Text);
                if (!r.Ok) return ToolResult.Error(string.Join("; then ", parts));
            }
            return ToolResult.Success(string.Join("; then ", parts));
        }, longRunning: false, tiers: ToolTiers.Main));
    }

    /// <summary>True when the input asks to wait for a busy script/job instead of answering "body busy".</summary>
    public static bool Queue(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty("queue", out var q) && q.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Expands a <c>do</c> input into (tool, args) steps. Accepts the schema form <c>{action, args, then:[{action,args}]}</c>
    /// and the shorthand <c>{goto:"cobble gen", then:{harvest_generator:{count:64}}}</c>; a string/number arg becomes the
    /// tool's main parameter ("to", "player", "block", "item", "move", "at"). Error = a message for the model.
    /// </summary>
    public static List<(string Tool, JsonElement Input)> Expand(JsonElement input, out string? error)
    {
        error = null;
        var steps = new List<(string, JsonElement)>();
        if (input.ValueKind != JsonValueKind.Object) { error = "do needs {action, args}"; return steps; }
        if (!AddStep(input, steps, out error)) return steps;
        if (input.TryGetProperty("then", out var then))
        {
            var list = then.ValueKind == JsonValueKind.Array ? then.EnumerateArray().ToList()
                : then.ValueKind == JsonValueKind.Object ? [then]
                : then.ValueKind == JsonValueKind.String ? [JsonSerializer.SerializeToElement(new { action = then.GetString() })] : [];
            foreach (var t in list)
            {
                if (steps.Count >= 6) { error = "do: at most 6 steps (delegate longer jobs)"; return steps; }
                if (!AddStep(t, steps, out error)) return steps;
            }
        }
        return steps;
    }

    /// <summary>Project supported shorthand onto the canonical schema before registry validation; keep routing metadata.</summary>
    private static JsonElement NormalizeInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return input;
        var o = JsonNode.Parse(input.GetRawText())!.AsObject();
        var action = input.Str("action");
        if (!input.TryGetProperty("action", out _)) action = LiftShorthand(input, o) ?? action;
        if (action != null && o["action"] is JsonValue v && v.TryGetValue<string>(out _)) NormalizeActionArgs(o, action);
        if (input.TryGetProperty("then", out var then)) NormalizeThen(o, then);
        return JsonSerializer.SerializeToElement(o);
    }

    /// <summary>{goto: "gen"} → {action: "goto", args: "gen"}; returns the action, or null when no key is an action.</summary>
    private static string? LiftShorthand(JsonElement input, JsonObject o)
    {
        var shorthand = input.EnumerateObject().FirstOrDefault(p => Actions.Contains(p.Name, StringComparer.OrdinalIgnoreCase));
        if (shorthand.Name == null) return null;
        o["action"] = shorthand.Name;
        o["args"] = JsonNode.Parse(shorthand.Value.GetRawText());
        o.Remove(shorthand.Name);
        return shorthand.Name;
    }

    /// <summary>The action in lower case; a string/number arg becomes the tool's main parameter.</summary>
    private static void NormalizeActionArgs(JsonObject o, string action)
    {
        action = action.Trim().ToLowerInvariant();
        o["action"] = action;
        if (o["args"] is not { } args) return;
        var element = JsonSerializer.SerializeToElement(args);
        if (element.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            o["args"] = JsonNode.Parse(Normalize(action, element).GetRawText());
    }

    private static void NormalizeThen(JsonObject o, JsonElement then)
    {
        var steps = then.ValueKind switch
        {
            JsonValueKind.Array => then.EnumerateArray().ToArray(),
            JsonValueKind.Object => [then],
            JsonValueKind.String => [JsonSerializer.SerializeToElement(new { action = then.GetString() })],
            _ => null,
        };
        if (steps != null) o["then"] = new JsonArray(steps.Select(s => JsonNode.Parse(NormalizeInput(s).GetRawText())).ToArray());
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "action", "args", "then", "queue", "body" };

    /// <summary>
    /// One brain, many bodies: splits a <c>do</c> by the <c>body</c> its <c>then[]</c> steps name. The first group is the
    /// call itself with every step that names no body (body = null: the call's own target); each other body gets its steps
    /// as a <c>do</c> of their own (first step as the action, the rest as <c>then</c>), in order of first mention. Without
    /// step bodies: the input as it is.
    /// </summary>
    public static List<(JsonElement? Body, JsonElement Input)> SplitByBody(JsonElement input)
    {
        if (!HasStepBodies(input, out var then)) return [(null, input)];
        var own = new JsonArray();
        var others = GroupByStepBody(then, own);
        var first = JsonNode.Parse(input.GetRawText())!.AsObject();
        if (own.Count > 0) first["then"] = own; else first.Remove("then");
        var list = new List<(JsonElement?, JsonElement)> { (null, JsonSerializer.SerializeToElement(first)) };
        foreach (var (_, body, steps) in others) list.Add((body, BodyDo(input, steps)));
        return list;
    }

    private static bool HasStepBodies(JsonElement input, out JsonElement then)
    {
        then = default;
        return input.ValueKind == JsonValueKind.Object && input.TryGetProperty("then", out then) && then.ValueKind == JsonValueKind.Array
            && then.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.Object && t.TryGetProperty("body", out _));
    }

    /// <summary>The steps naming a body, grouped per body in order of first mention (body removed); the rest go to <paramref name="own"/>.</summary>
    private static List<(string Key, JsonElement Body, List<JsonNode> Steps)> GroupByStepBody(JsonElement then, JsonArray own)
    {
        var others = new List<(string Key, JsonElement Body, List<JsonNode> Steps)>();
        foreach (var t in then.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("body", out var b))
            {
                own.Add(JsonNode.Parse(t.GetRawText()));
                continue;
            }
            var key = b.GetRawText();
            var step = JsonNode.Parse(t.GetRawText())!.AsObject();
            step.Remove("body");
            var g = others.FindIndex(x => x.Key == key);
            if (g < 0) { others.Add((key, b.Clone(), new List<JsonNode>())); g = others.Count - 1; }
            others[g].Steps.Add(step);
        }
        return others;
    }

    /// <summary>A body's own <c>do</c>: its first step as the action, the rest as <c>then</c>, the call's <c>queue</c> kept.</summary>
    private static JsonElement BodyDo(JsonElement input, List<JsonNode> steps)
    {
        var o = steps[0].AsObject();
        if (steps.Count > 1) o["then"] = new JsonArray(steps.Skip(1).Select(s => s.DeepClone()).ToArray());
        if (input.TryGetProperty("queue", out var q)) o["queue"] = JsonNode.Parse(q.GetRawText());
        return JsonSerializer.SerializeToElement(o);
    }

    private static bool AddStep(JsonElement step, List<(string, JsonElement)> steps, out string? error)
    {
        error = null;
        var (action, args) = StepAction(step);
        if (string.IsNullOrWhiteSpace(action)) { error = $"do needs an action ({string.Join("|", Actions)})"; return false; }
        action = action.Trim().ToLowerInvariant();
        if (!Actions.Contains(action)) { error = $"do can't run '{action}' (use {string.Join("|", Actions)}; delegate other jobs)"; return false; }
        var input = Normalize(action, args);
        if (SkillActions.Contains(action)) steps.Add(("skill", JsonSerializer.SerializeToElement(new { name = SkillName(action), args = input })));
        else steps.Add((action, input));
        return true;
    }

    /// <summary>A step's action and args: {action, args}, {action, ...args beside it} or the shorthand {goto: "gen"}.</summary>
    private static (string? Action, JsonElement Args) StepAction(JsonElement step)
    {
        if (step.ValueKind != JsonValueKind.Object) return (null, default);
        if (step.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String)
            return (a.GetString(), step.TryGetProperty("args", out var ar) ? ar : ArgsBesideAction(step));
        // Shorthand {goto: "cobble gen"} / {harvest_generator: {count: 64}}.
        foreach (var p in step.EnumerateObject())
        {
            if (Reserved.Contains(p.Name)) continue;
            if (Actions.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) return (p.Name, p.Value);
        }
        return (null, default);
    }

    /// <summary>{action:"goto", to:"gen"}: the args sit next to action.</summary>
    private static JsonElement ArgsBesideAction(JsonElement step)
    {
        var o = new JsonObject();
        foreach (var p in step.EnumerateObject())
            if (!Reserved.Contains(p.Name)) o[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        return JsonSerializer.SerializeToElement(o);
    }

    /// <summary>The parameter a bare string/number arg fills, per action ("value" for any other).</summary>
    private static readonly Dictionary<string, string> MainArg = new(StringComparer.Ordinal)
    {
        ["goto"] = "to", ["follow"] = "player", ["give_items"] = "player", ["drop_items"] = "item", ["mine"] = "block", ["withdraw"] = "item",
        ["craft"] = "item", ["act"] = "move", ["harvest_generator"] = "at", ["chop_tree"] = "at", ["deposit"] = "at", ["see"] = "view",
        ["light"] = "design", ["dig"] = "site",
    };

    private static JsonElement Normalize(string action, JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object) return args.Clone();
        if (args.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) return JsonSerializer.SerializeToElement(new { });
        var o = new JsonObject { [MainArg.GetValueOrDefault(action, "value")] = JsonNode.Parse(args.GetRawText()) };
        return JsonSerializer.SerializeToElement(o);
    }
}
