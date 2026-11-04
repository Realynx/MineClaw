using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// Reaching high blocks (docs/web/story-mode.md "Reaching high blocks"). Fox 11-04: "With the blocks-out-of-reach prompt for mining,
/// cutting or harvesting, it should nerd-pole up to reach those blocks on its own and destroy the nerd pole on the way back down. Not
/// many blocks should truly be out of reach." The pawn does the climbing (<c>story.dig</c>, the client's tree pillar); the colony makes
/// sure it has blocks to climb with and tells "truly out of reach" from "not from here":
/// <list type="bullet">
/// <item>A pawn keeps a small pillar stack (<see cref="ColonyMeta.PillarStack"/>, dirt first, then cobblestone, netherrack) out of
/// its store trips.</item>
/// <item>A job up high (a target more than 3 above the pawn, a tall tree) carries <c>pillar</c>: where to take a stack from storage
/// when it is short, taken only if it really needs to climb.</item>
/// <item>A target out of reach even from a pillar rests for that pawn; once <see cref="UnreachPawns"/> pawns couldn't reach it, the
/// designation waits with the reason instead of failing.</item>
/// </list>
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>What a pawn may pillar with, cheapest first: dirt, cobblestone, netherrack (planks, stone and sandstone it uses
    /// when it has them, but never keeps back from storage).</summary>
    internal static readonly int[] PillarKeep = [3, 4, 87];
    private static readonly int[] PillarAny = [3, 4, 87, 5, 1, 24];

    /// <summary>The pillar kind a pawn keeps out of its store trip (the first it carries no more than the stack of), or -1.</summary>
    internal int PillarKeepKind(PawnLive live)
    {
        if (Meta.PillarStack <= 0) return -1;
        foreach (int id in PillarKeep)
        {
            int n = live.Inventory.Where(s => s.Id == id).Sum(s => s.Count);
            if (n > 0 && n <= Meta.PillarStack) return id;
        }
        return -1;
    }

    /// <summary>A job up high gets <c>pillar</c> (a stack from storage) when the pawn carries too few blocks to climb with.</summary>
    private ActiveJob PillarHint(ActiveJob job, PawnLive live)
    {
        if (job.Kind is not ("dig" or "harvest" or "chop") || job.At is not { } at || !live.Online || Meta.PillarStack <= 0) return job;
        int top = at.Y;
        if (job.Kind == "chop" && job.Json["logs"] is JsonArray logs && logs.Count > 0 && P3.Parse(JsonNode.Parse("[" + logs[^1]!.GetValue<string>() + "]")) is { } hi)
            top = Math.Max(top, hi.Y);
        if (top <= live.Y + 3) return job;
        int have = live.Inventory.Where(s => PillarAny.Contains(s.Id)).Sum(s => s.Count);
        int want = Math.Max(8, Math.Min(Meta.PillarStack, top - (int)live.Y + 2));
        if (have >= want) return job;
        if (FindStored(s => PillarKeep.Contains(s.Id), live.Block, 64) is not { } st) return job;
        job.Json["pillar"] = new JsonObject
        {
            ["from"] = st.Store.Chest.ToString(), ["item"] = ItemName(st.Stack.Id, st.Stack.Meta), ["n"] = Math.Min(st.Stack.Count, Math.Max(want, Meta.PillarStack) - have),
        };
        return job;
    }

    /// <summary>Out of reach even from a pillar ("out of reach even from a pillar: …", "out of reach: 9 block(s) to pillar up…").</summary>
    internal static bool OutOfReach(string why) => why.Contains("out of reach", StringComparison.OrdinalIgnoreCase);

    /// <summary>A designation no pawn could reach even from a pillar waits with the reason (no failure, no alert).</summary>
    private bool ReachWait(ActiveJob j, string why)
    {
        if (j.Source != "designation" || Designations.FirstOrDefault(d => d.Id == j.SourceId) is not { } des) return false;
        NoteWait(des, Short(why));
        return true;
    }
}
