using System.Text.Json.Nodes;

namespace MineClaw.Hive.Story;

public sealed partial class StoryColony
{
    private readonly HashSet<string> _preparedChestRemovals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _chestRemovalLookAfter = new(StringComparer.Ordinal);

    private bool ChestDeconstructing(P3 at) => Designations.Any(d => !d.Auto && d.Kind == "deconstruct"
        && d.At is { } cell && (cell == at || DoubleHalf(at) == cell
            || StorageAt(at) is { } s && (cell == s.Chest || cell == s.DoubleWith)));

    private static bool ManualChestRemoval(DesignationState d, IStoryWorld w, P3 at) =>
        !d.Auto && d.Kind == "deconstruct" && w.Loaded(at.X, at.Z) && w.Block(at.X, at.Y, at.Z).Id is 54 or 146;

    /// <summary>Explicitly dismantling an existing chest retires only older matching chest ownership at that exact cell.
    /// This also upgrades old marks that were stuck on the generic "a chest may hold items" protection refusal.</summary>
    private void ReconcileChestDeconstruction(IStoryWorld w)
    {
        foreach (var d in Designations.Where(d => d.At is { } at && ManualChestRemoval(d, w, at)).ToList())
        {
            var at = d.At!.Value;
            if (!_preparedChestRemovals.Add(d.Id)) continue;
            // A player may have just joined a new half onto an already registered single chest.
            // Refresh that record before exclusion/cancellation or registering a duplicate storage.
            foreach (var s in Storages.Where(s => s.Chest == at || s.DoubleWith == at || DoubleHalf(s.Chest, w) == at).ToList())
                ReconcileStorage(s, w);
            int id = w.Block(at.X, at.Y, at.Z).Id;
            var hit = Builds.Where(b => b.Created <= d.Created && b.Kind != "preserve")
                .SelectMany(b => b.Ghosts.Where(g => !g.Remove && g.At == at && g.Id == id).Select(g => (B: b, G: g))).ToList();
            var keys = hit.Select(h => $"{h.B.Id}|{h.G.At}").ToHashSet();
            foreach (var (pawn, job) in _current.ToList())
                if ((job.Source == "ghost" && JobCells(job).Any(keys.Contains) || job.Puts.Any(put => ChestDeconstructing(put.Chest)))
                    && Pawns.TryGetValue(pawn, out var p)) Supersede(p, "chest marked for deconstruction");
            foreach (var (b, g) in hit) DropGhost(b, g);
            foreach (var b in hit.Select(h => h.B).Distinct()) if (b.Ghosts.Count == 0) RemoveBuild(b);
            if (d.Reason?.Contains("protected", StringComparison.OrdinalIgnoreCase) == true)
            {
                Reopen(d);
                d.Fails = 0;
                d.Reason = null;
            }
            _resort = null;
            MaterialsChanged();
        }
    }

    /// <summary>Inspect, then move contents through the existing transport ledger. Only an empty chest reaches the
    /// client-side fresh-window check. No room elsewhere means waiting, never dumping its contents on the ground.</summary>
    private (bool Ready, ActiveJob? Job) PrepareChestRemoval(PawnState pawn, PawnLive live, DesignationState mark, P3 at)
    {
        if (World is not { } world) return (false, null);
        ReconcileChestDeconstruction(world);
        if (StorageAt(at) == null) Set(at, null, null, null);
        var source = StorageAt(at)!;
        RefreshSeen([source], force: true);
        if (!ChestOpen(source.Chest)) { NoteWait(mark, "the chest must be accessible before it can be emptied"); return (false, null); }
        if (_current.Values.Any(j => j.Takes.Any(t => t.Chest == source.Chest || t.Chest == source.DoubleWith)
            || j.Puts.Any(p => p.Chest == source.Chest || p.Chest == source.DoubleWith)))
        {
            NoteWait(mark, "waiting for the chest's current transfer");
            return (false, null);
        }
        if (source.SeenAt is not { } seen || seen < mark.Created
            || _chestRemovalLookAfter.TryGetValue(mark.Id, out var after) && seen <= after)
        {
            var key = "look:" + source.Chest;
            if (!Open(key, pawn.Name, null)) return (false, null);
            NoteWait(mark, "checking the chest before deconstruction");
            var look = Job(WorkTypes.Build, "look", "checking the chest before deconstruction", source.Chest, key,
                "look", source.Chest.ToString(), "working", j => j["deconstruct_id"] = mark.Id, xp: 2);
            look.MoreLeases.Add(DesignationKey(mark));
            return (false, look);
        }
        if (source.Contents.Count == 0) { NoteWait(mark, null); return (true, null); }
        if (live.FreeSlots <= 0) { NoteWait(mark, "needs room to carry the chest's contents"); return (false, null); }
        var moves = new List<(StorageState Src, Stack St, StorageState? Dst, bool Fallback)>();
        foreach (var stack in source.Contents)
        {
            var destination = BestChest(stack.Id, stack.Meta, source.Chest, source)
                ?? (Meta.UnfilteredTakesAnything ? Fallback(stack.Id, stack.Meta, source.Chest, source) : null);
            if (destination == null) continue;
            int max = MaxStack(stack.Id);
            int room = destination.FreeSlots * max + destination.Contents.Where(s => s.Id == stack.Id && s.Meta == stack.Meta && s.Count % max != 0)
                .Sum(s => max - s.Count % max);
            int count = Math.Min(stack.Count, Math.Min(max, room));
            if (count <= 0) continue;
            // One bounded stack per evacuation trip cannot overbook the destination's observed slots.
            moves.Add((source, stack with { Count = count }, destination, !Accepts(destination, stack.Id, stack.Meta)));
            break;
        }
        if (moves.Count == 0) { NoteWait(mark, "needs another chest with room for these contents"); return (false, null); }
        var move = ResortTrip(pawn, live, source, moves, WorkTypes.Build);
        if (move != null)
        {
            move.Json["deconstruct_id"] = mark.Id;
            move.MoreLeases.Add(DesignationKey(mark));
            NoteWait(mark, "moving the chest's contents to other storage");
        }
        return (false, move);
    }

    private bool ChestRemovalWait(ActiveJob job, string? note)
    {
        if (job.Json["deconstruct"]?.GetValue<bool>() != true
            || note?.Contains("chest deconstruction waiting:", StringComparison.OrdinalIgnoreCase) != true) return false;
        if (job.At is { } at && StorageAt(at) is { } storage)
        {
            RefreshSeen([storage], force: true);
            storage.SeenAt = null; // a new deposit or changed double-chest layout requires another look
        }
        if (Designations.FirstOrDefault(d => d.Id == job.SourceId) is { } mark)
        {
            _chestRemovalLookAfter[mark.Id] = Now;
            NoteWait(mark, Short(note));
        }
        if (job.Lease is { } key) _skip[key] = Now + TimeSpan.FromSeconds(2);
        GearTakesEnded(job, done: false);
        return true;
    }
}
