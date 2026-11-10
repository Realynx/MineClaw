using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// A cell is never both "to dig" and "to place" (docs/web/story-mode.md "Quarries win over builds"; live 11-05 story-1, Fox: "The pawns
/// are broken at the quarry. They keep placing and deleting blocks on the top level."). A dirt floor build (b11401, 432 blocks at y 63)
/// covered the top layer of quarry z17860: the quarry dug its cells, the 5-second reconcile saw them missing and reopened the build,
/// builders placed the dirt again, and the quarry's full look from the top found its first layer refilled and called every pawn up out
/// of the pit to dig it again ("Build b11401 … 3 missing: reopened" every few seconds, rim placements handed to a pawn 17 blocks down).
/// <list type="bullet">
/// <item>The quarry wins: a build's cell in a quarry's columns, at or under its top, is blocked as "conflicts with quarry …" (never
/// placed, never reopened), with one alert per build and quarry asking the owner to move or remove one. The quarry's steps are its
/// own (it places them where the stair needs them and never digs them).</item>
/// <item>Painting a quarry (or switching a mine zone to quarry) over a build's cells says the overlap in the answer and in an alert.</item>
/// <item>A pawn down in a quarry pit leaves build cells above it to a builder up top.</item>
/// </list>
/// The conflict ends by itself when the quarry or the build moves: the cell is open again on the next pass.
/// </summary>
public sealed partial class StoryColony
{
    internal const string QuarryConflictWords = "conflicts with quarry ";

    /// <summary>Builds and quarries whose overlap was alerted (<c>build|zone</c>): said once.</summary>
    private readonly HashSet<string> _conflictSaid = new(StringComparer.Ordinal);

    /// <summary>The quarry that digs this cell (its columns, from its top down), or null.</summary>
    internal ZoneState? QuarryAt(P3 at)
    {
        if (at.Y < 1 || !Zix().Quarry.TryGetValue(ZoneCells.Key(at.X, at.Z), out var z)) return null;
        return at.Y <= QuarryTopOf(z) ? z : null;
    }

    /// <summary>The quarry's top: kept once seen, else found now, else the level it was painted at.</summary>
    private int QuarryTopOf(ZoneState z)
    {
        if (z.QuarryTop is { } t) return t;
        var q = Quarry(z);
        return z.QuarryTop ?? (q is { Top: > 0 } ? q.Top : z.Area.Y2); // a column out of view: the look's top (none is kept yet)
    }

    private static string ConflictWords(ZoneState z) => $"{QuarryConflictWords}{ZoneName(z)}: the quarry digs this cell";

    /// <summary>A cell blocked because a quarry digs it.</summary>
    private static bool Conflicted(GhostState g) =>
        BuildConflicted(g) || QuarryConflicted(g);

    private static bool QuarryConflicted(GhostState g) =>
        g.State == "blocked" && g.Reason?.StartsWith(QuarryConflictWords, StringComparison.Ordinal) == true;

    /// <summary>The quarry a placed cell of a build lies in, or null (digs never conflict: both want the cell open).</summary>
    private ZoneState? QuarryConflict(GhostState g) => g.Job && !g.Remove ? QuarryAt(g.At) : null;

    /// <summary>
    /// The reconcile's and the pick's check of one cell: in a quarry → blocked as a conflict (true: nothing more to do with it). A cell
    /// once in conflict whose quarry has gone (erased, moved, switched to ores) is open again.
    /// </summary>
    private bool QuarryHeld(BuildState b, GhostState g)
    {
        if (QuarryConflict(g) is { } z)
        {
            MarkConflict(b, g, z);
            return true;
        }
        if (QuarryConflicted(g)) ForgetCellFailures(b, g);
        return false;
    }

    private void MarkConflict(BuildState b, GhostState g, ZoneState z)
    {
        var why = ConflictWords(z);
        if (g.State == "blocked" && g.Reason == why) return;
        ForgetCellFailures(b, g);
        g.State = "blocked";
        g.Reason = why;
        g.Holds = g.HoldsWords = null;
        g.Rev = Touch();
        SayQuarryConflict(b, z);
    }

