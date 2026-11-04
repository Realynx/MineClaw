using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MineClaw.Hive.Story;

/// <summary>
/// Retrying failed build cells (docs/web/story-mode.md "Retrying failed build cells"; Fox 11-04: "There are some water channels I
/// can't unblock to retry to make them fill"). The Retry tool covered designations only: a build cell that failed three times stayed
/// <c>blocked</c> for good, and one resting after a failure waited out its back-off. <c>POST …/builds {op:"retry", id|ids, cells?}</c>
/// reopens a build's failed or resting cells (all of them, or the given ones), <c>{op:"retry_all"}</c> every build's: their failure
/// counts, rests and per-pawn skips are forgotten, a blocked build is queued again, and its checks run at once, so a reason that still
/// holds (no bucket, no water to fill from, a block in the cell) is said now instead of after three more failures.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>One ghost as the state sends it: [x, y, z, id, meta, build, state, kind] and, for a failed or unsupported cell, its
    /// reason as a 9th element (the map's failed-cell marker).</summary>
    private static JsonNode GhostJson(BuildState b, GhostState g)
    {
        // The 8th element: dig (Id/Meta = the block expected there: the live world's when queued), place, or pour (a liquid source).
        var a = new JsonArray(g.At.X, g.At.Y, g.At.Z, g.Id, g.Meta, b.Id, g.State, g.Remove ? "dig" : StoryConstruction.IsLiquid(g) ? "pour" : "place");
        if (g.Reason != null && g.State != "done") a.Add(g.Reason);
        return a;
    }

    /// <summary>A cell that failed (blocked, or resting after a failure, or with failures on record).</summary>
    private bool CellFailedNow(BuildState b, GhostState g)
    {
        if (!g.Job || g.State is "done" or "unsupported") return false;
        var id = $"{b.Id}|{g.At}";
        return g.State is "blocked" or "misrotated" || _ghostFails.ContainsKey(id) || _softFails.ContainsKey("g:" + id) || _skip.ContainsKey("g:" + id);
    }

    /// <summary>How many build cells have failed (the web's "Retry all failed (N)" counts them with the failed designations).</summary>
    public int FailedCells()
    {
        lock (_lock) return Builds.Sum(b => b.Ghosts.Count(g => CellFailedNow(b, g)));
    }

    /// <summary><c>{op:"retry", ids, cells?}</c> | <c>{op:"retry_all"}</c> → <c>{ids, reopened, reasons:[{id, reason}]}</c>.</summary>
    public (JsonNode? Result, string? Error) RetryBuilds(string op, IReadOnlyList<string>? ids, IReadOnlyList<P3>? cells)
    {
        if (op == "retry" && ids is not { Count: > 0 }) return (null, "id (or ids) required");
        lock (_lock)
        {
            var hit = op == "retry_all" ? Builds.Where(b => b.Status != "done" || b.Ghosts.Any(g => g.State == "misrotated")).ToList() : Builds.Where(b => ids!.Contains(b.Id)).ToList();
            if (op == "retry" && hit.Count == 0) return (null, "no such build");
            var cellSet = cells?.ToHashSet();
            var reasons = new JsonArray();
            int reopened = hit.Sum(b => RetryBuild(b, cellSet, reasons));
            MaterialsChanged();
            WakeIdle();
            return (new JsonObject
            {
                ["ids"] = new JsonArray(hit.Select(b => (JsonNode)b.Id!).ToArray()), ["reopened"] = reopened, ["reasons"] = reasons,
            }, null);
        }
    }

    /// <summary>One build's retry: its failed cells (or those of <paramref name="cellSet"/>) start over, it is queued again, and its checks
    /// run now (a reason that still holds blocks it again and joins <paramref name="reasons"/>). → how many cells reopened.</summary>
    private int RetryBuild(BuildState b, HashSet<P3>? cellSet, JsonArray reasons)
    {
        var targets = b.Ghosts.Where(g => g.Job && g.State is not ("done" or "unsupported") && (cellSet == null || cellSet.Contains(g.At))).ToList();
        int reopened = 0;
        foreach (var g in targets)
        {
            bool failed = CellFailedNow(b, g);
            ForgetCellFailures(b, g);
            if (!failed) continue;
            reopened++;
            g.Rev = Touch();
        }
        if (b.Status is "blocked" or "waiting") { b.Status = "queued"; b.Reason = null; }
        if (b.Status == "done" && b.Ghosts.Any(g => g.Job && g.State == "todo")) b.Status = "queued"; // a misrotated cell reopened
        b.Rev = Touch();
        _buildAlerted.Remove(b.Id);
        if (RetryCheck(b, targets) is { } why)
        {
            b.Status = "blocked";
            b.Reason = why;
            reasons.Add(new JsonObject { ["id"] = b.Id, ["reason"] = why });
        }
        return reopened;
    }

    /// <summary>A cell starts over: blocked → todo, its failure count, rest, soft failures and per-pawn skips forgotten.</summary>
    private void ForgetCellFailures(BuildState b, GhostState g)
    {
        var id = $"{b.Id}|{g.At}";
        var key = "g:" + id;
        if (g.State is "blocked" or "misrotated") g.State = "todo";
        _turnRedo.Remove(key); // a misrotated cell gets its one take-back again (StoryColony.Builds.cs)
        g.Reason = null;
        _ghostFails.Remove(id);
        _softFails.Remove(key);
        _leaseFails.Remove(key);
        _skip.Remove(key);
        ForgetPawnFails(key);
    }

    /// <summary>
    /// What still stops a retried build, said at once: a liquid build's cells (a block in the cell, nothing under it, lava off), its
    /// bucket (none anywhere and none to make), and water to fill from (no source and no full bucket carried). Null = go ahead (the
    /// pick finds the rest, as for any build).
    /// </summary>
    private string? RetryCheck(BuildState b, List<GhostState> cells)
    {
        if (World is not { } w || LiquidOfBuild(b) is not { } liquid) return null;
        if (liquid == "lava" && !Meta.AllowLava) return "lava is off (the colony option allow_lava)";
        return RetryCellsProblem(w, b, cells, liquid) ?? WaterSupplyProblem(w, b, cells, liquid == "lava");
    }

    private string? RetryCellsProblem(IStoryWorld w, BuildState b, List<GhostState> cells, string liquid)
    {
        var own = b.Ghosts.Select(g => g.At).ToHashSet();
        var dug = DugFirst(b.Spec ?? new JsonObject(), []);
        foreach (var g in cells)
        {
            var (id, _) = w.Block(g.At.X, g.At.Y, g.At.Z);
            if (!TakesLiquid(id) && !dug.Contains(g.At)) return $"{g.At} holds {BlockLabel(w, id)}: dig it out and the {liquid} goes in";
            if (!Held(w, g.At.Add(0, -1, 0), g.Id, own, dug)) return $"nothing under {g.At} to hold the {liquid}: it would pour down";
            if (PourFaceProblem(w, g.At, g.Id, liquid, own, dug) is { } face) return face;
        }
        return null;
    }

    /// <summary>A bucket (or one being made) and water to fill it from (or a full one carried, or an earlier water build that will be
    /// the source). Ponds a pawn couldn't fill at are tried again.</summary>
    private string? WaterSupplyProblem(IStoryWorld w, BuildState b, List<GhostState> cells, bool lava)
    {
        if (BucketsHeld() == 0 && b.Materials.FirstOrDefault(m => m.Item == "bucket") is not { Crafting: > 0 })
            return "needs a bucket (3 iron ingots at a crafting table)";
        _fillMisses.Clear();
        if (cells.Count == 0 || FullBucketCarried(lava) || EarlierWaterBuild(b) != null) return null;
        if (FillSource(w, lava, cells[0].At, cells.Select(c => c.At).ToHashSet()) != null) return null;
        return lava ? $"no lava source within {SourceRadius} blocks to fill a bucket from"
            : $"no still water within {SourceRadius} blocks to fill a bucket from (a lake or pool with dry ground beside it, or an infinite source)";
    }

    private bool FullBucketCarried(bool lava)
    {
        int fullId = lava ? MineClaw.Data.Items.LavaBucket : MineClaw.Data.Items.WaterBucket;
        return Pawns.Values.Any(p => Host.Live(p.Name) is { Online: true } l && l.Inventory.Any(s => s.Id == fullId));
    }

    /// <summary>Failures the canal bugs of 11-04 caused ("no block face to pour against", a refilling source refused as protected, a pond
    /// out of reach): water cells blocked by them start over once, on the first load after the fix.</summary>
    private static readonly Regex OldWaterFailure = new(@"no block face|protected|no reachable visible source|no path to any|bucket didn't fill|can't get near",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>On load, once (<see cref="ColonyMeta.WaterRetried"/>): blocked water cells reopen (cells blocked before this fix carry
    /// no reason of their own) and a water build blocked by one of those reasons is queued again.</summary>
    internal bool MigrateWaterFailures() => MigrateWaterCells() | MigrateOldWaterFailures();

    private bool MigrateOldWaterFailures()
    {
        lock (_lock)
        {
            if (Meta.WaterRetried) return false;
            Meta.WaterRetried = true;
            foreach (var b in Builds.Where(b => LiquidOfBuild(b) != null))
            {
                foreach (var g in b.Ghosts.Where(g => g.State == "blocked" && (g.Reason == null || OldWaterFailure.IsMatch(g.Reason))))
                {
                    g.State = "todo";
                    g.Reason = null;
                }
                if (b.Status == "blocked" && (b.Reason == null || OldWaterFailure.IsMatch(b.Reason))) { b.Status = "queued"; b.Reason = null; }
            }
            return true;
        }
    }

    /// <summary>
    /// On load, once (<see cref="ColonyMeta.WaterCellsFixed"/>): a water build's pour cell right above a cell its own group digs (the trench
    /// cell under it) moves down into that dug cell, which is where the channel's water goes; water cells that failed with "nothing beside
    /// or under" start over (live 11-04: 371 such failures). The queue-time check refuses both shapes for new builds.
    /// </summary>
    private bool MigrateWaterCells()
    {
        lock (_lock)
        {
            if (Meta.WaterCellsFixed) return false;
            Meta.WaterCellsFixed = true;
            foreach (var b in Builds.Where(b => LiquidOfBuild(b) != null && b.Status != "done")) LowerOntoDugCells(b);
            return true;
        }
    }

    private void LowerOntoDugCells(BuildState b)
    {
        var dug = GroupDigCells(b);
        var taken = b.Ghosts.Select(g => g.At).ToHashSet();
        bool changed = false;
        foreach (var g in b.Ghosts.Where(g => g.State != "done"))
        {
            var below = g.At.Add(0, -1, 0);
            bool moved = dug.Contains(below) && !dug.Contains(g.At) && taken.Add(below);
            if (moved) g.At = below;
            if (!moved && g.Reason?.Contains("nothing beside or under", StringComparison.Ordinal) != true) continue;
            g.State = "todo";
            g.Reason = null;
            changed = true;
        }
        if (changed && b.Status == "blocked") { b.Status = "queued"; b.Reason = null; }
    }

    /// <summary>Every cell the build's group digs, done or not (a placed design's or a channel's remove items).</summary>
    private HashSet<P3> GroupDigCells(BuildState b) =>
        Group(b.Spec) is not { } group ? [] : Builds.Where(o => Group(o.Spec) == group).SelectMany(o => o.Ghosts).Where(g => g.Remove).Select(g => g.At).ToHashSet();
}
