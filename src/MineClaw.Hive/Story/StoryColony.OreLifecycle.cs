using MineClaw.World;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private bool OreParentExists(OreWorkOrigin o) => o.Source switch
    {
        "build" => Builds.Any(b => b.Id == o.Id && b.Kind == "mine"),
        "zone" => Zones.Any(z => z.Id == o.Id && z.Kind == "mine"),
        "designation" => true,
        _ => false,
    };

    private void OreTick()
    {
        StopInvalidOreJobs();
        foreach (var d in Designations.Where(d => d.OreOrigin != null).ToList())
        {
            bool changed = d.At is { } at && World is { } view && view.Loaded(at.X, at.Z)
                && Now - d.Created > TimeSpan.FromSeconds(10) && !_leases.ContainsKey("b:" + at)
                && OreIdentity.Canonical(view.Block(at.X, at.Y, at.Z).Id, Modern) != d.Ore;
            if (OreParentExists(d.OreOrigin!) && !changed) continue;
            Designations.Remove(d);
            Removed("designation", d.Id);
        }
        if (Meta.OreFrontiers is not { Count: > 0 } frontiers || World is not { } w) return;
        foreach (var edge in frontiers.ToList())
        {
            if (OreParentExists(edge.Origin) && (!OreParentActive(edge.Origin) || !w.Loaded(edge.At.X, edge.At.Z))) continue;
            frontiers.Remove(edge);
            Touch();
            if (OreParentActive(edge.Origin)) ExposedOre(w, edge);
        }
    }

    private void StopInvalidOreJobs()
    {
        foreach (var (body, j) in _current.ToList())
        {
            if (j.OreOrigin is not { } origin || !Pawns.TryGetValue(body, out var p)) continue;
            if (OreParentActive(origin) && j.DigDesignations.Keys.All(at => !OreAccessProtected(at)
                && Meta.CancelledOre?.Contains(at) != true)) continue;
            Supersede(p, origin.Source == "zone" ? "quarry changed" : "mining instruction changed");
            p.Rev = Bump();
        }
    }

    // Cancelling a manual seed also cancels the exposed continuation. Cancelling one ore cell prevents just that extension,
    // and cuts its running batch before the next atomic action. Other authorized blueprint/quarry work remains available.
    private void CancelOreMarks(List<DesignationState> gone, string? kind, Area? area, IReadOnlyList<P3>? blocks)
    {
        var parents = gone.Where(d => d.Kind == "mine" && d.OreOrigin == null).Select(d => d.Id).ToHashSet();
        gone.AddRange(Designations.Where(d => d.OreOrigin is { Source: "designation" } o && parents.Contains(o.Id) && !gone.Contains(d)));
        foreach (var d in gone.Where(d => d.OreOrigin != null && d.At != null))
        {
            Meta.CancelledOre ??= [];
            if (!Meta.CancelledOre.Contains(d.At!.Value)) Meta.CancelledOre.Add(d.At.Value);
        }
        if (Meta.OreFrontiers is { } edges)
            foreach (var e in edges.Where(e => e.Origin.Source == "designation" && parents.Contains(e.Origin.Id)
                || kind is null or "mine" && (area?.Contains(e.At) == true || blocks?.Contains(e.At) == true)).ToList())
            {
                Meta.CancelledOre ??= [];
                if (!Meta.CancelledOre.Contains(e.At)) Meta.CancelledOre.Add(e.At);
                edges.Remove(e);
                Touch();
            }
        if (gone.Any(d => d.OreOrigin != null) || parents.Count > 0) Touch();
    }
}
