namespace MineClaw.Hive.Story;

/// <summary>
/// Designations on entities (haul, hunt, forbid) follow their entity (docs/web/story-mode.md "Designations"). Fox 11-04: "When I have
/// items selected for hauling and they get hauled to a chest or into an inventory, the hauling icons don't go away, but the items do."
/// A collect trip picked up designated drops as standing drops (or with its radius) and only its own job's designation, if any, was
/// done; nothing noticed a drop picked up by a player, merged into a neighbouring stack or despawned.
/// <list type="bullet">
/// <item>A collect job done completes the haul designation of every drop it leased (its batch included).</item>
/// <item>Every few seconds, a designation whose entity the colony's view hasn't shown for <see cref="EntityGone"/> (while the
/// column where it lay is in view, and no pawn's job holds it) goes, with a removal in the state delta; a drop that merged into a
/// stack of the same item right by it passes its designation on to that stack (unless that one is designated already).</item>
/// </list>
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>How long a designated entity may be out of the view (its column in view) before its designation goes.</summary>
    public static readonly TimeSpan EntityGone = TimeSpan.FromSeconds(8);

    private readonly Dictionary<int, (P3? At, int Item, DateTimeOffset Seen)> _markSeen = new();
    private DateTimeOffset _markPrune = DateTimeOffset.MinValue;

    private static bool IsEntityMark(string kind) => kind is "haul" or "hunt" or "forbid";

    /// <summary>A collect job done: the haul designations of the drops it leased are done too.</summary>
    private void HauledDone(ActiveJob j)
    {
        if (j.Kind != "collect") return;
        var ids = j.MoreLeases.Append(j.Lease ?? "").Where(k => k.StartsWith("e:", StringComparison.Ordinal))
            .Select(k => int.TryParse(k.AsSpan(2), out var id) ? id : -1).Where(id => id >= 0).ToHashSet();
        if (ids.Count == 0) return;
        foreach (var d in Designations.Where(d => d.Kind == "haul" && d.Entity is { } e && ids.Contains(e)).ToList())
            DropMark(d);
    }

    private void DropMark(DesignationState d)
    {
        Designations.Remove(d);
        Removed("designation", d.Id);
        if (d.Entity is { } e) _markSeen.Remove(e);
    }

    /// <summary>Every 3 s (the tick): designations whose entity is gone go; a merged drop's designation follows the stack it joined.</summary>
    private void PruneEntityMarks()
    {
        if (Now - _markPrune < TimeSpan.FromSeconds(3) || World is not { } w) return;
        _markPrune = Now;
        var marks = Designations.Where(d => d.Entity != null && IsEntityMark(d.Kind)).ToList();
        if (marks.Count == 0) { _markSeen.Clear(); return; }
        var ents = w.Entities();
        var byId = new Dictionary<int, StoryEntity>();
        foreach (var e in ents) byId.TryAdd(e.Id, e);
        var held = _current.Values.SelectMany(j => j.MoreLeases.Append(j.Lease ?? "")).ToHashSet(StringComparer.Ordinal);
        var marked = marks.Select(d => d.Entity!.Value).ToHashSet();
        foreach (var d in marks)
        {
            int id = d.Entity!.Value;
            if (byId.TryGetValue(id, out var e)) { _markSeen[id] = (e.Block, e.ItemId, Now); continue; }
            if (held.Contains("e:" + id)) continue; // a pawn is at it: its job's end decides
            if (!_markSeen.TryGetValue(id, out var last)) { _markSeen[id] = (d.At, -1, Now); continue; } // first miss: the clock starts
            if (last.At is { } at && !w.Loaded(at.X, at.Z)) { _markSeen[id] = last with { Seen = Now }; continue; } // out of every body's sight
            // Merged into a stack of the same item right by it: the designation follows the stack.
            if (d.Kind != "hunt" && last.At is { } la && last.Item >= 0
                && ents.Where(x => x.Kind == "item" && x.ItemId == last.Item && x.Id != id && Math.Abs(x.X - (la.X + 0.5)) <= 1.5 && Math.Abs(x.Z - (la.Z + 0.5)) <= 1.5
                        && Math.Abs(x.Y - la.Y) <= 1.5).OrderBy(x => Math.Abs(x.X - (la.X + 0.5)) + Math.Abs(x.Z - (la.Z + 0.5))).FirstOrDefault() is { } into)
            {
                _markSeen.Remove(id);
                if (marked.Contains(into.Id)) { DropMark(d); continue; }
                d.Entity = into.Id;
                d.Rev = Touch();
                marked.Add(into.Id);
                _markSeen[into.Id] = (into.Block, into.ItemId, Now);
                continue;
            }
            if (Now - last.Seen < EntityGone) continue;
            DropMark(d); // picked up (by a pawn, a player, another bot), killed, despawned
        }
        foreach (var k in _markSeen.Keys.Where(k => !marked.Contains(k)).ToList()) _markSeen.Remove(k);
    }
}
