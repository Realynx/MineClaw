using System.Text.Json.Nodes;
using MineClaw.World;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private static string WorkOf(DesignationState d) => d.OreOrigin?.Type ?? WorkOf(d.Kind);

    // Discover only faces actually opened by authorized mining. The frontier is saved as ordinary, cancellable marks;
    // we never scan arbitrary underground ore or append it to a design's original access geometry.
    private ActiveJob FollowOre(ActiveJob job)
    {
        if (job.Kind != "dig" || job.At is not { } at) return job;
        job.OreOrigin ??= MiningOrigin(job);
        if (job.OreOrigin == null) return job;
        job.DigCells ??= [at];
        job.Json["dig_cells"] = new JsonArray(job.DigCells.Select(c => (JsonNode)c.ToString()).ToArray());
        job.Json["follow_veins"] = true;
        if (job.Source == "zone") job.Json["vein_quarry"] = Zones.Any(z => z.Id == job.SourceId && IsQuarry(z));
        // Ghost jobs otherwise reserve only g: coordinates, which would allow an ore mark to reserve the same block.
        foreach (var c in job.DigCells)
            if (job.Lease != "b:" + c && !job.MoreLeases.Contains("b:" + c)) job.MoreLeases.Add("b:" + c);
        return job;
    }

    private OreWorkOrigin? MiningOrigin(ActiveJob j)
    {
        if (j.Source == "zone" && j.Type == WorkTypes.Mine && j.SourceId is { } zone)
            return new("zone", zone, j.Type);
        if (j.Source == "ghost" && j.SourceId?.Split('|')[0] is { } bid && Builds.Any(b => b.Id == bid && b.Kind == "mine"))
            return new("build", bid, j.Type);
        if (j.Source == "designation" && Designations.FirstOrDefault(d => d.Id == j.SourceId && d.Kind == "mine") is { } mark)
            return mark.OreOrigin ?? new("designation", mark.Id, j.Type);
        return null;
    }

    /// <summary>Called after the atomic dig, before its per-cell report consumes the reservation. Null means stop.</summary>
    public JsonArray? DigExposed(string body, string job, string cell, int beforeId, int beforeMeta)
    {
        lock (_lock)
        {
            if (!_current.TryGetValue(body, out var j) || j.Id != job || j.OreOrigin is not { } origin
                || !Pawns.TryGetValue(body, out var p) || ParseCell(cell) is not { } at
                || j.DigCells?.Contains(at) != true || !OreParentActive(origin)) return null;
            var answer = new JsonArray();
            var w = Host.ViewOf(body) ?? World;
            if (w == null || !w.Loaded(at.X, at.Z) || !OpenCell(w.Block(at.X, at.Y, at.Z).Id)
                || !j.DigExposedCells.Add(at)) return answer;
            int ore = OreIdentity.Canonical(beforeId, Modern);
            var live = Host.Live(body);
            _toolPlans.Clear(); // the tool may have broken or the body's inventory may have changed during the batch
            foreach (var n in Neighbours(at))
            {
                if (j.DigCells.Contains(n)) continue;
                var d = ExposedOre(w, new OreFrontier(n, ore, origin));
                if (d == null || j.DigCells.Contains(n) || j.DigCells.Count >= 64
                    || !VeinWorkable(p, live, w, d) || PlanTool(p, live, d.Ore) is not { Wait: null, Take: null } tool
                    || tool.Hold != j.Json["tool"]?.GetValue<string>() || tool.HoldEnch != j.Json["tool_ench"]?.GetValue<string>()) continue;
                j.DigCells.Add(n);
                j.DigDesignations[n] = d.Id;
                string key = "b:" + n;
                j.MoreLeases.Add(key);
                _leases[key] = (body, Now + LeaseTtl);
                answer.Add(new JsonObject { ["at"] = n.ToString(), ["ore"] = d.Ore });
            }
            return answer;
        }
    }

    private DesignationState? ExposedOre(IStoryWorld w, OreFrontier edge)
    {
        var at = edge.At;
        if (at.Y <= 0 || at.Y >= w.Height || Meta.CancelledOre?.Contains(at) == true || OreAccessProtected(at)) return null;
        if (!w.Loaded(at.X, at.Z))
        {
            Meta.OreFrontiers ??= [];
            if (!Meta.OreFrontiers.Contains(edge)) { Meta.OreFrontiers.Add(edge); Touch(); }
            return null;
        }
        int ore = OreIdentity.Canonical(w.Block(at.X, at.Y, at.Z).Id, Modern);
        if (ore == 0 || edge.Ore != 0 && ore != edge.Ore) return null;
        // An original mine cell stays its blueprint's job; it will expose the next face when actually dug.
        if (Builds.Any(b => b.Kind == "mine" && b.Ghosts.Any(g => g.At == at && g.Remove && g.State != "done"))) return null;
        var existing = Designations.FirstOrDefault(d => d.At == at && d.Kind is "mine" or "deconstruct");
        if (existing != null) return existing.OreOrigin == edge.Origin ? existing : null;
        var d = new DesignationState
        {
            Id = NextId("d"), Kind = "mine", At = at, Auto = true, Ore = ore, OreOrigin = edge.Origin,
            Label = "following " + BlockIdName(w, ore).Replace('_', ' ') + " vein", Created = Now, Rev = Touch(),
        };
        Designations.Add(d);
        WakeIdle();
        return d;
    }

    private ActiveJob? VeinJob(PawnState p, PawnLive live, string type)
    {
        if (World is not { } w) return null;
        foreach (var d in Designations.Where(d => d.OreOrigin?.Type == type && d.At != null).OrderBy(d => d.At!.Value.Dist(live.Block)).ToList())
        {
            if (!VeinWorkable(p, live, w, d)) continue;
            var at = d.At!.Value;
            var tool = PlanTool(p, live, d.Ore);
            if (tool is { Wait: { } why }) { NoteWait(d, why); continue; }
            NoteWait(d, null);
            // No free slot: its dig batch would dig nothing and ask again at once (StoryColony.StorageFull.cs).
            if (live.FreeSlots <= 0) { PocketsFullNote(p, live, $"ore vein at {at}"); return null; }
            var j = WithTool(tool, Job(type, "dig", d.Label!, at, "b:" + at, "designation", d.Id));
            j.OreOrigin = d.OreOrigin;
            j.DigDesignations[at] = d.Id;
            j.Json["vein_ores"] = new JsonObject { [at.ToString()] = d.Ore };
            return FollowOre(j);
        }
        return null;
    }

    private bool VeinWorkable(PawnState p, PawnLive live, IStoryWorld w, DesignationState d)
    {
        if (d.OreOrigin is not { } origin || d.At is not { } at || !OreParentActive(origin)
            || !Open("b:" + at, p.Name, at) || !w.Loaded(at.X, at.Z)
            || OreIdentity.Canonical(w.Block(at.X, at.Y, at.Z).Id, Modern) != d.Ore
            || OreAccessProtected(at) || !Exposed(w, at)
            || Support(live.X, live.Y, live.Z).Contains(at) || StoodOnByOthers(p.Name).Contains(at)) return false;
        if (!OreIdentity.ExposureSafe(new StoryBlockAccess(w), new BlockPos(at.X, at.Y, at.Z)))
        {
            NoteWait(d, "ore waits for loaded, safe access without liquids or falling blocks");
            return false;
        }
        if (QuarryVeinWait(w, origin, at) is { } wait)
        {
            NoteWait(d, wait);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Why ore followed from a quarry waits, else null (any other origin: null). It needs what a dig of the quarry itself needs (no
    /// spawner stop, its stair whole, no seal to place first), and never opens the pit to a cave: vein jobs come before quarry work, so
    /// they dug past all three, and a wall ore beside a cave let the cave into the pit. Wall ore beside rock or a closed pocket (an ore
    /// hole dug before) is still followed out of the zone's columns.
    /// </summary>
    private string? QuarryVeinWait(IStoryWorld w, OreWorkOrigin origin, P3 at)
    {
        if (origin.Source != "zone" || Zones.FirstOrDefault(z => z.Id == origin.Id) is not { } z || !IsQuarry(z)
            || Quarry(z) is not { } q) return null;
        if (!q.Done && (q.Spawner != null || StairBroken(q) || SealPending(q)))
            return $"waits for quarry {ZoneName(z)}: " + (q.Spawner == null && StairBroken(q) ? "its stair comes first" : HazardWait(z, q));
        foreach (var n in Neighbours(at))
            if (!z.Cells.Contains(n.X, n.Z) && w.Loaded(n.X, n.Z) && CaveAir(w, n) && QuarryAt(n) == null && !ClosedPocket(w, q, n))
                return $"would open the quarry to a cave at {n}";
        return null;
    }

    private bool OreParentActive(OreWorkOrigin origin) => origin.Source switch
    {
        "build" => Builds.Any(b => b.Id == origin.Id && b.Kind == "mine" && b.Status != "paused"),
        "zone" => Zones.Any(z => z.Id == origin.Id && z.Kind == "mine"),
        // A successfully mined manual seed disappears. Its exposed remainder still belongs to that completed instruction.
        "designation" => true,
        _ => false,
    };

    private bool OreAccessProtected(P3 at)
    {
        if (Forbidden(at) || PreservedAt(at) != null || Meta.TempBlocks?.Any(t => t.At == at) == true
            || Builds.Any(b => b.Ghosts.Any(g => g.At == at && !g.Remove)) || MineAccessFloors().Contains(at)) return true;
        foreach (var z in Zones.Where(IsQuarry))
            if (z.Contains(at.X, at.Z) && StoryQuarry.Ring(z.Cells) is { } ring)
            {
                var (top, first) = QuarryStairStart(z); // the leading steps over lower ground are no part of the stair
                int k = top - at.Y;
                if (k >= first && StoryQuarry.Step(ring, top, k) == at) return true;
                if (k > first && StoryQuarry.Step(ring, top, k - 1) == at.Add(0, 1, 0)
                    && World is { } w && StepNeedsBlock(w, at.Add(0, 1, 0)) != null) return true;
            }
        return false;
    }

    private List<(BuildState Build, JsonObject? Spec)> _oreFloorSources = [];
    private HashSet<P3> _oreFloors = [];

    private HashSet<P3> MineAccessFloors()
    {
        var sources = Builds.Where(b => b.Kind == "mine").Select(b => (Build: b, Spec: b.Spec)).ToList();
        if (_oreFloorSources.SequenceEqual(sources)) return _oreFloors;
        _oreFloorSources = sources;
        _oreFloors = [];
        foreach (var group in sources.GroupBy(s => Group(s.Spec) ?? s.Build.Id))
        {
            var digs = new HashSet<P3>();
            foreach (var (b, spec) in group)
            {
                if (spec?["blocks"] is JsonArray cells)
                    foreach (var cell in cells) { if (P3.Parse(cell) is { } c) digs.Add(c); }
                else if (Area.Parse(spec?["area"]) is { } area) digs.UnionWith(area.Blocks());
                digs.UnionWith(b.Ghosts.Where(g => g.Remove).Select(g => g.At));
            }
            foreach (var d in digs)
                if (!digs.Contains(d.Add(0, -1, 0))) _oreFloors.Add(d.Add(0, -1, 0));
        }
        return _oreFloors;
    }
}
