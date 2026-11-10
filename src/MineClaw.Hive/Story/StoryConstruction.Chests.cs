using MineClaw.World;

namespace MineClaw.Hive.Story;

/// <summary>
/// A design's chests and the game's own rules for them (docs/web/story-mode.md "Chests in a design"; Fox 11-09: "Even now, there's some
/// blocked blocks on the storehouse build"). Vanilla never places a chest beside a double chest of its kind, nor between two of them
/// (<see cref="ChestPairs.CanPlace(Func{BlockPos, int}, BlockPos, int, out ChestPairs.Clash)"/>), and a chest under a full opaque
/// block doesn't open. A row of chests used to be handed to pawns whole: every third one came back "server undid my placement
/// (protected area?)" three times and stood blocked with a reason that pointed at protection.
/// </summary>
public static partial class StoryConstruction
{
    /// <summary>The start of every chest-rule reason: the reconcile pass, blocked-cell tries and the materials pass know it.</summary>
    public const string ChestRule = "vanilla can't place a chest here";

    /// <summary>A placed chest or trapped chest of a design (the two kinds that pair).</summary>
    public static bool IsChest(GhostState g) => g.Job && !g.Remove && ChestPairs.IsPairable(g.Id);

    /// <summary>A reason that is the chest rule's.</summary>
    public static bool IsChestRule(string? why) => why?.StartsWith(ChestRule, StringComparison.Ordinal) == true;

    /// <summary>A cell blocked by the chest rule: never placed as things stand (no stock reserved, no try spent on it).</summary>
    public static bool ChestRuleHeld(GhostState g) => g.State == "blocked" && IsChestRule(g.Reason);

    /// <summary>The reason a chest cell is blocked with, e.g. "vanilla can't place a chest here: it would touch the double chest at
    /// 1,64,0 (use a trapped chest or leave a gap)". b1.8.1 has no trapped chest.</summary>
    public static string ChestRuleReason(string clash, int id, bool modern) =>
        $"{ChestRule}: {clash} ({(!modern ? "leave a gap" : id == ChestPairs.TrappedChest ? "use a plain chest or leave a gap" : "use a trapped chest or leave a gap")})";

    /// <summary>The pawn client's refusal before any click (<see cref="ChestPairs.Refusal"/>) as the chest rule's reason, or null when
    /// <paramref name="why"/> is not one.</summary>
    public static string? ChestRuleOfRefusal(GhostState g, string why, bool modern)
    {
        int at = why.IndexOf(ChestPairs.RefusalStart, StringComparison.Ordinal);
        if (at < 0) return null;
        int colon = why.IndexOf(": ", at + ChestPairs.RefusalStart.Length, StringComparison.Ordinal);
        return colon < 0 ? null : ChestRuleReason(why[(colon + 2)..], g.Id, modern);
    }

    /// <summary>
    /// The chest cells of <paramref name="ghosts"/> the game would refuse, each with what it would touch. They are walked in a fixed
    /// order (y, z, x) over the world's chests, <paramref name="pending"/> (other builds' chest cells still to come) and the cells
    /// accepted so far, so what is accepted is single chests and lone pairs, which go in in any order. A cell whose chest stands
    /// already is not walked (only its facing is open); an unloaded neighbour is no chest.
    /// </summary>
    public static List<(GhostState Ghost, ChestPairs.Clash Clash)> UnbuildableChests(IEnumerable<GhostState> ghosts, IStoryWorld w,
        IReadOnlyDictionary<P3, int>? pending = null)
    {
        var refused = new List<(GhostState, ChestPairs.Clash)>();
        var accepted = new Dictionary<P3, int>();
        int At(BlockPos q)
        {
            var p = new P3(q.X, q.Y, q.Z);
            if (accepted.TryGetValue(p, out int id) || pending != null && pending.TryGetValue(p, out id)) return id;
            return w.Loaded(q.X, q.Z) ? w.Block(q.X, q.Y, q.Z).Id : -1;
        }
        foreach (var g in ghosts.Where(IsChest).OrderBy(g => g.At.Y).ThenBy(g => g.At.Z).ThenBy(g => g.At.X))
        {
            if (w.Block(g.At.X, g.At.Y, g.At.Z).Id == g.Id) continue;
            if (ChestPairs.CanPlace(At, new BlockPos(g.At.X, g.At.Y, g.At.Z), g.Id, out var clash)) accepted[g.At] = g.Id;
            else refused.Add((g, clash));
        }
        return refused;
    }

    /// <summary>
    /// The chests of a request that won't open: a full opaque block on top (<see cref="StoryColony.ShutsLid"/>), the request's own
    /// block there, else the world's. Its digs leave their cells empty; a chest the chest rule blocked is left out (it is never built).
    /// </summary>
    public static List<P3> ShutChests(IEnumerable<GhostState> ghosts, IStoryWorld w)
    {
        var cells = new Dictionary<P3, GhostState>();
        foreach (var g in ghosts)
            if (!cells.TryGetValue(g.At, out var had) || had.Remove) cells[g.At] = g; // a placement over a dig of the same cell wins
        var shut = new List<P3>();
        foreach (var g in cells.Values)
        {
            if (!IsChest(g) || ChestRuleHeld(g)) continue;
            var above = g.At.Add(0, 1, 0);
            int id = cells.TryGetValue(above, out var top) ? top.Remove ? 0 : top.Id : w.Block(above.X, above.Y, above.Z).Id;
            if (StoryColony.ShutsLid(w.Data, id)) shut.Add(g.At);
        }
        shut.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));
        return shut;
    }
}
