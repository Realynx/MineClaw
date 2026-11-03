using MineClaw.Agent.Tools;
using MineClaw.Hive.Clone;
using MineClaw.Hive.Hierarchy.Bus;

namespace MineClaw.Hive.Hierarchy;

/// <summary>
/// A hive-kind clone's own <c>hive</c> tool: it can spawn, list and release ITS clones (subhive/script bodies or further hives),
/// every op a bus request to main, which allocates the account, whitelists it and records the ownership. Never chat.
/// Same name as main's tool so prompts stay one vocabulary; smaller (no handover/code: inline code is main's Opus only).
/// <c>spawn bodies=N script=X</c> (no brain) brings N bodies in straight onto saved crew script X (Fox 10-30: "spawn up its bodies
/// with a script already made, so its bodies just go straight to work"): ONE crew over just them, started here as each logs in.
/// </summary>
public static class CloneHiveTool
{
    /// <summary>
    /// The tool text; the last clause points one job for several bodies at a saved crew script (live 10-29: per-body jobs, turn after
    /// turn), for new bodies (spawn bodies=N script=X) or the ones it has (script{name, body}). Paid for within main's cap: args is read
    /// (main's hive tool vocabulary) but not in the schema; the saved script's params() defaults apply otherwise.
    /// </summary>
    public const string Description =
        "Bodies/clones via main, never chat: spawn bodies=N (+script=X: they start on X), release name|all, list. Yours now: script{name,body}.";

    public static void Register(ToolRegistry tools, CloneHost host)
    {
        tools.SetInputAdapter(HiveTools.MainTool, HiveTools.NormalizeInput);
        tools.Register(HiveTools.MainTool, Description,
            ToolSchema.Create().Str("op", enumValues: ["spawn", "release", "list"]).Str("task", required: false).Str("name", required: false)
                .Str("script", required: false).Str("kind", required: false).Int("bodies", required: false).Build(),
            async (input, _) =>
            {
                var op = input.ReqStr("op").Trim().ToLowerInvariant();
                string? args = input.ValueKind == System.Text.Json.JsonValueKind.Object && input.TryGetProperty("args", out var a)
                    && a.ValueKind == System.Text.Json.JsonValueKind.Object ? a.GetRawText() : null;
                // Bodies straight onto a saved crew script: this hive hosts them and runs the crew; a brain named = a new hive (main's path).
                if (op == "spawn" && input.Str("script") is { Length: > 0 } script && input.Int("bodies") is > 0 and var n
                    && input.Str("brain") is null && input.Str("kind") is null or "subhive" or "script" && string.IsNullOrWhiteSpace(input.Str("name")))
                {
                    if (host.CrewSpawn is not { } crewSpawn) return ToolResult.Error("no: this hive hosts no bodies of its own; spawned nothing");
                    var t = await crewSpawn(script, args, n, input.Str("task")).ConfigureAwait(false);
                    return t.StartsWith("ok", StringComparison.Ordinal) ? ToolResult.Success(t) : ToolResult.Error(t);
                }
                BusReply? r = op switch
                {
                    "spawn" => await host.BusAsync(BusTypes.Spawn, new SpawnReq(input.Str("kind"), input.Str("brain"), input.Str("task"), input.Str("script"), args,
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
}
