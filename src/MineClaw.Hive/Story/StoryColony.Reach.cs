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
/// <item>A dig, or another job up high, carries <c>pillar</c>: where to take a stack from storage when short.
/// Diggers restock before approaching: a target below the rim may still need climbing from the quarry floor.</item>
/// <item>A target out of reach even from a pillar rests for that pawn; once <see cref="UnreachPawns"/> pawns couldn't reach it, the
/// designation waits with the reason instead of failing.</item>
/// </list>
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>What a pawn may pillar with, cheapest first: dirt, cobblestone, netherrack (planks, stone and sandstone it uses
    /// when it has them, but never keeps back from storage).</summary>
    internal static readonly int[] PillarKeep = [3, 4, 87];
    // Planks a pawn carries are a build's material: never counted as blocks to climb with (it fetches dirt or cobble instead).
    private static readonly int[] PillarAny = [3, 4, 87, 1, 24];

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

    /// <summary>A dig, or another job up high, gets a storage hint when short of climbing blocks. A quarry remnant can be below
    /// the pawn at the rim but above its reachable floor, so digs cannot use the pawn's starting height to rule out a climb.</summary>
    private ActiveJob PillarHint(ActiveJob job, PawnLive live)
    {
        if (job.Kind is not ("dig" or "harvest" or "chop") && job.Json["reach"]?.GetValue<bool>() != true
            || job.At is not { } at || !live.Online || Meta.PillarStack <= 0) return job;
        int top = JobTop(job, at);
        if (job.Kind is not ("dig" or "place") && top <= live.Y + 3) return job;
        int have = live.Inventory.Where(s => PillarAny.Contains(s.Id)).Sum(s => s.Count);
        // A cobble/stone/dirt build's permanent material is not spare scaffolding.
        if (job.Kind == "place" && World is { } w && job.Json["block"] is JsonValue material && material.TryGetValue<string>(out var item))
        {
            int id = KeyOfItem(w, item, 0).Id;
            if (PillarAny.Contains(id))
                have -= Math.Min(live.Inventory.Where(s => s.Id == id).Sum(s => s.Count), (job.Json["cells"] as JsonArray)?.Count ?? 1);
        }
        int want = Math.Max(8, Math.Min(Meta.PillarStack, top - (int)live.Y + 2));
        // The reachable base can be far below both target and pawn. Keep a supply hint for the longest supported climb;
        // the SDK only takes these extra blocks when its actual access plan needs them.
        if (job.Kind == "dig") want = Math.Max(want, Scripting.Story.ReachScan.MaxPillar);
        if (have >= want) return job;
        if (FindStored(s => PillarKeep.Contains(s.Id), live.Block, 64) is not { } st) return job;
        job.Json["pillar"] = new JsonObject
        {
            ["from"] = st.Store.Chest.ToString(), ["item"] = ItemName(st.Stack.Id, st.Stack.Meta),
            ["n"] = Math.Min(st.Stack.Count, (job.Kind == "dig" ? want : Math.Max(want, Meta.PillarStack)) - have),
        };
        return job;
    }

    /// <summary>The highest cell a job works at: its target, or a chop's top log.</summary>
    private static int JobTop(ActiveJob job, P3 at)
    {
        if (job.Kind == "chop" && job.Json["logs"] is JsonArray logs && logs.Count > 0 && P3.Parse(JsonNode.Parse("[" + logs[^1]!.GetValue<string>() + "]")) is { } hi)
            return Math.Max(at.Y, hi.Y);
        return at.Y;
    }

    /// <summary>How often a chop designation stays open after a partial chop (its rest still standing) before it is let go with an alert.</summary>
    internal const int MaxChopPartials = 2;

    /// <summary>The pawn that took part of a tree rests from its remnant this long (another pawn, or it later, climbs for the rest).</summary>
    internal static readonly TimeSpan PartialRest = TimeSpan.FromSeconds(20);

    private readonly Dictionary<string, int> _chopPartials = new(StringComparer.Ordinal);

    /// <summary>
    /// A chop designation a pawn finished only in part ("partial: …"): the tree's logs still standing in the colony's view, lowest first,
    /// when the designation should stay open for them (at most <see cref="MaxChopPartials"/> times), else null (it is done and said).
    /// </summary>
    private List<P3>? ChopRest(DesignationState des)
    {
        if (des.Kind != "chop" || World is not { } w || des.Blocks is not { Count: > 0 } logs) return null;
        int n = _chopPartials.GetValueOrDefault(des.Id);
        if (n >= MaxChopPartials) { _chopPartials.Remove(des.Id); return null; }
        var rest = logs.Where(l => w.Loaded(l.X, l.Z) && StoryBlocks.Logs.Contains(w.Block(l.X, l.Y, l.Z).Id)).OrderBy(l => l.Y).ToList();
        if (rest.Count == 0) { _chopPartials.Remove(des.Id); return null; }
        _chopPartials[des.Id] = n + 1;
        return rest;
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
