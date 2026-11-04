using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using Microsoft.Extensions.Logging;

namespace MineClaw.Agent;

/// <summary>
/// One brain, many bodies, step 3 (docs/design/one-brain-many-bodies.md section 2): the brain's tool calls take a
/// <c>body</c> ("67", ["67","69"], "all", "any"; default: the turn's focus body). The parameter is documented in the cached
/// prompt, never in the tool schemas (the brain and worker tool budgets stay as they are), and stripped before the tool
/// runs. A list or "all" plans one call per body: instant tools run with that body in scope, jobs run on that body's own
/// activity slot (all bodies at once), and the result reads "67: ok ...; 69: failed ...". <c>do</c> steps in <c>then[]</c>
/// may name their own body. Without an active directory (hive mode, one body) nothing here runs.
/// </summary>
public sealed partial class BotAgent
{
    public const string BodyParam = "body";

    /// <summary>The script tool's hidden field for a crew's bodies (Bot's <c>ScriptBridge.CrewBodiesKey</c>).</summary>
    public const string CrewBodiesParam = "__bodies";

    /// <summary>
    /// Set by the script layer: is this <c>script</c> call a run of code that works through roles (everyBody/parallel/spawn)? Then its
    /// <c>body</c> list, "all" or one body is ONE crew over exactly those bodies (Fox 10-30: "assign scripts to all or some or just one
    /// of its bodies"), not one copy of the coordinator per body. Null = every body list splits as before.
    /// </summary>
    public Func<JsonElement, bool>? CrewRun { get; set; }

    /// <summary>Tools that act on a body (and so default to the turn's focus body): jobs, macros, body lookups, scripts.</summary>
    internal static bool RoutesToBody(string name) =>
        IsBodyTool(name) || ReadOnlyTools.Contains(name)
        || name is DoTool.ToolName or "script" or Worker.WorkerAgent.AmendToolName or "stop";

    /// <summary>The call's <c>body</c> value, or null.</summary>
    internal static JsonElement? BodySpec(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(BodyParam, out var b)
        && b.ValueKind is JsonValueKind.String or JsonValueKind.Array or JsonValueKind.Number ? b : null;

    /// <summary>The input without its <c>body</c> (tools never see it).</summary>
    internal static JsonElement WithoutBody(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(BodyParam, out _)) return input;
        var o = new JsonObject();
        foreach (var p in input.EnumerateObject())
            if (!p.Name.Equals(BodyParam, StringComparison.Ordinal)) o[p.Name] = JsonNode.Parse(p.Value.GetRawText());
        return JsonSerializer.SerializeToElement(o);
    }

    /// <summary>Plans a brain call for every body it targets (one plan, unrouted, when there is no active directory).</summary>
    private List<Planned> PlanAll(LlmToolUse u, ModelTier tier, bool retry, bool stopping, ICollection<string> sigs)
    {
        var dir = ActiveBodies;
        if (dir == null || tier != ModelTier.Main) return [UnroutedPlan(u, tier, retry, stopping, sigs)];
        var spec = BodySpec(u.Input);
        var input = WithoutBody(u.Input);
        if (spec == null && !RoutesToBody(u.Name)) return [Plan(u with { Input = input }, tier, retry, stopping, sigs)];

        if (spec is { } crewSpec && u.Name == "script" && IsCrew(input)) return [CrewPlan(u, dir, crewSpec, input, tier, retry, stopping, sigs)];
        return RoutedPlans(u, dir, spec, input, tier, retry, stopping, sigs);
    }

    /// <summary>
    /// A body this brain doesn't drive (another hive's, a clone process): never run the call on our own body instead.
    /// Live 10-29: main's goto{body:"420"} walked main, and main then told Fox "420 is right next to u now".
    /// </summary>
    private Planned UnroutedPlan(LlmToolUse u, ModelTier tier, bool retry, bool stopping, ICollection<string> sigs)
    {
        if (tier == ModelTier.Main && BodySpec(u.Input) is { } lone && NotMine(lone) is { } who)
            return new Planned { Use = u, Result = ToolResult.Error(ForeignBodyError(who)) };
        return Plan(u, tier, retry, stopping, sigs);
    }

