using MineClaw.Agent.Llm;
using MineClaw.Agent.Tools;
using MineClaw.Hive.Ipc;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Supervisor;

namespace MineClaw.Hive;

/// <summary>
/// Hive tools. Main brain: ONE merged tool, <c>hive</c> (op spawn | recall | list), instead of spawn_clone/recall_clone/
/// list_clones — a third of the cached-prefix cost, keeping the brain's tool budget under 2,300 tokens. Clone brain:
/// <c>hive_report</c> (progress/done/failed back to main; done/failed starts the deposit-then-logout).
/// </summary>
public static class HiveTools
{
    public const string MainTool = "hive";
    public const string CloneTool = "hive_report";

    /// <summary>Main's cached prompt line for script spawns (added once, next to the hive hint).</summary>
    public const string ScriptHint =
        "hive spawn script=<saved name> (or code=<your Lua>, args) starts a clone straight on it, brain asleep (no calls) until it " +
        "ends/fails/asks; after=logout (default) or sleep.";

    /// <param name="llm">Main's model options: enables <c>profile</c> (only when a profile is usable) and the script-authorship
    /// policy for inline <c>code</c> (only the Opus brain writes Lua). Null = no profiles, no inline code.</param>
    public static void RegisterMain(ToolRegistry tools, HiveSupervisor hive, LlmOptions? llm = null)
    {
        var schema = ToolSchema.Create().Str("op", enumValues: ["spawn", "recall", "list", "handover"]).Str("task", required: false).Str("name", "e.g. 67", required: false);
        // Script spawn: the clone starts it at once with its brain asleep (zero calls until a wake). Tiny: no descriptions.
        // How to use them is one cached prompt line (HiveTools.ScriptHint), not tool text.
        if (hive.ScriptPrep != null)
            schema = schema.Str("script", required: false).Str("code", required: false).Raw("args", """{"type":"object"}""", required: false)
                .Str("after", required: false);
        if (llm != null && llm.UsableProfiles().Any()) schema = schema.Str("profile", required: false);
        tools.Register(MainTool,
            hive.ScriptPrep != null ? "Clones: spawn a task/script, recall name|all, list." : "Clones: spawn a task, recall name|all, list.",
            schema.Build(),
            (input, _) =>
            {
                var op = input.ReqStr("op").Trim().ToLowerInvariant();
                var opts = new HiveSpawnOptions(input.Str("profile"), input.Str("script"), input.Str("code"),
                    input.ValueKind == System.Text.Json.JsonValueKind.Object && input.TryGetProperty("args", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Object ? a.GetRawText() : null,
                    input.Str("after"));
                var r = op switch
                {
                    "spawn" => hive.Spawn(input.Str("task") ?? "", input.Str("name"), options: opts),
                    // Explicitly move a job another clone owns to this one (the old owner is told).
                    "handover" => hive.Spawn(input.Str("task") ?? "", input.Str("name"), handover: true, options: opts),
                    "recall" => hive.Recall(input.Str("name") ?? "all"),
                    "list" => hive.List(),
                    _ => "error: op is spawn|recall|list|handover",
                };
                return Task.FromResult(r.StartsWith("error") ? ToolResult.Error(r) : ToolResult.Success(r));
            }, tiers: ToolTiers.Main);
        // Inline Lua for a clone is script authorship: only main's Opus brain (same rule as the script tool), on every path.
        tools.AddPolicy(MainTool, (input, caller) =>
            !string.IsNullOrWhiteSpace(input.Str("code")) && (llm == null || caller?.IsBrainModel(llm) != true) ? Scripting.ScriptPrompts.BrainOnly : null);
    }

    public static void RegisterClone(ToolRegistry tools, CloneHost host) =>
        tools.Register(CloneTool,
            "Report your hive task to the main bot. done/failed ends it: you then deposit everything and log out.",
            ToolSchema.Create().Str("status", enumValues: ["progress", "done", "failed"]).Str("text", "short")
                .Num("pct", "0-1 progress", required: false).Build(),
            async (input, _) => ToolResult.Success(await host.ReportAsync(input.ReqStr("status"), input.Str("text") ?? "", input.Num("pct")).ConfigureAwait(false)),
            tiers: ToolTiers.Main | ToolTiers.Worker);
}
