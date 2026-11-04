using System.Text.Json;
using System.Text.Json.Nodes;
using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Supervisor;

namespace MineClaw.Hive;

/// <summary>
/// Hive tools. Main brain: ONE merged tool, <c>hive</c> (op spawn | release | list | handover | chorus; kind, brain), instead of spawn_clone/recall_clone/
/// list_clones — a third of the cached-prefix cost, keeping the brain's tool budget under 2,300 tokens. Clone brain:
/// <c>hive_report</c> (progress/done/failed back to main; done/failed starts the deposit-then-logout).
/// </summary>
public static class HiveTools
{
    public const string MainTool = "hive";
    public const string CloneTool = "hive_report";

    /// <summary>Main's cached prompt line for script spawns (added once, next to the hive hint).</summary>
    public const string ScriptHint =
        "hive spawn script=<saved name> (or code=<JS>, args) starts a clone straight on it, brain asleep (no calls) until it " +
        "ends/fails/asks; after=logout (default) or sleep.";

    /// <summary>Main's cached prompt line for the chorus (the tool text is budget-pinned, so its usage lives here).</summary>
    public const string ChorusHint =
        "Reassign bodies with hive, never via chat. hive op=chorus task=<line> name=all|names: every body says that plain-chat line at the same moment, even mid-job (salutes: o7).";

    /// <summary>
    /// Main's cached prompt line for the clone kinds (added once, next to the script hint). <c>script=X bodies=N</c> is a hive script:
    /// ONE saved script owns N bodies with no brain (live 10-29: a brain dispatching per-body jobs left bodies idle and doubled up).
    /// A brain hive given <c>script=X</c> too starts its bodies on X as they log in (Fox 10-30: "so its bodies just go straight to
    /// work"); "release name|all" left the line to pay for it (the op enum and name carry it), the cap stays 220 chars.
    /// </summary>
    public const string KindHint =
        "hive spawn bodies=N: N bodies YOUR brain drives (\"2 agents\"). kind=hive brain=deepseek bodies=15: ONE DeepSeek " +
        "hive driving 15 bodies (+script=X: working X). script=X bodies=N: X alone drives N. Report only what spawned.";

    /// <param name="llm">Main's model options: the script-authorship policy for inline <c>code</c> (only the Opus brain writes Lua).
    /// Null = no inline code.</param>
    /// <param name="hierarchy">Kinds, ownership and dynamic accounts; null = the supervisor's slots only (old behaviour).</param>
    public static void RegisterMain(ToolRegistry tools, HiveSupervisor hive, LlmOptions? llm = null, Hierarchy.HiveHierarchy? hierarchy = null)
    {
        var schema = ToolSchema.Create().Str("op", enumValues: ["spawn", "release", "list", "handover", "chorus"]).Str("task", required: false).Str("name", required: false);
        // Script spawn: the clone starts it at once with its brain asleep (zero calls until a wake). Tiny: no descriptions.
        // How to use them is one cached prompt line (HiveTools.ScriptHint/KindHint), not tool text.
        if (hive.ScriptPrep != null)
            schema = schema.Str("script", required: false).Str("code", required: false).Raw("args", """{"type":"object"}""", required: false)
                .Str("after", required: false);
        // kind always; brain (which replaces the old profile field: a profile name still works as brain=<profile>, and "profile"
        // is still read) only when there is a brain to choose, as profile was. Paid for by the shorter description below.
        schema = schema.Str("kind", required: false).Int("bodies", required: false);
        if (llm != null && llm.UsableProfiles().Any()) schema = schema.Str("brain", required: false);
        tools.SetInputAdapter(MainTool, NormalizeInput);
        tools.Register(MainTool,
            // Reassigning goes through spawn/handover: a clone ignores hive bots' chat (live 3, 14:34: "69, robin wants you on the
            // cobble gen" never reached 69). Budget pinned in ToolBudgets.HiveTool.
            "Spawn or reassign.",
            schema.Build(),
            (input, _) => MainOpAsync(hive, hierarchy, input), tiers: ToolTiers.Main);
        // Inline Lua for a clone is script authorship: only main's Opus brain (same rule as the script tool), on every path.
        tools.AddPolicy(MainTool, (input, caller) =>
            !string.IsNullOrWhiteSpace(input.Str("code")) && (llm == null || caller?.IsBrainModel(llm) != true) ? Scripting.ScriptPrompts.BrainOnly : null);
    }

