using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private static bool FreecamBuild(BuildState b) => b.Spec?["freecam_edit"] is JsonValue v && v.TryGetValue<bool>(out var yes) && yes;
    private static bool ExactBuild(BuildState b) => b.Spec?["exact_state"] is JsonValue v && v.TryGetValue<bool>(out var yes) && yes;

    private static JsonObject BuildMetadata(BuildState build, JsonObject json)
    {
        if (Group(build.Spec) is { } group) json["group"] = group;
        if (build.Spec?["name"] is JsonValue v && v.TryGetValue<string>(out var name) && name.Length > 0) json["name"] = name;
        return json;
    }

    /// <summary>Edit only the named cells of placed plans. No server blocks or reusable design templates are changed.
    /// A group's mixed edit item owns its exact replacements and intentional air, while unrelated old work stays untouched.</summary>
    public (JsonNode? Result, string? Error) EditBuildCells(string? buildId, JsonArray? cells)
    {
        lock (_lock)
        {
            if (World is not { } world) return (null, "no world view yet");
            if (cells is not { Count: > 0 and <= 4096 }) return (null, "cells required (1..4096)");
            var anchor = buildId != null ? Builds.FirstOrDefault(b => b.Id == buildId) : null;
            if (buildId != null && anchor == null) return (null, "no such build");
            if (anchor?.Kind == "preserve") return (null, "select a placed build, not a preserved-zone repair item");
            var input = new JsonObject { ["kind"] = "block", ["exact_state"] = true, ["cells"] = cells?.DeepClone() };
            var (ghosts, _, error) = StoryConstruction.Expand(world.Data, input, world);
            if (error != null) return (null, error);
            // Even a complete, otherwise valid batch is rejected before IDs, leases, snapshots or plans change.
            foreach (var g in ghosts)
                if (world.Loaded(g.At.X, g.At.Z) && world.Block(g.At.X, g.At.Y, g.At.Z).Id is var id
                    && id != 0 && StoryBlocks.Undiggable.Contains(id)) return (null, $"cannot replace unremovable block at {g.At}");

            var selected = ghosts.Select(g => g.At).ToHashSet();
            var owners = Builds.Where(b => b.Kind != "preserve").OrderBy(b => b.Created)
                .SelectMany(b => b.Ghosts.Where(g => selected.Contains(g.At)).Select(g => (g.At, Build: b)))
                .GroupBy(x => x.At).ToDictionary(g => g.Key, g => g.Last().Build);
            var targets = new Dictionary<string, (BuildState? Anchor, List<GhostState> Cells)>(StringComparer.Ordinal);
            foreach (var g in ghosts)
            {
                var owner = anchor ?? owners.GetValueOrDefault(g.At);
                string key = owner == null ? "new" : Group(owner.Spec) is { } group ? "group:" + group : "build:" + owner.Id;
                if (!targets.TryGetValue(key, out var target)) targets[key] = target = (owner, []);
                target.Cells.Add(g);
            }
            // A persistent edit item has a bounded sparse spec too; reject oversize extension atomically.
            foreach (var target in targets.Values)
                if (FindEditBuild(target.Anchor) is { } edit
                    && edit.Ghosts.Count(g => !selected.Contains(g.At)) + target.Cells.Count > 4096)
                    return (null, "an edited plan contains at most 4096 cells");

            var destinations = new Dictionary<P3, BuildState>();
            var edited = new List<BuildState>();
            foreach (var target in targets.Values)
            {
                var edit = FindEditBuild(target.Anchor) ?? NewEditBuild(target.Anchor);
                edited.Add(edit);
                foreach (var g in target.Cells) destinations[g.At] = edit;
            }
            RetireEditedCells(selected, destinations);
            var temps = ActiveTempCells();
            foreach (var g in ghosts)
            {
                var build = destinations[g.At];
                MarkBuiltAlready(world, g, Touch());
                if (temps.Contains(g.At)) g.State = "todo"; // active access must be recovered before its replacement
                build.Ghosts.Add(g);
                if (g.Remove && g.State == "done") Removed("ghost", $"{build.Id}|{g.At}");
            }
            ChestRuleCells(world, ghosts.Select(g => (destinations[g.At], g)), null); // chests the game can't place: blocked, alerted (StoryColony.Builds.cs)
            foreach (var build in edited)
            {
                SaveSparseBuildSpec(build);
                build.Label = $"freecam edits ({build.Ghosts.Count} cells)";
                build.Material = "mixed";
                if (build.Status != "paused") build.Status = AllBuilt(build) ? "done" : "queued";
                build.Reason = null;
                build.Rev = Touch();
            }
            HoldEditedSnapshots(selected);
            BuildConflictsPass();
            MaterialsChanged();
            WakeIdle();
            return (new JsonObject
            {
                ["ids"] = new JsonArray(edited.Select(b => (JsonNode)b.Id).ToArray()),
                ["build_id"] = destinations[ghosts[0].At].Id, ["rev"] = _rev,
            }, null);
        }
    }

    private BuildState? FindEditBuild(BuildState? anchor)
    {
        if (anchor == null) return null;
        if (FreecamBuild(anchor)) return anchor;
        return Group(anchor.Spec) is { } group ? Builds.FirstOrDefault(b => FreecamBuild(b) && Group(b.Spec) == group) : null;
    }

    private BuildState NewEditBuild(BuildState? anchor)
    {
        string group = Group(anchor?.Spec) ?? "freecam-" + Guid.NewGuid().ToString("N");
        if (anchor != null && Group(anchor.Spec) == null)
        {
            anchor.Spec = anchor.Spec != null ? (JsonObject)anchor.Spec.DeepClone() : new JsonObject { ["kind"] = anchor.Kind };
            anchor.Spec["group"] = group;
            anchor.Rev = Touch();
        }
        var spec = new JsonObject { ["kind"] = "block", ["group"] = group, ["freecam_edit"] = true,
            ["replacement"] = true, ["exact_state"] = true, ["name"] = anchor?.Spec?["name"]?.DeepClone() ?? JsonValue.Create("Freecam edits") };
        var build = new BuildState { Id = NextId("b"), Kind = "block", Spec = spec, Created = Now,
            Order = Builds.Count == 0 ? 0 : Builds.Max(b => b.Order) + 1, Status = anchor?.Status == "paused" ? "paused" : "queued", Rev = Touch() };
        Builds.Add(build);
        return build;
    }

    private void RetireEditedCells(HashSet<P3> selected, Dictionary<P3, BuildState> destinations)
    {
        var hits = Builds.SelectMany(b => b.Ghosts.Where(g => selected.Contains(g.At)).Select(g => (Build: b, Ghost: g))).ToList();
        var keys = hits.Select(h => $"{h.Build.Id}|{h.Ghost.At}").ToHashSet();
        var marks = Designations.Where(d => d.At is { } at && selected.Contains(at) && d.Kind is "mine" or "deconstruct" or "cut" or "harvest"
            || d.Kind == "chop" && (d.At is { } root && selected.Contains(root) || d.Blocks?.Any(selected.Contains) == true)).ToList();
        var markIds = marks.Select(d => d.Id).ToHashSet();
        foreach (var (body, job) in _current.ToList())
            if ((job.Source == "ghost" && JobCells(job).Any(keys.Contains) || job.Source == "designation" && markIds.Contains(job.SourceId ?? "")
                || job.At is { } at && selected.Contains(at) || job.DigCells?.Any(selected.Contains) == true
                || job.Takes.Any(t => selected.Contains(t.Chest)) || job.Puts.Any(p => selected.Contains(p.Chest)))
                && Pawns.TryGetValue(body, out var pawn)) Supersede(pawn, "owner edited these build cells");
        if (marks.Count > 0) RemoveDesignations(null, null, null, markIds.ToList());
        foreach (var temp in (Meta.TempBlocks ?? []).Where(t => selected.Contains(t.At)).ToList()) ReleaseReplacedTemporary(temp);
        foreach (var (build, ghost) in hits)
        {
            if (destinations[ghost.At] == build)
            {
                // The same ghost key will get a newer row, not a removal and row in the same delta.
                ForgetCellFailures(build, ghost);
                _leases.Remove($"g:{build.Id}|{ghost.At}");
                build.Ghosts.Remove(ghost);
            }
            else DropGhost(build, ghost);
        }
        foreach (var build in hits.Select(h => h.Build).Distinct())
        {
            if (destinations.Values.Contains(build) || build.Kind == "preserve") continue;
            if (build.Ghosts.Count == 0) RemoveBuild(build);
            else { SaveSparseBuildSpec(build); build.Rev = Touch(); }
        }
    }

    private static void SaveSparseBuildSpec(BuildState build)
    {
        var spec = build.Spec != null ? (JsonObject)build.Spec.DeepClone() : new JsonObject();
        // A mine's original cut footprint also protects its natural walking floors. Sparse cells govern expansion, while
        // area/blocks remain access metadata for MineAccessFloors even after an owner releases a cut above that floor.
        if (build.Kind != "mine") { spec.Remove("area"); spec.Remove("blocks"); }
        foreach (var key in new[] { "place", "replace_area", "material", "height", "level", "roof", "facing" }) spec.Remove(key);
        spec["kind"] = build.Kind;
        spec["cells"] = new JsonArray(build.Ghosts.Select(g => (JsonNode)new JsonArray(g.At.X, g.At.Y, g.At.Z, g.Remove ? 0 : g.Id, g.Remove ? 0 : g.Meta)).ToArray());
        build.Spec = spec;
    }

    private void HoldEditedSnapshots(HashSet<P3> selected)
    {
        foreach (var zone in Zones.Where(z => z.Preserve is { On: true, Data: not null }))
        {
            var snap = zone.Preserve!.Data!;
            bool changed = false;
            foreach (var at in selected)
                if (at.Y >= snap.Lo && at.Y <= snap.Hi && snap.Cols.TryGetValue(ZoneCells.Key(at.X, at.Z), out var column))
                { column[at.Y - snap.Lo] = PreserveSnap.Held; changed = true; }
            if (changed) { zone.Preserve.Changed(); zone.Rev = Touch(); }
        }
    }
}
