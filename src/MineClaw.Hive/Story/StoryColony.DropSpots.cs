using Microsoft.Extensions.Logging;
using MineClaw.World;

namespace MineClaw.Hive.Story;

/// <summary>
/// Drops the pawns can't collect, kept by place (docs/web/story-mode.md "Hauling": "A collect that found nothing"). Live 11-04 story-1
/// (release 20261104-181056): "collect … failed: no reachable drops nearby" ×63 in 6 minutes all over the farm, each in about 0.2 s
/// (the pawn got there and its collect saw nothing it could take). The rest after a miss was keyed by the drop's entity id: a drop that
/// came back with a new id (a new egg or seed at the same spot, a merged stack, a re-drop) was a new target, a batch's other drops never
/// rested, and every drop took three pawns' misses before it rested for the colony, then rested only 30 s, 1 min, 10 min.
/// <list type="bullet">
/// <item>A collect that found nothing marks the spot of each of its drops still in view (any drop within a block of it counts there).
/// <see cref="DropSpotMisses"/> misses there (one when the pawn's own safety checks wrote them off: "; 1 drop(s) left near the void")
/// and the spot rests for everyone, whatever drops lie there: 5 min, then 15, then 30; after a rest one more miss rests it again.</item>
/// <item>Before a drop is offered at all: somewhere to stand by it, by the pawn client's own rule (<see cref="ItemEntityRules.HasPickupSpot"/>:
/// a cell within 2 of it, at, over or under its level, on anything that holds a player up in the version's collision boxes, farmland,
/// slabs and soul sand included, room for the body, the head out of water, a pickup box that touches it). A drop in deep water, in a
/// wall or over the void has none and is never offered.</item>
/// <item>The idle audit says it: "waiting: drops at 6,64,6 unreachable" (the standing task), "no reachable target: haul …: drops there
/// unreachable" (a haul mark).</item>
/// </list>
/// </summary>
public sealed partial class StoryColony
{
    /// <summary>Misses at one spot (by any pawns, of any drops) before it rests for everyone.</summary>
    public const int DropSpotMisses = 3;
    /// <summary>A spot nobody missed at for this long (and not resting) is forgotten.</summary>
    public static readonly TimeSpan DropSpotForget = TimeSpan.FromMinutes(30);

    /// <summary>The n-th rest of a spot: 5 min, 15 min, then 30 min.</summary>
    internal static TimeSpan DropSpotRest(int n) => n switch { <= 1 => TimeSpan.FromMinutes(5), 2 => TimeSpan.FromMinutes(15), _ => TimeSpan.FromMinutes(30) };

    private sealed class DropSpot
    {
        public P3 At;
        public int Misses, Rests;
        public DateTimeOffset Last, Until;
        public string Text = "";
    }

    private readonly List<DropSpot> _dropSpots = new();
    private readonly Dictionary<(long X, long Y, long Z), (bool Ok, DateTimeOffset At)> _standBy = new();

    /// <summary>A drop collect that found nothing: each of its drops still in view counts a miss at its spot.</summary>
    private void NoteDropMiss(PawnState p, ActiveJob j, string why)
    {
        if (!NoDropReached(j, why) || World is not { } w) return;
        _dropSpots.RemoveAll(s => s.Until <= Now && Now - s.Last > DropSpotForget);
        var ents = w.Entities();
        bool writtenOff = why.Contains(';'); // its own safety checks wrote them off: no use trying again soon
        if (DropOf(ents, j.Lease) is { } first) MissAt(p, first.Block, writtenOff);
        foreach (var more in j.MoreLeases)
            if (DropOf(ents, more) is { } e) MissAt(p, e.Block, writtenOff);
    }

    private static StoryEntity? DropOf(IReadOnlyList<StoryEntity> ents, string? lease)
    {
        if (lease == null || !lease.StartsWith("e:", StringComparison.Ordinal) || !int.TryParse(lease.AsSpan(2), out var id)) return null;
        foreach (var e in ents) if (e.Id == id) return e;
        return null;
    }

    private void MissAt(PawnState p, P3 at, bool writtenOff)
    {
        var spot = SpotNear(at, resting: false) ?? AddSpot(at);
        spot.Misses++;
        spot.Last = Now;
        if (spot.Until > Now || !writtenOff && spot.Misses < (spot.Rests == 0 ? DropSpotMisses : 1)) return;
        spot.Rests++;
        spot.Misses = 0;
        spot.Until = Now + DropSpotRest(spot.Rests);
        _log.LogInformation("story {Colony}: drops at {At} unreachable ({Pawn} the last to miss them): left for {Min} min", Id, spot.At, p.Name,
            (int)DropSpotRest(spot.Rests).TotalMinutes);
    }

    private DropSpot AddSpot(P3 at)
    {
        var s = new DropSpot { At = at, Text = $"drops at {at} unreachable" };
        if (_dropSpots.Count >= 256) _dropSpots.RemoveAt(0);
        _dropSpots.Add(s);
        return s;
    }

    /// <summary>The spot within a block of <paramref name="at"/> (only a resting one with <paramref name="resting"/>), or null.</summary>
    private DropSpot? SpotNear(P3 at, bool resting)
    {
        foreach (var s in _dropSpots)
            if ((!resting || s.Until > Now) && Math.Abs(s.At.X - at.X) <= 1 && Math.Abs(s.At.Y - at.Y) <= 1 && Math.Abs(s.At.Z - at.Z) <= 1) return s;
        return null;
    }

    /// <summary>
    /// Why a drop is left alone now: its spot rests after misses, or there is nowhere to stand by it (null = it may be offered).
    /// </summary>
    private string? DropUnreachable(IStoryWorld w, StoryEntity e)
    {
        if (_dropSpots.Count > 0 && SpotNear(e.Block, resting: true) is { } spot) return spot.Text;
        return StandBy(w, e) ? null : $"drops at {e.Block} unreachable";
    }

    /// <summary>Somewhere to pick the drop up from (cached a few seconds per spot, to a sixteenth of a block): the client's own rule
    /// (<see cref="ItemEntityRules.HasPickupSpot"/>: a cell whose body, standing on whatever holds it up in the version's collision boxes,
    /// touches the drop with its head out of water). Live 11-04: the colony offered seeds on 1.12.2 farmland (15/16 high) that the
    /// pawn's client, counting only full blocks as ground, wrote off "near the void"; both now ask the same World rule.</summary>
    private bool StandBy(IStoryWorld w, StoryEntity e)
    {
        var c = e.Block;
        if (!w.Loaded(c.X, c.Z)) return true; // can't tell: the pawn's collect will
        var key = (Q(e.X), Q(e.Y), Q(e.Z));
        if (_standBy.TryGetValue(key, out var hit) && Now - hit.At < TimeSpan.FromSeconds(5)) return hit.Ok;
        if (_standBy.Count > 1024) _standBy.Clear();
        bool ok = ItemEntityRules.HasPickupSpot(AccessOf(w), new Vec3d(e.X, e.Y, e.Z));
        _standBy[key] = (ok, Now);
        return ok;
    }

    private static long Q(double v) => (long)Math.Floor(v * 16);

    private StoryBlockAccess? _access;

    /// <summary>The colony's view as the World package reads blocks (one adapter per view).</summary>
    private StoryBlockAccess AccessOf(IStoryWorld w) =>
        _access is { } a && ReferenceEquals(a.World, w) ? a : _access = new StoryBlockAccess(w);
}
