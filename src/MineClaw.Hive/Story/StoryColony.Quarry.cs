using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

/// <summary>
/// Quarry zones (docs/web/story-mode.md "Quarries"). Fox 11-04: "The quarry zone didn't make sense — nothing happened." A mine zone
/// in <c>quarry</c> mode (the default for new ones; <c>ores</c> keeps the old ore-only behaviour) digs everything in its columns,
/// layer by layer from its top down to bedrock (never bedrock, never y 0), leaving the perimeter stair (<see cref="StoryQuarry"/>)
/// standing: a step that is air, liquid or would fall (sand, gravel) gets a cheap block placed first. A layer starts once the one
/// above has nothing left to dig (resting cells don't hold it back). Cells beside lava or water are left standing (a seal), with an
/// alert. Mine work; exposed cells only; the pickaxe its material needs (StoryColony.Gear.cs); loot is batched like any gathering.
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>What a quarry has now (rescanned at most every 5 s, and after each of its jobs).</summary>
    internal sealed class QuarryScan
    {
        public DateTimeOffset At;
        public int Top, Bottom, Layer;
        public bool Done;
        public List<(int X, int Z)>? Ring;
        public readonly List<P3> Dig = new();
        public readonly List<(P3 At, bool Clear)> StepFix = new();
        public int Hazards;
    }

    private readonly Dictionary<string, QuarryScan> _quarryScan = new();
    private readonly Dictionary<string, DateTimeOffset> _quarryAlert = new();
    private const int QuarryLayersPerScan = 24;

    internal static bool IsQuarry(ZoneState z) => z.Kind == "mine" && z.Mode == "quarry";

    /// <summary>The ground under open air in a column near the zone's level (no air, plant, log, leaf or liquid).</summary>
    private static int? GroundTop(IStoryWorld w, int x, int z, int level)
    {
        if (!w.Loaded(x, z)) return null;
        for (int y = Math.Min(w.Height - 1, level + 16); y >= Math.Max(1, level - 16); y--)
        {
            int id = w.Block(x, y, z).Id;
            if (OpenCell(id) || StoryBlocks.Liquids.Contains(id) || StoryBlocks.Logs.Contains(id) || StoryBlocks.Leaves.Contains(id)) continue;
            return y;
        }
        return null;
    }

    /// <summary>The lowest y a column is dug to: one over its highest bedrock (y 0..8), else 1.</summary>
    private static int ColumnBottom(IStoryWorld w, int x, int z)
    {
        for (int y = 8; y >= 0; y--)
            if (w.Block(x, y, z).Id == StoryQuarry.Bedrock) return y + 1;
        return 1;
    }

    internal QuarryScan? Quarry(ZoneState z)
    {
        if (World is not { } w) return null;
        if (_quarryScan.TryGetValue(z.Id, out var c) && Now - c.At < TimeSpan.FromSeconds(5)) return c;
        var q = new QuarryScan { At = Now, Ring = StoryQuarry.Ring(z.Cells) };
        var cols = z.Cells.Sorted();
        if (z.QuarryTop is not { } top)
        {
            var tops = cols.Select(cc => GroundTop(w, cc.X, cc.Z, z.Area.Y2)).OfType<int>().ToList();
            if (tops.Count == 0) { _quarryScan[z.Id] = q; q.Done = true; return q; } // nothing seen yet
            z.QuarryTop = top = tops.Max();
            z.Rev = Touch();
        }
        q.Top = top;
        var bottoms = cols.ToDictionary(cc => cc, cc => ColumnBottom(w, cc.X, cc.Z));
        q.Bottom = bottoms.Count > 0 ? bottoms.Values.Min() : 1;
        int maxK = top - q.Bottom;
        int k = Math.Max(0, z.QuarryLayer);
        for (int scanned = 0; k <= maxK && scanned < QuarryLayersPerScan; k++, scanned++)
        {
            ScanLayer(w, z, q, k, bottoms);
            if (q.Dig.Any(p => !Skipped("b:" + p)) || q.StepFix.Count > 0) break;
            q.Dig.Clear();
        }
        q.Layer = k;
        q.Done = k > maxK;
        if (k != z.QuarryLayer && k <= maxK + 1) { z.QuarryLayer = Math.Min(k, maxK + 1); z.Rev = Touch(); }
        if (q.Hazards > 0) QuarryHazardAlert(z, q);
        _quarryScan[z.Id] = q;
        return q;
    }

    /// <summary>One layer: its cells to dig (not a step, not bedrock or below, not beside lava or water), and its stair to fix.</summary>
    private void ScanLayer(IStoryWorld w, ZoneState z, QuarryScan q, int k, Dictionary<(int X, int Z), int> bottoms)
    {
        int y = q.Top - k;
        q.StepFix.Clear();
        q.Hazards = 0;
        var steps = new HashSet<P3>();
        if (q.Ring is { } ring)
        {
            // This layer's step and the ones just above and below it: fixed before the layer under them is dug.
            for (int j = Math.Max(0, k - 1); j <= Math.Min(k + 1, q.Top - q.Bottom); j++)
            {
                var s = StoryQuarry.Step(ring, q.Top, j);
                steps.Add(s);
                int id = w.Block(s.X, s.Y, s.Z).Id;
                if (OpenCell(id) || StoryBlocks.Liquids.Contains(id) || StoryQuarry.Falls(id)) q.StepFix.Add((s, StoryQuarry.Falls(id)));
            }
            steps.Add(StoryQuarry.Step(ring, q.Top, k));
        }
        var underFix = q.StepFix.Select(f => f.At.Add(0, -1, 0)).ToHashSet();
        foreach (var (x, zz) in z.Cells.Sorted())
        {
            var at = new P3(x, y, zz);
            if (y < bottoms[(x, zz)] || steps.Contains(at) || underFix.Contains(at) || !w.Loaded(x, zz)) continue;
            int id = w.Block(x, y, zz).Id;
            if (OpenCell(id) || id == StoryQuarry.Bedrock) continue;
            if (StoryBlocks.Liquids.Contains(id) || BesideLiquid(w, at)) { q.Hazards++; continue; } // left standing: a seal
            q.Dig.Add(at);
        }
    }

    private static bool BesideLiquid(IStoryWorld w, P3 at)
    {
        foreach (var (dx, dy, dz) in new[] { (1, 0, 0), (-1, 0, 0), (0, 0, 1), (0, 0, -1), (0, 1, 0), (0, -1, 0) })
            if (StoryBlocks.Liquids.Contains(w.Block(at.X + dx, at.Y + dy, at.Z + dz).Id)) return true;
        return false;
    }

    private void QuarryHazardAlert(ZoneState z, QuarryScan q)
    {
        if (_quarryAlert.TryGetValue(z.Id, out var last) && Now - last < TimeSpan.FromMinutes(10)) return;
        _quarryAlert[z.Id] = Now;
        Alert("warn", $"Quarry {ZoneName(z)}: {q.Hazards} cell(s) at y {q.Top - q.Layer} left standing beside lava or water (a seal)");
    }

    /// <summary>The quarry's next job for this pawn: a stair step to place, else the nearest exposed cell of the current layer.</summary>
    private ActiveJob? QuarryJob(PawnState p, PawnLive live, ZoneState z)
    {
        if (World is not { } w || Quarry(z) is not { Done: false } q) return null;
        var from = live.Block;
        foreach (var (at, clear) in q.StepFix.OrderBy(f => f.At.Dist(from)))
        {
            if (!Open("b:" + at, p.Name, at)) continue;
            var block = live.Inventory.FirstOrDefault(s => PillarAny.Contains(s.Id));
            (StorageState Store, Stack Stack)? stored = block == null ? FindStored(s => PillarAny.Contains(s.Id), from) : null;
            if (block == null && stored == null)
            {
                if (!_quarryAlert.TryGetValue("stair:" + z.Id, out var t) || Now - t > TimeSpan.FromMinutes(10))
                {
                    _quarryAlert["stair:" + z.Id] = Now;
                    Alert("warn", $"Quarry {ZoneName(z)} needs a block for its stair (cobblestone or dirt): none carried or stored");
                }
                break;
            }
            var item = block ?? stored!.Value.Stack;
            var j = Job(WorkTypes.Mine, "place", "building the quarry stair", at, "b:" + at, "zone", z.Id, "working", f =>
            {
                f["block"] = ItemName(item.Id, item.Meta);
                if (clear) f["clear"] = true;
                if (stored is { } st) f["fetch"] = Fetch(st.Store.Chest, st.Stack, 16);
            }, xp: 5);
            if (stored is { } st2) j.Takes.Add((st2.Store.Chest, st2.Stack with { Count = Math.Min(16, st2.Stack.Count) }));
            return j;
        }
        var mine = live.Online ? Support(live.X, live.Y, live.Z).ToHashSet() : [];
        HashSet<P3>? stoodOn = null;
        var targets = q.Dig.ToHashSet();
        foreach (var at in q.Dig.OrderBy(c => c.Dist(from) + (mine.Contains(c) ? 1000 : 0)))
        {
            if (!Open("b:" + at, p.Name, at)) continue;
            int id = w.Block(at.X, at.Y, at.Z).Id;
            if (OpenCell(id) || !Exposed(w, at)) continue; // dug since the scan, or buried
            if ((stoodOn ??= StoodOnByOthers(p.Name)).Contains(at)) continue;
            var tool = PlanTool(p, live, id);
            if (tool is { Wait: not null }) continue;
            P3? stand = mine.Contains(at) ? StandBeside(w, at, c => c == at || targets.Contains(c)) : null;
            return WithTool(tool, Job(WorkTypes.Mine, "dig", $"quarrying (y {at.Y})", at, "b:" + at, "zone", z.Id, "working",
                f => { if (stand is { } s) f["stand"] = s.ToString(); }));
        }
        return null;
    }

    /// <summary><c>update {id, mode: ores | quarry}</c> on a mine zone.</summary>
    private (JsonNode? Result, string? Error) MineModeOp(ZoneState z, string? mode)
    {
        if (mode is not ("ores" or "quarry")) return (null, "mode must be ores|quarry");
        if (mode == "quarry" && StoryQuarry.Ring(z.Cells) == null) return (null, QuarryTooNarrow);
        z.Mode = mode;
        z.Rev = Touch();
        _quarryScan.Remove(z.Id);
        _zoneScan.Remove(z.Id);
        WakeIdle();
        return (new JsonObject { ["id"] = z.Id, ["mode"] = mode }, null);
    }

    internal const string QuarryTooNarrow = "a quarry needs at least 2×2 (a 1-wide strip is a trench: paint it as Ores only, or designate Mine)";

    /// <summary>The state's quarry fields: mode, and for a quarry its progress and stair.</summary>
    private void QuarryJson(ZoneState z, JsonObject o)
    {
        o["mode"] = z.Mode ?? "ores";
        if (!IsQuarry(z) || Quarry(z) is not { } q || z.QuarryTop == null) return;
        var steps = new JsonArray();
        if (q.Ring is { } ring)
            for (int k = 0; k <= q.Top - q.Bottom && steps.Count < 512; k++) steps.Add(StoryQuarry.Step(ring, q.Top, k).Json());
        o["quarry"] = new JsonObject
        {
            ["top"] = q.Top, ["bottom"] = q.Bottom, ["depth"] = q.Top - q.Bottom + 1, ["layer"] = q.Layer, ["y"] = q.Top - q.Layer,
            ["cells_left"] = q.Dig.Count, ["stair_fixes"] = q.StepFix.Count, ["sealed"] = q.Hazards, ["done"] = q.Done, ["stair"] = steps,
            ["status"] = q.Done ? "done: down to bedrock" : q.Ring == null ? "no stair: the zone is too narrow" : $"layer {q.Layer + 1} of {q.Top - q.Bottom + 1} (y {q.Top - q.Layer}), {q.Dig.Count} cell(s) left",
        };
    }
}
