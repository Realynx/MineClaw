using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    /// <summary>Release selected cells from every build and preserve snapshot without changing the blocks or the rest of a zone.</summary>
    public (JsonNode? Result, string? Error) UnprotectCells(IReadOnlyList<P3>? cells)
    {
        if (cells is not { Count: > 0 and <= 4096 }) return (null, "cells required (1..4096)");
        lock (_lock)
        {
            var selected = cells.ToHashSet();
            var hits = Builds.SelectMany(b => b.Ghosts.Where(g => selected.Contains(g.At)).Select(g => (Build: b, Ghost: g))).ToList();
            var keys = hits.Select(h => $"{h.Build.Id}|{h.Ghost.At}").ToHashSet();
            foreach (var (pawn, job) in _current.ToList())
                if (job.Source == "ghost" && JobCells(job).Any(keys.Contains) && Pawns.TryGetValue(pawn, out var p))
                    Supersede(p, "owner stopped rebuilding these blocks");
            foreach (var (b, g) in hits) DropGhost(b, g);
            foreach (var b in hits.Select(h => h.Build).Distinct())
                if (b.Ghosts.Count == 0 && b.Kind != "preserve") RemoveBuild(b);
            int preserved = 0;
            foreach (var z in Zones.Where(z => z.Preserve is { On: true, Data: not null }))
            {
                var snap = z.Preserve!.Data!;
                bool changed = false;
                foreach (var at in selected)
                    if (at.Y >= snap.Lo && at.Y <= snap.Hi && snap.Cols.TryGetValue(ZoneCells.Key(at.X, at.Z), out var col))
                    {
                        col[at.Y - snap.Lo] = PreserveSnap.Unprotected;
                        changed = true;
                        preserved++;
                    }
                if (changed) { z.Preserve.Changed(); z.Rev = Touch(); }
            }
            MaterialsChanged();
            WakeIdle();
            return (new JsonObject { ["cells"] = selected.Count,
                ["build_cells"] = hits.Count, ["preserved_cells"] = preserved }, null);
        }
    }
}
