using MineClaw.Agent.Tools;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Supervisor;

namespace MineClaw.Hive;

/// <summary>
/// Hive tools. Main brain: ONE merged tool, <c>hive</c> (op spawn | recall | list), instead of spawn_clone/recall_clone/
/// list_clones — a third of the cached-prefix cost, keeping the brain's tool budget under 2,000 tokens. Clone brain:
/// <c>hive_report</c> (progress/done/failed back to main; done/failed starts the deposit-then-logout).
/// </summary>
public static class HiveTools
{
    public const string MainTool = "hive";
    public const string CloneTool = "hive_report";

    public static void RegisterMain(ToolRegistry tools, HiveSupervisor hive) =>
        tools.Register(MainTool,
            "Your clones: spawn {task}, recall {name|all} (they deposit first), list.",
            ToolSchema.Create().Str("op", enumValues: ["spawn", "recall", "list"]).Str("task", required: false).Str("name", "e.g. 67", required: false).Build(),
            (input, _) =>
            {
                var op = input.ReqStr("op").Trim().ToLowerInvariant();
                var r = op switch
                {
                    "spawn" => hive.Spawn(input.Str("task") ?? "", input.Str("name")),
                    "recall" => hive.Recall(input.Str("name") ?? "all"),
                    "list" => hive.List(),
                    _ => "error: op is spawn|recall|list",
                };
                return Task.FromResult(r.StartsWith("error") ? ToolResult.Error(r) : ToolResult.Success(r));
            }, tiers: ToolTiers.Main);

    public static void RegisterClone(ToolRegistry tools, CloneHost host) =>
        tools.Register(CloneTool,
            "Report your hive task to the main bot. done/failed ends it: you then deposit everything and log out.",
            ToolSchema.Create().Str("status", enumValues: ["progress", "done", "failed"]).Str("text", "short")
                .Num("pct", "0-1 progress", required: false).Build(),
            async (input, _) => ToolResult.Success(await host.ReportAsync(input.ReqStr("status"), input.Str("text") ?? "", input.Num("pct")).ConfigureAwait(false)),
            tiers: ToolTiers.Main | ToolTiers.Worker);
}