    /// <summary>One plan per targeted body; do: steps in then[] with their own body run on that body's slot (a chain per body).</summary>
    private List<Planned> RoutedPlans(LlmToolUse u, global::MineClaw.Agent.Bodies.IBodyDirectory dir, JsonElement? spec, JsonElement input, ModelTier tier,
        bool retry, bool stopping, ICollection<string> sigs)
    {
        var plans = new List<Planned>();
        var groups = u.Name.Equals(DoTool.ToolName, StringComparison.OrdinalIgnoreCase)
            ? DoTool.SplitByBody(input) : [(Body: (JsonElement?)null, Input: input)];
        foreach (var (stepBody, part) in groups)
        {
            var targets = Targets(dir, stepBody ?? spec, u.Name, part, out var error);
            if (error != null)
            {
                plans.Add(new Planned { Use = u, Result = ToolResult.Error(error) });
                continue;
            }
            foreach (var b in targets) plans.Add(PlanForBody(u, dir, b, part, tier, retry, stopping, sigs));
        }
        if (plans.Count > 1 && groups.Count == 1) _log.LogInformation("bodies: {Tool} on {Bodies}", u.Name, string.Join(", ", plans.Select(p => p.Body)));
        return plans;
    }

    private Planned PlanForBody(LlmToolUse u, global::MineClaw.Agent.Bodies.IBodyDirectory dir, string b, JsonElement part, ModelTier tier, bool retry,
        bool stopping, ICollection<string> sigs)
    {
        if (u.Name == "stop" && !IsMainBody(b)) return StopBody(u, dir, b);
        return Plan(u with { Input = ForBody(u.Name, part, b) }, tier, retry, stopping, sigs, b);
    }

    private Planned StopBody(LlmToolUse u, global::MineClaw.Agent.Bodies.IBodyDirectory dir, string b)
    {
        string? what = null;
        try { what = dir.Stop(b); } catch (Exception ex) { _log.LogDebug(ex, "bodies: stop {Body} failed", b); }
        return new Planned { Use = u, Body = b, Result = ToolResult.Success(what != null ? "stopped " + what : "nothing running") };
    }

    private bool IsCrew(JsonElement input)
    {
        try { return CrewRun?.Invoke(input) == true; }
        catch (Exception ex) { _log.LogDebug(ex, "bodies: crew check failed"); return false; }
    }

    /// <summary>
    /// A crew script with a <c>body</c>: one call on the brain's own scope carrying the crew's bodies ("all" stays "all", so bodies that
    /// log in later join; names resolve and must be online; main never runs a lane).
    /// </summary>
    private Planned CrewPlan(LlmToolUse u, global::MineClaw.Agent.Bodies.IBodyDirectory dir, JsonElement spec, JsonElement input, ModelTier tier, bool retry,
        bool stopping, ICollection<string> sigs)
    {
        var names = SpecNames(spec);
        JsonNode bodies;
        if (names.Any(n => string.Equals(n?.Trim(), "all", StringComparison.OrdinalIgnoreCase))) bodies = JsonValue.Create("all")!;
        else
        {
            var targets = Targets(dir, spec, u.Name, input, out var error);
            if (error != null) return new Planned { Use = u, Result = ToolResult.Error(error) };
            var lanes = targets.Where(b => !IsMainBody(b)).ToList();
            if (lanes.Count == 0) return new Planned { Use = u, Result = ToolResult.Error("a crew runs on your other bodies (yours coordinates): name them or \"all\"") };
            bodies = new JsonArray(lanes.Select(b => (JsonNode?)JsonValue.Create(b)).ToArray());
        }
        var o = JsonNode.Parse(input.GetRawText())!.AsObject();
        o[CrewBodiesParam] = bodies;
        _log.LogInformation("bodies: script crew on {Bodies}", bodies.ToJsonString());
        return Plan(u with { Input = JsonSerializer.SerializeToElement(o) }, tier, retry, stopping, sigs);
    }

    /// <summary>The error for a call naming a body this brain doesn't drive: how to reach it, and to report only confirmed results.</summary>
    internal static string ForeignBodyError(string who) =>
        $"not done: {who} isn't a body I drive (nothing moved). Give it the order with hive op=handover name={who} task=\"...\"; " +
        $"tell the player only that you told {who}, then what its result confirms";

