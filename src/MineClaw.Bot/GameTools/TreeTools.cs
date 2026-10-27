using MineClaw.Agent.Events;
using MineClaw.Agent.Tools;
using MineClaw.Bot.Game;
using MineClaw.Bot.Memory;
using MineClaw.Bot.WorldIndex;
using MineClaw.Client;
using MineClaw.Client.Trees;

namespace MineClaw.Bot.GameTools;

/// <summary>
/// <c>chop_tree</c>: one macro = fell one whole tree, gather its saplings, replant (<see cref="MinecraftClient.ChopTreeAsync"/>).
/// The "mine/chop a tree, get wood" mapping lives in the description (cached tool prefix), not the system prompt.
/// Results feed the world index (tree spot replanted / sapling growing) and ride along as a background task event so
/// "good job" afterwards credits the wood work.
/// </summary>
public static class TreeTools
{
    public const string ToolName = "chop_tree";

    public static void Register(ToolRegistry tools, IGameClient c, IPlaceResolver places)
    {
        // Hive: the claim wrapper doesn't know this macro; chop with the real client underneath.
        var chopper = (c as ClaimAwareGameClient)?.Inner ?? c;
        tools.Register(ToolName, "Fell ONE whole tree + replant. For mine/chop a tree, get wood (not mine).",
            ToolSchema.Create().Str("at", "x,y,z, name, oak/birch/spruce", required: false).Build(),
            async (i, ct) =>
            {
                var spec = i.Str("at")?.Trim();
                // Work at height (pillar up to the crown): carry clutch blocks first (live 10-27: 67 fell off a crown with none).
                var clutch = await ClutchBlocks.EnsureAsync(c, ct);
                ToolResult With(ToolResult r) => clutch == null ? r : r with { Text = clutch + "; " + r.Text };
                if (string.IsNullOrEmpty(spec) || spec is "nearest" or "tree" or "the tree") return With(GameToolSet.R(await chopper.ChopTreeAsync(null, null, ct)));
                if (TreeScan.ParseWood(spec) is { } wood) return With(GameToolSet.R(await chopper.ChopTreeAsync(null, wood, ct)));
                if (GameToolSet.Resolve(spec, places) is { } p) return With(GameToolSet.R(await chopper.ChopTreeAsync(p, null, ct)));
                return ToolResult.Error($"unknown tree '{spec}' (x,y,z, a saved name, or oak/birch/spruce)");
            }, longRunning: true);
    }

    /// <summary>Feeds every chop report into the index and the event queue (no LLM call: background priority).</summary>
    public static void AttachReports(MinecraftClient client, WorldIndexer? indexer, EventQueue events)
    {
        client.TreeChopped += r =>
        {
            if (r.Replanted is { } p) indexer?.NoteTree(new MineClaw.World.BlockPos(p.X, p.Y, p.Z), $"replanted {r.Wood} sapling, growing");
            else if (r.LogsFelled > 0) indexer?.NoteTree(new MineClaw.World.BlockPos(r.Base.X, r.Base.Y, r.Base.Z), "chopped, not replanted: " + (r.ReplantProblem ?? "logs left"));
            events.Post(ReportEvent(r));
        };
    }

    /// <summary>A background task event ("chop_tree ok: chopped oak tree ...") so praise in the next turn credits it.</summary>
    public static GameEvent ReportEvent(TreeChopReport r)
    {
        // Worded so the personality's task classifier files it under wood work (it reads "sapling" as farming).
        string what = $"{r.Wood} tree, {r.LogsFelled}/{r.LogsTotal} logs";
        return r.Ok
            ? new GameEvent(EventKind.TaskCompleted, $"{ToolName} ok: chopped {what}, replanted", priority: EventPriority.Background)
            : GameEvent.TaskFail($"{ToolName} failed: chopped {what}, " + (r.Replanted is null ? "not replanted" : "replanted"), wakes: false);
    }
}