    /// <summary>Every cell of the build a quarry digs, blocked; → the cells and the first quarry, or (0, null).</summary>
    private (int Cells, ZoneState? Quarry) MarkConflicts(BuildState b)
    {
        int n = 0;
        ZoneState? first = null;
        foreach (var g in b.Ghosts)
        {
            if (g.State == "leased" || QuarryConflict(g) is not { } z) continue;
            MarkConflict(b, g, z);
            first ??= z;
            n++;
        }
        if (n > 0) ConflictStatus(b);
        return (n, first);
    }

    /// <summary>The build's reason, and one alert per build and quarry (the quarry goes first; the owner moves or removes one).</summary>
    private void SayQuarryConflict(BuildState b, ZoneState z)
    {
        var why = ConflictWords(z);
        int n = b.Ghosts.Count(g => g.Reason == why);
        b.Reason = $"{n} block{(n == 1 ? "" : "s")} lie in quarry {ZoneName(z)}, which digs them out: left unbuilt";
        b.Rev = Touch();
        if (!_conflictSaid.Add($"{b.Id}|{z.Id}")) return;
        Alert("warn", $"Build {b.Id} ({b.Label}): {b.Reason}. Move or remove the build or the quarry");
    }

    /// <summary>A build with nothing left but cells a quarry digs is blocked (not done, not queued).</summary>
    private void ConflictStatus(BuildState b)
    {
        if (b.Status is "paused" or "blocked" || b.Ghosts.Any(g => g.Job && g.State is "todo" or "leased")) return;
        if (!b.Ghosts.Any(Conflicted)) return;
        b.Status = "blocked";
        b.Rev = Touch();
    }

    /// <summary>A quarry painted (or switched on) over builds: their cells in it are blocked now, and the overlap is said.</summary>
    private void QuarryOverBuilds(ZoneState z, List<string> warnings)
    {
        if (!IsQuarry(z)) return;
        foreach (var b in Builds.ToList())
        {
            var (n, q) = MarkConflicts(b);
            if (n > 0 && q == z) warnings.Add($"overlaps build {b.Id} ({b.Label}): the quarry digs {n} of its blocks (left unbuilt)");
        }
    }

    /// <summary>New builds whose cells a quarry digs: blocked at once, said in the answer.</summary>
    private void BuildsInQuarries(List<BuildState> made, List<string> warnings)
    {
        foreach (var b in made)
            if (MarkConflicts(b) is (> 0 and var n, { } z))
                warnings.Add($"build {b.Id}: {n} block{(n == 1 ? "" : "s")} lie in quarry {ZoneName(z)}, which digs them out: left unbuilt");
    }

    /// <summary>The paint's answer gets the overlap with builds when the painted zone is a quarry.</summary>
    private void PaintedQuarry(ZonePaintResult painted, List<string> warnings)
    {
        if (Zones.FirstOrDefault(z => z.Id == painted.Id) is { } z) QuarryOverBuilds(z, warnings);
    }

    /// <summary>A mine zone switched to quarry: the overlap with builds in the answer.</summary>
    private static void AddWarnings(JsonObject o, List<string> warnings)
    {
        if (warnings.Count > 0) o["warnings"] = new JsonArray(warnings.Select(x => (JsonNode)x!).ToArray());
    }

    // ------------------------------------------------------------------ a pawn down in the pit

    /// <summary>The highest build cell this pawn is offered: down in a quarry pit, nothing more than 3 above it while a builder up top
    /// can take it (live 11-05: rim placements went to a pawn at y 46, "its nearest support face is 18.7–23.0 blocks from my eyes").</summary>
    private int BuildCap(PawnState p, PawnLive live)
    {
        if (!live.Online || !InQuarryPit(live.Block)) return int.MaxValue;
        foreach (var o in Pawns.Values)
            if (o != p && UpTopBuilder(o)) return live.Block.Y + 3;
        return int.MaxValue;
    }

    /// <summary>Feet in a quarry's columns, more than 2 under its top.</summary>
    private bool InQuarryPit(P3 feet) =>
        Zix().Quarry.TryGetValue(ZoneCells.Key(feet.X, feet.Z), out var z) && feet.Y < QuarryTopOf(z) - 2;

    /// <summary>A builder up top (not in a pit, not under ground) that will take the cells: idle now or building now.</summary>
    private bool UpTopBuilder(PawnState o)
    {
        if (World is not { } w || !SurfacePawnFor(w, o, WorkTypes.Build)) return false;
        return !InQuarryPit(Host.Live(o.Name).Block);
    }
}
