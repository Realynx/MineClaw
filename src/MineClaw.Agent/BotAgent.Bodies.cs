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
        if (dir == null || tier != ModelTier.Main)
        {
            // A body this brain doesn't drive (another hive's, a clone process): never run the call on our own body instead.
            // Live 10-29: main's goto{body:"420"} walked main, and main then told Fox "420 is right next to u now".
            if (tier == ModelTier.Main && BodySpec(u.Input) is { } lone && NotMine(lone) is { } who)
                return [new Planned { Use = u, Result = ToolResult.Error(ForeignBodyError(who)) }];
            return [Plan(u, tier, retry, stopping, sigs)];
        }
        var spec = BodySpec(u.Input);
        var input = WithoutBody(u.Input);
        if (spec == null && !RoutesToBody(u.Name)) return [Plan(u with { Input = input }, tier, retry, stopping, sigs)];

        var plans = new List<Planned>();
        // do: steps in then[] with their own body run on that body's slot (a chain per body).
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
            foreach (var b in targets)
            {
                if (u.Name == "stop" && !IsMainBody(b))
                {
                    string? what = null;
                    try { what = dir.Stop(b); } catch (Exception ex) { _log.LogDebug(ex, "bodies: stop {Body} failed", b); }
                    plans.Add(new Planned { Use = u, Body = b, Result = ToolResult.Success(what != null ? "stopped " + what : "nothing running") });
                    continue;
                }
                plans.Add(Plan(u with { Input = ForBody(u.Name, part, b) }, tier, retry, stopping, sigs, b));
            }
        }
        if (plans.Count > 1 && groups.Count == 1) _log.LogInformation("bodies: {Tool} on {Bodies}", u.Name, string.Join(", ", plans.Select(p => p.Body)));
        return plans;
    }

    /// <summary>The error for a call naming a body this brain doesn't drive: how to reach it, and to report only confirmed results.</summary>
    internal static string ForeignBodyError(string who) =>
        $"not done: {who} isn't a body I drive (nothing moved). Give it the order with hive op=handover name={who} task=\"...\"; " +
        $"tell the player only that you told {who}, then what its result confirms";

    /// <summary>The first name in a <c>body</c> value that isn't this bot (main, me, all/any, its login or number), else null.</summary>
    private string? NotMine(JsonElement spec)
    {
        var names = spec.ValueKind == JsonValueKind.Array
            ? spec.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())
            : [spec.ValueKind == JsonValueKind.String ? spec.GetString() : spec.GetRawText()];
        var me = _chat.Username ?? "";
        var myNumber = System.Text.RegularExpressions.Regex.Match(me, @"(\d+)$") is { Success: true } m ? m.Groups[1].Value : null;
        foreach (var raw in names)
        {
            var n = raw?.Trim().Trim('"') ?? "";
            if (n.Length == 0 || n is "main" or "me" or "self" or "all" or "any" or "focus") continue;
            if (n.Equals(me, StringComparison.OrdinalIgnoreCase) || n == myNumber
                || NameWords().Any(w => !string.IsNullOrWhiteSpace(w) && n.Equals(w, StringComparison.OrdinalIgnoreCase))) continue;
            return n;
        }
        return null;
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
        var names = s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()).ToList()
            : [s.ValueKind == JsonValueKind.String ? s.GetString() : s.GetRawText()];
        var result = new List<string>();
        foreach (var raw in names)
        {
            var n = raw?.Trim().Trim('"') ?? "";
            if (n.Length == 0) continue;
            if (n.Equals("all", StringComparison.OrdinalIgnoreCase)) { result.AddRange(online); continue; }
            if (n.Equals("any", StringComparison.OrdinalIgnoreCase))
            {
                var free = online.Where(b => !IsMainBody(b) && dir.Activities(b) is { IsBusy: false } && dir.Holder(b) == null).ToList();
                var pick = dir.Pick(tool + " " + input.GetRawText(), free.Count > 0 ? free : online.Where(b => !IsMainBody(b)).ToList(), out var why);
                if (pick == null) { error = "no body fits: " + (why ?? "none online (bodies op=spawn)"); return []; }
                result.Add(pick);
                continue;
            }
            var c = dir.Canonical(n);
            if (c == null) { error = $"no body called {n} (bodies: {string.Join(", ", dir.Known())})"; return []; }
            if (!online.Contains(c, StringComparer.OrdinalIgnoreCase)) { error = $"{c} isn't in game (bodies op=spawn name={c} first)"; return []; }
            result.Add(c);
        }
        if (result.Count == 0) { error = "body: name a body (\"67\"), a list, \"all\" or \"any\""; return []; }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
