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
    ];

    /// <summary>Actions that are skills of the catalog (run through the worker's <c>skill</c> tool).</summary>
    public static readonly IReadOnlySet<string> SkillActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "move_items" };

    public const string Description =
        "Run a game macro yourself (delegate multi-step jobs). then: next steps, skipped if one fails. goto returns at once, " +
        "walking in the background: plan meanwhile; jobs called now run on arrival. queue=true: after the busy script. " +
        "goto{to,dist?,build?,allow_parkour?(only if poofimafox/kiwi asked)} follow{player} mine{block,count?} " +
        "harvest_generator{at,count,store?} chop_tree{at?} deposit{at?,items?} withdraw{item,count?,at?} craft{item,count?} " +
        "move_items{moves} act{move:jump|spin|wave|crouch|look|punch,target?} see{view?}";

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

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "action", "args", "then", "queue" };

    private static bool AddStep(JsonElement step, List<(string, JsonElement)> steps, out string? error)
    {
        error = null;
        string? action = null;
        JsonElement args = default;
        if (step.ValueKind == JsonValueKind.Object && step.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String)
        {
            action = a.GetString();
            if (step.TryGetProperty("args", out var ar)) args = ar;
            else
            {
                // {action:"goto", to:"gen"}: the args sit next to action.
                var o = new JsonObject();
                foreach (var p in step.EnumerateObject())
                    if (!Reserved.Contains(p.Name)) o[p.Name] = JsonNode.Parse(p.Value.GetRawText());
                args = JsonSerializer.SerializeToElement(o);
            }
        }
        else if (step.ValueKind == JsonValueKind.Object)
        {
            // Shorthand {goto: "cobble gen"} / {harvest_generator: {count: 64}}.
            foreach (var p in step.EnumerateObject())
            {
                if (Reserved.Contains(p.Name)) continue;
                if (!Actions.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) continue;
                action = p.Name;
                args = p.Value;
                break;
            }
        }
        if (string.IsNullOrWhiteSpace(action)) { error = $"do needs an action ({string.Join("|", Actions)})"; return false; }
        action = action.Trim().ToLowerInvariant();
        if (!Actions.Contains(action)) { error = $"do can't run '{action}' (use {string.Join("|", Actions)}; delegate other jobs)"; return false; }
        var input = Normalize(action, args);
        if (SkillActions.Contains(action)) steps.Add(("skill", JsonSerializer.SerializeToElement(new { name = action, args = input })));
        else steps.Add((action, input));
        return true;
    }

    private static JsonElement Normalize(string action, JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object) return args.Clone();
        if (args.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            var key = action switch
            {
                "goto" => "to", "follow" => "player", "mine" => "block", "withdraw" or "craft" => "item", "act" => "move",
                "harvest_generator" or "chop_tree" or "deposit" => "at", "see" => "view", _ => "value",
            };
            var o = new JsonObject { [key] = JsonNode.Parse(args.GetRawText()) };
            return JsonSerializer.SerializeToElement(o);
        }
        return JsonSerializer.SerializeToElement(new { });
    }
}
