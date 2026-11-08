using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    /// <summary>A palette placement replaces its entire bounding volume, including empty rooms. Validate and prepare
    /// its clearing work before changing any existing build. Each request contains one replacement design.</summary>
    private string? PrepareDesignReplacement(IStoryWorld w, List<BuildState> made)
    {
        var tagged = made.Where(b => b.Spec?["replace_area"] != null).ToList();
        if (tagged.Count == 0) return null;
        if (tagged.Count != 1 || Area.Parse(tagged[0].Spec!["replace_area"]) is not { } area)
            return "one replace_area [x1,y1,z1,x2,y2,z2] required per placed design";
        long width = (long)area.X2 - area.X1 + 1, height = (long)area.Y2 - area.Y1 + 1, depth = (long)area.Z2 - area.Z1 + 1;
        if (width > 262144 || height > 262144 || depth > 262144 || width * height * depth > 262144
            || area.Y1 < 1 || area.Y2 >= w.Height || area.X2 == int.MaxValue || area.Z2 == int.MaxValue)
            return "a replacement design must fit within y 1..world height and at most 262144 cells";
        if (made.Any(b => b.Kind == "mine" || b.Ghosts.Any(g => !area.Contains(g.At))))
            return "all replacement design cells must be inside replace_area";
        var group = Group(tagged[0].Spec) ?? "d" + Guid.NewGuid().ToString("N");
        if (made.Any(b => Group(b.Spec) is { } own && own != group)) return "a replacement request contains one design group";
        var explicitCells = made.SelectMany(b => b.Ghosts).Select(g => g.At).ToHashSet();
        var temps = (Meta.TempBlocks ?? []).Where(t => ReplacementAccessPawn(t) != null).Select(t => t.At).ToHashSet();
        var clear = new List<GhostState>();
        foreach (var at in area.Blocks())
        {
            if (explicitCells.Contains(at)) continue;
            var (id, meta) = w.Block(at.X, at.Y, at.Z);
            // Keep genuinely unremovable cells visible as blocked work instead of silently claiming they were cleared.
            bool blocked = id != 0 && StoryBlocks.Undiggable.Contains(id);
            clear.Add(new GhostState { At = at, Id = id, Meta = meta, Remove = true,
                State = temps.Contains(at) || id == 0 && w.Loaded(at.X, at.Z) ? "done" : blocked ? "blocked" : "todo",
                Reason = blocked ? "cannot clear " + BlockLabel(w, id) : null });
        }
        foreach (var b in made)
        {
            b.Spec!["group"] = group;
            b.Spec["replacement"] = true;
            if (b.Kind == "remove") b.Spec["replacement_clear"] = true;
            foreach (var g in b.Ghosts.Where(g => g.Remove && temps.Contains(g.At))) g.State = "done";
        }
        var digs = clear.Chunk(4096).Select(chunk => new BuildState
        {
            Kind = "remove", Ghosts = chunk.ToList(), Created = Now,
            Spec = new JsonObject { ["kind"] = "remove", ["group"] = group, ["replacement"] = true, ["replacement_clear"] = true },
        }).ToList();
        made.InsertRange(0, digs);
        return null;
    }

    private static bool ReplacementBuild(BuildState b) => b.Spec?["replacement"]?.GetValue<bool>() == true;

    /// <summary>Intentional empty space remains owned after it is cleared, including cells that started empty.</summary>
    private static bool ReplacementAir(BuildState b, GhostState g) => g.Remove && ReplacementBuild(b);

    /// <summary>The actual footing wins over old cleanup flags: keep a temporary column while any online overworld pawn stands
    /// on it. Proximity or an unfinished registry entry alone does not make an abandoned scaffold active access.</summary>
    private string? ReplacementAccessPawn(TempBlockState t) => Pawns.Keys.FirstOrDefault(pawn =>
    {
        var live = Host.Live(pawn);
        return live.Online && live.Dim == 0 && live.Y > t.At.Y && live.Y - t.At.Y <= Scripting.Story.ReachScan.MaxPillar + 2
            && Math.Abs(live.X - (t.At.X + 0.5)) <= 0.8 && Math.Abs(live.Z - (t.At.Z + 0.5)) <= 0.8;
    });

    /// <summary>Old ownership is removed permanently, including completed/paused cells and cells that match the new
    /// material. Cancelling the replacement must never resurrect an old building inside it.</summary>
    private void ReplaceOldDesignCells(List<BuildState> made)
    {
        if (made.Select(b => Area.Parse(b.Spec?["replace_area"])).FirstOrDefault(a => a != null) is not { } area) return;
        var hit = Builds.SelectMany(b => b.Ghosts.Where(g => area.Contains(g.At)).Select(g => (Build: b, Ghost: g))).ToList();
        var keys = hit.Select(h => $"{h.Build.Id}|{h.Ghost.At}").ToHashSet();
        var cleanup = Designations.Where(d => d.Auto && d.At is { } at && area.Contains(at)
            && d.Kind is "mine" or "deconstruct").Select(d => d.Id).ToHashSet();
        foreach (var (pawn, job) in _current.ToList())
            if ((job.Source == "ghost" && JobCells(job).Any(keys.Contains) || job.Source == "designation" && cleanup.Contains(job.SourceId ?? ""))
                && Pawns.TryGetValue(pawn, out var p))
                Supersede(p, "replaced by a new design");
        foreach (var temp in (Meta.TempBlocks ?? []).Where(t => area.Contains(t.At)).ToList())
            ReleaseReplacedTemporary(temp);
        foreach (var d in Designations.Where(d => cleanup.Contains(d.Id)).ToList())
        {
            _leases.Remove(DesignationKey(d));
            Designations.Remove(d);
            Removed("designation", d.Id);
        }
        foreach (var (b, g) in hit) DropGhost(b, g);
        foreach (var b in hit.Select(h => h.Build).Distinct().ToList())
            if (b.Ghosts.Count == 0 && b.Kind != "preserve") RemoveBuild(b);
        // Release the snapshot's former blocks immediately, before a pawn tries to clear them.
        foreach (var z in Zones.Where(z => z.Preserve is { On: true, Data: not null }))
        {
            var snap = z.Preserve!.Data!;
            bool changed = false;
            foreach (var (key, col) in snap.Cols)
                for (int i = 0; i < col.Length; i++)
                    if (area.Contains(ZoneCells.KX(key), snap.Lo + i, ZoneCells.KZ(key))) { col[i] = PreserveSnap.Held; changed = true; }
            if (changed) { z.Preserve.Changed(); z.Rev = Touch(); }
        }
    }

    private void ReleaseReplacedTemporary(TempBlockState temp)
    {
        if (ReplacementAccessPawn(temp) is { } standing)
        {
            // A new plan replaces old keep/defer instructions, but cleanup waits for safe footing.
            temp.Cancelled = false;
            temp.DeferredReason = null;
            temp.RetryAfter = null;
            temp.Misses = 0;
            temp.Pawn = standing;
            if (temp.Kind != "escape pillar") temp.Kind = "reach pillar";
            _dirty = true;
            return;
        }
        KeepTemp(temp);
        ForgetTemp(temp);
    }
}