    /// <summary>The names in a <c>body</c> value: a list's items or the one value (strings as text, anything else as raw JSON).</summary>
    private static List<string?> SpecNames(JsonElement spec) => spec.ValueKind == JsonValueKind.Array
        ? spec.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()).ToList()
        : [spec.ValueKind == JsonValueKind.String ? spec.GetString() : spec.GetRawText()];

    /// <summary>The first name in a <c>body</c> value that isn't this bot (main, me, all/any, its login or number), else null.</summary>
    private string? NotMine(JsonElement spec)
    {
        var me = _chat.Username ?? "";
        var myNumber = System.Text.RegularExpressions.Regex.Match(me, @"(\d+)$") is { Success: true } m ? m.Groups[1].Value : null;
        foreach (var raw in SpecNames(spec))
        {
            var n = raw?.Trim().Trim('"') ?? "";
            if (IsSelfName(n, me, myNumber)) continue;
            return n;
        }
        return null;
    }

    private bool IsSelfName(string n, string me, string? myNumber)
    {
        if (n.Length == 0 || n is "main" or "me" or "self" or "all" or "any" or "focus") return true;
        return n.Equals(me, StringComparison.OrdinalIgnoreCase) || n == myNumber
            || NameWords().Any(w => !string.IsNullOrWhiteSpace(w) && n.Equals(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>see</c> on another body looks through its eyes (its <c>focus</c>), unless the call names a focus itself.</summary>
    private static JsonElement ForBody(string tool, JsonElement input, string body)
    {
        if (IsMainBody(body) || tool != "see" || input.ValueKind != JsonValueKind.Object || input.TryGetProperty("focus", out _)) return input;
        var o = JsonNode.Parse(input.GetRawText())!.AsObject();
        o["focus"] = body;
        return JsonSerializer.SerializeToElement(o);
    }

    /// <summary>The bodies a <c>body</c> value names (null = the turn's focus), each online; error = a message for the model.</summary>
    private List<string> Targets(global::MineClaw.Agent.Bodies.IBodyDirectory dir, JsonElement? spec, string tool, JsonElement input, out string? error)
    {
        error = null;
        var online = dir.Online();
        if (spec is not { } s) return [TurnFocus is { } f && online.Contains(f, StringComparer.OrdinalIgnoreCase) ? f : "main"];
        var result = new List<string>();
        foreach (var raw in SpecNames(s))
        {
            var n = raw?.Trim().Trim('"') ?? "";
            if (n.Length == 0) continue;
            if (!AddTarget(dir, online, n, tool, input, result, out error)) return [];
        }
        if (result.Count == 0) { error = "body: name a body (\"67\"), a list, \"all\" or \"any\""; return []; }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Adds the bodies one name stands for ("all", "any", a body's name); false with the error when it can't.</summary>
    private static bool AddTarget(global::MineClaw.Agent.Bodies.IBodyDirectory dir, IReadOnlyList<string> online, string n, string tool, JsonElement input,
        List<string> result, out string? error)
    {
        error = null;
        if (n.Equals("all", StringComparison.OrdinalIgnoreCase)) { result.AddRange(online); return true; }
        if (n.Equals("any", StringComparison.OrdinalIgnoreCase)) return AddAnyBody(dir, online, tool, input, result, out error);
        var c = dir.Canonical(n);
        if (c == null) { error = $"no body called {n} (bodies: {string.Join(", ", dir.Known())})"; return false; }
        if (!online.Contains(c, StringComparer.OrdinalIgnoreCase)) { error = $"{c} isn't in game (bodies op=spawn name={c} first)"; return false; }
        result.Add(c);
        return true;
    }

    /// <summary>"any": the fittest free body (else any online non-main body) for the job.</summary>
    private static bool AddAnyBody(global::MineClaw.Agent.Bodies.IBodyDirectory dir, IReadOnlyList<string> online, string tool, JsonElement input,
        List<string> result, out string? error)
    {
        error = null;
        var free = online.Where(b => !IsMainBody(b) && dir.Activities(b) is { IsBusy: false } && dir.Holder(b) == null).ToList();
        var pick = dir.Pick(tool + " " + input.GetRawText(), free.Count > 0 ? free : online.Where(b => !IsMainBody(b)).ToList(), out var why);
        if (pick == null) { error = "no body fits: " + (why ?? "none online (bodies op=spawn)"); return false; }
        result.Add(pick);
        return true;
    }
}