    /// <summary>One <c>hive</c> call of main's brain.</summary>
    private static async Task<ToolResult> MainOpAsync(HiveSupervisor hive, Hierarchy.HiveHierarchy? hierarchy, JsonElement input)
    {
        var op = input.ReqStr("op").Trim().ToLowerInvariant();
        if (op == "chorus")
        {
            // The line rides in "task" (no extra fields: the tool's text is budget-pinned); main always joins in.
            var result = await hive.ChorusAsync(input.Str("task"), input.Str("name")).ConfigureAwait(false);
            return result.StartsWith("error") ? ToolResult.Error(result) : ToolResult.Success(result);
        }
        string? args = ArgsOf(input);
        var brain = input.Str("brain") ?? input.Str("profile");
        string r = hierarchy != null ? HierarchyOp(hierarchy, op, input, brain, args) : SupervisorOp(hive, op, input, brain, args);
        return r.StartsWith("error") ? ToolResult.Error(r) : ToolResult.Success(r);
    }

    /// <summary>The raw <c>args</c> object of a script spawn, when given.</summary>
    internal static string? ArgsOf(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a.GetRawText() : null;

    private static string HierarchyOp(Hierarchy.HiveHierarchy hierarchy, string op, JsonElement input, string? brain, string? args)
    {
        var req = new Hierarchy.SpawnRequest(input.Str("kind"), brain, input.Str("task"), input.Str("script"), input.Str("code"), args,
            input.Str("after"), input.Str("name"), Handover: op == "handover", Bodies: input.Int("bodies"));
        return op switch
        {
            "spawn" or "handover" => hierarchy.Spawn(req),
            "release" or "recall" => hierarchy.Release(input.Str("name") ?? "all"),
            "list" => hierarchy.List(),
            _ => "error: op is spawn|release|list|handover",
        };
    }

    /// <summary>No hierarchy: brain=opus|deepseek|&lt;profile&gt; maps onto a model profile for the clone's brain.</summary>
    private static string SupervisorOp(HiveSupervisor hive, string op, JsonElement input, string? brain, string? args)
    {
        var (bk, profile) = Hierarchy.HiveKinds.ParseBrain(brain);
        if (bk == Hierarchy.BrainKind.DeepSeek) profile = "deepseek";
        var opts = new HiveSpawnOptions(profile, input.Str("script"), input.Str("code"), args, input.Str("after")) { Kind = input.Str("kind") };
        return op switch
        {
            "spawn" => hive.Spawn(input.Str("task") ?? "", input.Str("name"), options: opts),
            // Explicitly move a job another clone owns to this one (the old owner is told).
            "handover" => hive.Spawn(input.Str("task") ?? "", input.Str("name"), handover: true, options: opts),
            "release" or "recall" => hive.Recall(input.Str("name") ?? "all"),
            "list" => hive.List(),
            _ => "error: op is spawn|release|list|handover",
        };
    }

    // recall is the legacy spelling of release; profile/brain and body metadata remain extra allowed properties.
    internal static JsonElement NormalizeInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("op", out var op) || op.ValueKind != JsonValueKind.String) return input;
        var value = op.GetString()!.Trim().ToLowerInvariant();
        var o = JsonNode.Parse(input.GetRawText())!.AsObject();
        o["op"] = value == "recall" ? "release" : value;
        return JsonSerializer.SerializeToElement(o);
    }

    public static void RegisterClone(ToolRegistry tools, CloneHost host) =>
        tools.Register(CloneTool,
            "Report your hive task to the main bot. done/failed ends it: you then deposit everything and log out.",
            ToolSchema.Create().Str("status", enumValues: ["progress", "done", "failed"]).Str("text", "short")
                .Num("pct", "0-1 progress", required: false).Build(),
            async (input, _) => ToolResult.Success(await host.ReportAsync(input.ReqStr("status"), input.Str("text") ?? "", input.Num("pct")).ConfigureAwait(false)),
            tiers: ToolTiers.Main | ToolTiers.Worker);
}
