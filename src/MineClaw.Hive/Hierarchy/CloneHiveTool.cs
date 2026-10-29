using MineClaw.Agent.Tools;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Hierarchy.Bus;

namespace MineClaw.Hive.Hierarchy;

/// <summary>
/// A hive-kind clone's own <c>hive</c> tool: it can spawn, list and release ITS clones (subhive/script bodies or further hives),
/// every op a bus request to main, which allocates the account, whitelists it and records the ownership. Never chat.
/// Same name as main's tool so prompts stay one vocabulary; smaller (no handover/args/code: inline code is main's Opus only).
/// </summary>
public static class CloneHiveTool
{
    /// <summary>The tool text; the last clause points one job for every body at a saved hive script (live 10-29: per-body jobs, turn after turn).</summary>
    public const string Description = "Your bodies/clones (via main, never chat): spawn bodies=N, release name|all, list. One job for all bodies: script{name}.";

    public static void Register(ToolRegistry tools, CloneHost host) =>
        tools.Register(HiveTools.MainTool, Description,
            ToolSchema.Create().Str("op", enumValues: ["spawn", "release", "list"]).Str("task", required: false).Str("name", required: false)
                .Str("script", required: false).Str("kind", required: false).Int("bodies", required: false).Build(),
            async (input, _) =>
            {
                var op = input.ReqStr("op").Trim().ToLowerInvariant();
                BusReply? r = op switch
                {
                    "spawn" => await host.BusAsync(BusTypes.Spawn, new SpawnReq(input.Str("kind"), input.Str("brain"), input.Str("task"), input.Str("script"),
                        Name: input.Str("name"), Bodies: input.Int("bodies"))).ConfigureAwait(false),
                    "release" or "recall" => await host.BusAsync(BusTypes.Release, new ReleaseReq(input.Str("name") ?? "all")).ConfigureAwait(false),
                    "list" => await host.BusAsync(BusTypes.List, null).ConfigureAwait(false),
                    _ => BusReply.Fail("op is spawn|release|list"),
                };
                if (r == null) return ToolResult.Error("main didn't answer on the hive bus");
                var text = r.Ok ? r.As<string>() ?? "ok" : r.Error ?? "failed";
                return r.Ok && !text.StartsWith("error", StringComparison.Ordinal) ? ToolResult.Success(text) : ToolResult.Error(text);
            }, tiers: ToolTiers.Main);
}
